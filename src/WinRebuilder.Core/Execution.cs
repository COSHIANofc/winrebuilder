using System.Security.Cryptography;
using System.Text;

namespace WinRebuilder.Core;

public enum OperationType { Package, Registry, ExplorerPatcherSetting }
public enum Outcome { Install, Change, Restore, Skip, Fail, Warning }
public sealed record PlannedOperation(string Id, OperationType Type, Phase Phase, PackageSpec? Package, RegistrySpec? Registry,
    ExplorerSetting? ExplorerSetting = null);
public sealed record ExecutionPlan(string ProfileHash, IReadOnlyList<PlannedOperation> Operations);
public sealed record OperationResult(string OperationId, OperationType Type, Outcome Outcome, DateTimeOffset Timestamp, string Message);
public sealed record ExecutionState(string ProfileHash, string ApplicationVersion,
    Dictionary<string, DateTimeOffset> Completed, Dictionary<string, DateTimeOffset> Failed);
public sealed record LogEntry(DateTimeOffset Timestamp, string OperationId, OperationType Type, Outcome Outcome, string Message);

public interface IRegistryAccess
{
    Task<RegistryValue> ReadAsync(string path, string name, CancellationToken ct);
    Task WriteAsync(string path, string name, RegistryValue value, CancellationToken ct);
    Task DeleteValueAsync(string path, string name, CancellationToken ct);
}
public interface IExplorerRegistryAccess : IRegistryAccess { }
public interface IRegistryBackupStore
{
    Task SaveAsync(RegistryBackup backup, CancellationToken ct);
    Task<RegistryBackup> LoadAsync(string backupId, CancellationToken ct);
    Task<IReadOnlyList<RegistryBackup>> ListAsync(CancellationToken ct);
}
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
        if (loaded.Profile.ExplorerPatcher?.Enabled == true)
        {
            if (loaded.ExplorerSettings is null) throw new FormatException("ExplorerPatcher settings file was not loaded.");
            foreach (var setting in loaded.ExplorerSettings)
            {
                var target = setting.Target;
                var identity = $"explorer-setting|{setting.Path.ToUpperInvariant()}|{setting.Name.ToUpperInvariant()}|{target.Exists}|{target.Kind}|{target.DWord}|{target.Text}";
                Add(new PlannedOperation(Id(identity), OperationType.ExplorerPatcherSetting, Phase.Shell, null, null, setting));
            }
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
    private readonly IExplorerRegistryAccess? explorerRegistry;
    private readonly TimeProvider clock;
    private readonly string appVersion;

    public Executor(IEnumerable<IPackageProvider> providers, IRegistryAccess registry, IRegistryBackupStore backups,
        IExecutionStateStore states, IOperationLogger logger, string appVersion, TimeProvider? clock = null,
        IExplorerRegistryAccess? explorerRegistry = null)
    {
        this.providers = providers.ToDictionary(x => x.Kind);
        this.registry = registry; this.backups = backups; this.states = states; this.logger = logger;
        this.appVersion = appVersion; this.clock = clock ?? TimeProvider.System;
        this.explorerRegistry = explorerRegistry;
    }

    public async Task<IReadOnlyList<OperationResult>> RunAsync(ExecutionPlan plan, bool dryRun, CancellationToken ct = default)
    {
        var state = dryRun ? null : await states.LoadAsync(plan.ProfileHash, ct);
        state ??= new ExecutionState(plan.ProfileHash, appVersion, new(), new());
        var results = new List<OperationResult>();
        Dictionary<string, (RegistryValue Previous, RegistryBackup? Backup)>? preparedExplorer = null;
        foreach (var op in plan.Operations)
        {
            ct.ThrowIfCancellationRequested();
            OperationResult result;
            try
            {
                if (op.Type == OperationType.ExplorerPatcherSetting && !dryRun && preparedExplorer is null)
                    preparedExplorer = await PrepareExplorerAsync(plan, ct);
                result = op.Type switch
                {
                    OperationType.Package => await PackageAsync(op, dryRun, ct),
                    OperationType.Registry => await RegistryAsync(op, plan.ProfileHash, dryRun, ct),
                    OperationType.ExplorerPatcherSetting => await ExplorerSettingAsync(op, plan.ProfileHash, dryRun, preparedExplorer, ct),
                    _ => throw new InvalidOperationException("Unknown operation type.")
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                // Provider errors are curated; unexpected exception messages can contain remote URLs or profile data.
                var message = e is InvalidOperationException ? e.Message : $"{e.GetType().Name} during operation.";
                if (op.Registry is { } registryTarget)
                    message = $"{registryTarget.Path}\\{registryTarget.Name}: {message}";
                if (op.ExplorerSetting is { } explorerTarget)
                    message = $"{explorerTarget.Path}\\{explorerTarget.Name}: {message}";
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

    private async Task<OperationResult> RegistryAsync(PlannedOperation op, string profileHash, bool dryRun, CancellationToken ct)
    {
        var target = op.Registry!;
        return await RegistryChangeAsync(op, profileHash, target.Path, target.Name, RegistryValue.ForTarget(target),
            RegistryScope.Generic, registry, dryRun, ct);
    }

    private async Task<OperationResult> ExplorerSettingAsync(PlannedOperation op, string profileHash, bool dryRun,
        Dictionary<string, (RegistryValue Previous, RegistryBackup? Backup)>? prepared, CancellationToken ct)
    {
        var target = op.ExplorerSetting!;
        if (explorerRegistry is null) throw new InvalidOperationException("ExplorerPatcher registry adapter unavailable.");
        if (dryRun)
            return await RegistryChangeAsync(op, profileHash, target.Path, target.Name, target.Target,
                RegistryScope.ExplorerPatcher, explorerRegistry, true, ct);
        var (previous, backup) = prepared![op.Id];
        if (backup is null)
        {
            if (await explorerRegistry.ReadAsync(target.Path, target.Name, ct) != previous)
                throw new InvalidOperationException("ExplorerPatcher registry state changed after inspection; no write was made.");
            return new OperationResult(op.Id, op.Type, Outcome.Skip, clock.GetUtcNow(), $"{target.Path}\\{target.Name}: value already matches.");
        }
        try
        {
            if (await explorerRegistry.ReadAsync(target.Path, target.Name, ct) != previous)
                throw new InvalidOperationException("Registry value changed after backup; no write was made.");
            if (target.Target.Exists) await explorerRegistry.WriteAsync(target.Path, target.Name, target.Target, ct);
            else await explorerRegistry.DeleteValueAsync(target.Path, target.Name, ct);
            if (await explorerRegistry.ReadAsync(target.Path, target.Name, ct) != target.Target)
                throw new InvalidOperationException("Registry verification failed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Registry change failed for {target.Path}\\{target.Name}; backup {backup.BackupId} was preserved.", e);
        }
        return new OperationResult(op.Id, op.Type, Outcome.Change, clock.GetUtcNow(),
            $"Changed {target.Path}\\{target.Name}; verified. Backup: {backup.BackupId}.");
    }

    private async Task<Dictionary<string, (RegistryValue Previous, RegistryBackup? Backup)>> PrepareExplorerAsync(
        ExecutionPlan plan, CancellationToken ct)
    {
        if (explorerRegistry is null) throw new InvalidOperationException("ExplorerPatcher registry adapter unavailable.");
        var prepared = new Dictionary<string, (RegistryValue Previous, RegistryBackup? Backup)>(StringComparer.Ordinal);
        foreach (var op in plan.Operations.Where(x => x.Type == OperationType.ExplorerPatcherSetting))
        {
            var setting = op.ExplorerSetting!;
            ExplorerRegistryPolicy.ValidateValue(setting.Path, setting.Name);
            setting.Target.Validate();
            var previous = await explorerRegistry.ReadAsync(setting.Path, setting.Name, ct);
            prepared.Add(op.Id, (previous, null));
        }
        foreach (var op in plan.Operations.Where(x => x.Type == OperationType.ExplorerPatcherSetting))
        {
            var setting = op.ExplorerSetting!;
            var previous = prepared[op.Id].Previous;
            if (previous == setting.Target) continue;
            var location = ExplorerRegistryPolicy.Parse(setting.Path);
            var backup = new RegistryBackup(1, RegistryBackupId.Create(), plan.ProfileHash, appVersion, op.Id,
                clock.GetUtcNow(), location.Hive, location.KeyPath, RegistryViewKind.Registry64,
                setting.Name, previous, setting.Target, RegistryScope.ExplorerPatcher);
            backup.Validate();
            await backups.SaveAsync(backup, ct);
            if (await backups.LoadAsync(backup.BackupId, ct) != backup)
                throw new InvalidOperationException($"Backup {backup.BackupId} could not be verified; registry was not modified.");
            prepared[op.Id] = (previous, backup);
        }
        foreach (var op in plan.Operations.Where(x => x.Type == OperationType.ExplorerPatcherSetting))
        {
            var setting = op.ExplorerSetting!;
            if (await explorerRegistry.ReadAsync(setting.Path, setting.Name, ct) != prepared[op.Id].Previous)
                throw new InvalidOperationException("ExplorerPatcher registry state changed after backup; no write was made.");
        }
        return prepared;
    }

    private async Task<OperationResult> RegistryChangeAsync(PlannedOperation op, string profileHash, string path, string name,
        RegistryValue desired, RegistryScope scope, IRegistryAccess access, bool dryRun, CancellationToken ct)
    {
        var old = await access.ReadAsync(path, name, ct);
        if (old == desired)
            return Result(Outcome.Skip, $"{path}\\{name}: value already matches.");
        string? backupId = null;
        if (!dryRun)
        {
            var location = scope == RegistryScope.Generic ? RegistryPathPolicy.Parse(path) : ExplorerRegistryPolicy.Parse(path);
            var backup = new RegistryBackup(1, RegistryBackupId.Create(), profileHash, appVersion, op.Id,
                clock.GetUtcNow(), location.Hive, location.KeyPath, RegistryViewKind.Registry64, name, old, desired, scope);
            backup.Validate();
            await backups.SaveAsync(backup, ct);
            backupId = backup.BackupId;
            try
            {
                var persisted = await backups.LoadAsync(backupId, ct);
                if (persisted != backup) throw new InvalidOperationException("Backup content changed during persistence.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Backup {backupId} could not be verified; registry was not modified.", e);
            }
            try
            {
                var beforeWrite = await access.ReadAsync(path, name, ct);
                if (beforeWrite != old)
                    throw new InvalidOperationException("Registry value changed after backup; no write was made.");
                if (desired.Exists) await access.WriteAsync(path, name, desired, ct);
                else await access.DeleteValueAsync(path, name, ct);
                var actual = await access.ReadAsync(path, name, ct);
                if (actual != desired)
                    throw new InvalidOperationException("Registry verification failed.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Registry change failed for {path}\\{name}; backup {backupId} was preserved.", e);
            }
        }
        return Result(Outcome.Change, dryRun ? $"Would change {path}\\{name}." :
            $"Changed {path}\\{name}; verified. Backup: {backupId}.");
        OperationResult Result(Outcome outcome, string message) => new(op.Id, op.Type, outcome, clock.GetUtcNow(), message);
    }
}
