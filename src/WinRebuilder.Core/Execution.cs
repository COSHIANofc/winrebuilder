using System.Security.Cryptography;
using System.Text;

namespace WinRebuilder.Core;

public enum OperationType { Package, Registry }
public enum Outcome { Install, Change, Skip, Fail, Warning }
public sealed record PlannedOperation(string Id, OperationType Type, Phase Phase, PackageSpec? Package, RegistrySpec? Registry);
public sealed record ExecutionPlan(string ProfileHash, IReadOnlyList<PlannedOperation> Operations);
public sealed record OperationResult(string OperationId, OperationType Type, Outcome Outcome, DateTimeOffset Timestamp, string Message);
public sealed record RegistryValue(bool Exists, RegistryKind? Kind, string? Value);
public sealed record RegistryBackup(string OperationId, string Path, string Name, RegistryValue Previous, DateTimeOffset Timestamp);
public sealed record ExecutionState(string ProfileHash, string ApplicationVersion,
    Dictionary<string, DateTimeOffset> Completed, Dictionary<string, DateTimeOffset> Failed);
public sealed record LogEntry(DateTimeOffset Timestamp, string OperationId, OperationType Type, Outcome Outcome, string Message);

public interface IRegistryAccess
{
    Task<RegistryValue> ReadAsync(string path, string name, CancellationToken ct);
    Task WriteAsync(string path, string name, RegistryKind kind, string value, CancellationToken ct);
}
public interface IRegistryBackupStore { Task SaveAsync(RegistryBackup backup, CancellationToken ct); }
public interface IPackageProvider
{
    PackageProvider Kind { get; }
    Task<bool> IsInstalledAsync(PackageSpec package, CancellationToken ct);
    Task InstallAsync(PackageSpec package, CancellationToken ct);
}
public interface IProcessRunner { Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct); }
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated = false);
public interface IDownloads
{
    Task<string> DownloadVerifiedAsync(Uri url, string sha256, CancellationToken ct);
    Task<string> DownloadTemporaryAsync(Uri url, CancellationToken ct);
}
public interface IExecutionStateStore
{
    Task<ExecutionState?> LoadAsync(string profileHash, CancellationToken ct);
    Task SaveAsync(ExecutionState state, CancellationToken ct);
}
public interface IOperationLogger { void Log(LogEntry entry); }

public static class Planner
{
    public static ExecutionPlan Create(LoadedProfile loaded)
    {
        var result = new List<PlannedOperation>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in loaded.Profile.Packages)
        {
            var identity = $"package|{p.Provider}|{p.Id}|{p.Repository}|{p.Asset}|{p.Url}|{p.Sha256}|{p.Installer}|{p.SilentMode}|{p.UninstallDisplayName}|{p.Phase}";
            Add(new PlannedOperation(Id(identity), OperationType.Package, p.Phase, p, null));
        }
        foreach (var r in loaded.Profile.Registry)
        {
            var identity = $"registry|{r.Path.ToUpperInvariant()}|{r.Name.ToUpperInvariant()}|{r.Kind}|{r.Value}";
            Add(new PlannedOperation(Id(identity), OperationType.Registry, Phase.Normal, null, r));
        }
        return new ExecutionPlan(loaded.Sha256, result.OrderBy(x => x.Phase).ThenBy(x => x.Type).ToArray());
        void Add(PlannedOperation op)
        {
            if (!ids.Add(op.Id)) throw new FormatException("Duplicate operation in profile.");
            result.Add(op);
        }
    }
    private static string Id(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
}

public sealed class Executor
{
    private readonly IReadOnlyDictionary<PackageProvider, IPackageProvider> providers;
    private readonly IRegistryAccess registry;
    private readonly IRegistryBackupStore backups;
    private readonly IExecutionStateStore states;
    private readonly IOperationLogger logger;
    private readonly TimeProvider clock;
    private readonly string appVersion;

