using System.Reflection;
using WinRebuilder.Core;
using WinRebuilder.Windows;

var publicVersion = typeof(Executor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? throw new InvalidOperationException("WinRebuilder informational version is missing.");
if (args is ["--version"])
{
    Console.WriteLine($"WinRebuilder {publicVersion}");
    return 0;
}
var profileCommand = args.Length is >= 2 and <= 3 && args[0] is "validate" or "plan" or "apply" &&
    (args.Length == 2 || args[0] == "apply" && args[2] == "--dry-run");
var rollbackCommand = args is ["rollback", _] or ["rollback", _, "--dry-run"];
if (!profileCommand && !rollbackCommand && args is not ["backups"])
{
    Console.Error.WriteLine("Usage: winrebuilder --version | backups | rollback <backup-id> [--dry-run] | validate|plan|apply <config.yml> [--dry-run]");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    if (args[0] is "backups" or "rollback")
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FAIL: registry backups and rollback require Windows 11.");
            return 2;
        }
        var backupStore = new JsonRegistryBackupStore();
        if (args[0] == "backups")
        {
            foreach (var backup in await backupStore.ListAsync(cancellation.Token))
                Console.WriteLine($"{backup.BackupId}  {backup.Timestamp:u}  {backup.FullPath}\\{backup.Name}");
            return 0;
        }
        RegistryBackupId.Validate(args[1]);
        var selected = await backupStore.LoadAsync(args[1], cancellation.Token);
        var rollbackDryRun = args.Length == 3;
        if (!rollbackDryRun && selected.Hive == RegistryHiveKind.LocalMachine && !Administrator.IsElevated())
        {
            Console.Error.WriteLine("FAIL: administrator privileges are required for HKLM rollback.");
            return 2;
        }
        IOperationLogger rollbackLogger = rollbackDryRun ? new ConsoleRollbackLogger() :
            new CombinedLogger(new ConsoleRollbackLogger(), new JsonFileOperationLogger());
        var rollback = new RegistryRollback(new WindowsRegistry(), backupStore, rollbackLogger, new ExplorerWindowsRegistry());
        var outcome = await rollback.RunAsync(args[1], rollbackDryRun, cancellation.Token);
        return outcome.Outcome == Outcome.Fail ? 1 : 0;
    }
    var loaded = await ConfigLoader.LoadAsync(args[1], cancellation.Token);
    var plan = Planner.Create(loaded);
    foreach (var op in plan.Operations.Where(op => op.Package?.Provider == PackageProvider.Github && op.Package.Sha256 is null))
        Console.WriteLine($"WARNING {op.Id} GitHub release asset has no pinned SHA-256.");
    if (args[0] == "validate")
    {
        Console.WriteLine($"VALID: {plan.Operations.Count} operations; profile SHA-256 {loaded.Sha256}");
        return 0;
    }
    if (args[0] == "plan")
    {
        foreach (var op in plan.Operations)
            Console.WriteLine($"{(op.Type == OperationType.Package ? "INSTALL" : "CHANGE")} {op.Phase} {op.Id} {(op.Package?.Name ?? (op.Registry is { } r ? r.Path + "\\" + r.Name : op.ExplorerSetting!.Path + "\\" + op.ExplorerSetting.Name))}");
        return 0;
    }
    var dryRun = args.Length == 3;
    if (!OperatingSystem.IsWindows() && dryRun && plan.Operations.Count == 0)
    {
        Console.WriteLine("SKIP: no operations.");
        return 0;
    }
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("FAIL: apply requires Windows 11; validate and plan work on macOS.");
        return 2;
    }
    if (!dryRun && plan.Operations.Any(op => op.Registry?.Path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) == true) && !Administrator.IsElevated())
    {
        Console.Error.WriteLine("FAIL: administrator privileges are required for HKLM changes.");
        return 2;
    }
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    var runner = new WindowsProcessRunner();
    var downloader = new VerifiedDownloader(http);
    var providers = new IPackageProvider[]
    {
        new WingetProvider(runner),
        new DownloadPackageProvider(PackageProvider.Github, downloader, http, runner, new UninstallRegistryDetector()),
        new DownloadPackageProvider(PackageProvider.Url, downloader, http, runner, new UninstallRegistryDetector())
    };
    IOperationLogger logger = dryRun ? new ConsoleOperationLogger(plan) : new CombinedLogger(new ConsoleOperationLogger(plan), new JsonFileOperationLogger());
    var executor = new Executor(providers, new WindowsRegistry(), new JsonRegistryBackupStore(), new JsonExecutionStateStore(), logger,
        publicVersion, explorerRegistry: new ExplorerWindowsRegistry());
    var results = await executor.RunAsync(plan, dryRun, cancellation.Token);
    return results.Any(r => r.Outcome == Outcome.Fail) ? 1 : 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("FAIL: operation cancelled.");
    return 130;
}
catch (Exception e)
{
    Console.Error.WriteLine("FAIL: " + e.Message);
    return 1;
}

internal sealed class CombinedLogger(params IOperationLogger[] loggers) : IOperationLogger
{
    public void Log(LogEntry entry) { foreach (var logger in loggers) logger.Log(entry); }
}

internal sealed class ConsoleRollbackLogger : IOperationLogger
{
    public void Log(LogEntry entry)
    {
        Console.WriteLine($"{entry.Outcome.ToString().ToUpperInvariant(),-7} [{entry.OperationId}]");
        Console.WriteLine($"        {entry.Message}");
    }
}
