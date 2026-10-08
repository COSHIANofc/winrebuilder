using System.Reflection;
using WinRebuilder.Core;

namespace WinRebuilder.Windows;

public sealed class WindowsExecutionSession : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(10) };
    public Executor Executor { get; }

    public WindowsExecutionSession(IOperationLogger logger)
    {
        var runner = new WindowsProcessRunner();
        var downloader = new VerifiedDownloader(http);
        IPackageProvider[] providers =
        [
            new WingetProvider(runner),
            new DownloadPackageProvider(PackageProvider.Github, downloader, http, runner, new UninstallRegistryDetector()),
            new DownloadPackageProvider(PackageProvider.Url, downloader, http, runner, new UninstallRegistryDetector())
        ];
        var version = typeof(Executor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("WinRebuilder informational version is missing.");
        Executor = new Executor(providers, new WindowsRegistry(), new JsonRegistryBackupStore(),
            new JsonExecutionStateStore(), logger, version, explorerRegistry: new ExplorerWindowsRegistry());
    }

    public void Dispose() => http.Dispose();
}