    public Executor(IEnumerable<IPackageProvider> providers, IRegistryAccess registry, IRegistryBackupStore backups,
        IExecutionStateStore states, IOperationLogger logger, string appVersion, TimeProvider? clock = null)
    {
        this.providers = providers.ToDictionary(x => x.Kind);
        this.registry = registry; this.backups = backups; this.states = states; this.logger = logger;
        this.appVersion = appVersion; this.clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<OperationResult>> RunAsync(ExecutionPlan plan, bool dryRun, CancellationToken ct = default)
    {
        var state = dryRun ? null : await states.LoadAsync(plan.ProfileHash, ct);
        state ??= new ExecutionState(plan.ProfileHash, appVersion, new(), new());
        var results = new List<OperationResult>();
        foreach (var op in plan.Operations)
        {
            ct.ThrowIfCancellationRequested();
            OperationResult result;
            try
            {
                result = op.Type == OperationType.Package
                    ? await PackageAsync(op, dryRun, ct)
                    : await RegistryAsync(op, dryRun, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                // Provider errors are curated; unexpected exception messages can contain remote URLs or profile data.
                var message = e is InvalidOperationException ? e.Message : $"{e.GetType().Name} during operation.";
                result = new OperationResult(op.Id, op.Type, Outcome.Fail, clock.GetUtcNow(), message);
            }
            results.Add(result);
            logger.Log(new LogEntry(result.Timestamp, op.Id, op.Type, result.Outcome, result.Message));
            if (!dryRun)
            {
                if (result.Outcome == Outcome.Fail)
                {
                    state.Failed[op.Id] = result.Timestamp;
                    state.Completed.Remove(op.Id);
                }
                else { state.Completed[op.Id] = result.Timestamp; state.Failed.Remove(op.Id); }
                await states.SaveAsync(state, ct);
            }
            if (result.Outcome == Outcome.Fail) break;
        }
        return results;
    }

    private async Task<OperationResult> PackageAsync(PlannedOperation op, bool dryRun, CancellationToken ct)
    {
        var package = op.Package!;
        if (!providers.TryGetValue(package.Provider, out var provider)) throw new InvalidOperationException("Provider unavailable.");
        if (await provider.IsInstalledAsync(package, ct))
            return Result(Outcome.Skip, package.Provider == PackageProvider.Winget ? $"Already installed: {package.Id}." : "Already installed.");
        if (!dryRun)
        {
            await provider.InstallAsync(package, ct);
            if (!await provider.IsInstalledAsync(package, ct))
                throw new InvalidOperationException("Installer completed but installed-package detection did not confirm the result.");
        }
        var description = package.Provider == PackageProvider.Winget ? $"winget package: {package.Id}." : "Package.";
        return Result(Outcome.Install, dryRun ? $"Would install {description}" : $"Installed {description}");
        OperationResult Result(Outcome outcome, string message) => new(op.Id, op.Type, outcome, clock.GetUtcNow(), message);
    }

    private async Task<OperationResult> RegistryAsync(PlannedOperation op, bool dryRun, CancellationToken ct)
    {
        var target = op.Registry!;
        var old = await registry.ReadAsync(target.Path, target.Name, ct);
        if (old.Exists && old.Kind == target.Kind && old.Value == target.Value)
            return Result(Outcome.Skip, "Value already matches.");
        if (!dryRun)
        {
            await backups.SaveAsync(new RegistryBackup(op.Id, target.Path, target.Name, old, clock.GetUtcNow()), ct);
            await registry.WriteAsync(target.Path, target.Name, target.Kind, target.Value, ct);
            var actual = await registry.ReadAsync(target.Path, target.Name, ct);
            if (!actual.Exists || actual.Kind != target.Kind || actual.Value != target.Value)
                throw new InvalidOperationException("Registry verification failed; recovery backup was preserved.");
        }
        return Result(Outcome.Change, dryRun ? "Would change registry value." : "Registry value changed and verified.");
        OperationResult Result(Outcome outcome, string message) => new(op.Id, op.Type, outcome, clock.GetUtcNow(), message);
    }
}
