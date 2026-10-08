using System.ComponentModel;
using System.Text.Json;
using WinRebuilder.Core;

namespace WinRebuilder.Windows;

public static class WingetCommands
{
    public const string Executable = "winget";
    public const string ExplorerPatcherVersion = "26100.8457.70.3";
    public const string ExplorerPatcherPublisher = "VALINET Solutions SRL";
    public const string ExplorerPatcherInstallerUrl = "https://github.com/valinet/ExplorerPatcher/releases/download/26100.8457.70.3/ep_setup.exe";
    public const string ExplorerPatcherInstallerSha256 = "8146DB4D3A87201FB80AD1D3712BA8F56883E9A0811758BD39F62D73A9F2C586";
    public static IReadOnlyList<string> Version => ["--version"];
    public static IReadOnlyList<string> ListHelp => ["list", "--help"];
    public static IReadOnlyList<string> InstallHelp => ["install", "--help"];
    public static IReadOnlyList<string> ListExact(string id)
    {
        if (!WingetPackageId.IsValid(id)) throw new ArgumentException("Invalid winget package ID.", nameof(id));
        return ["list", "--id", id, "--exact", "--disable-interactivity"];
    }
    public static IReadOnlyList<string> InstallExact(string id)
    {
        if (!WingetPackageId.IsValid(id)) throw new ArgumentException("Invalid winget package ID.", nameof(id));
        if (id == ProfileLoader.ExplorerPatcherWingetId)
            return ["install", "--id", id, "--exact", "--version", ExplorerPatcherVersion, "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];
        return ["install", "--id", id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];
    }
    public static IReadOnlyList<string> ShowExplorerPatcher => ["show", "--id", ProfileLoader.ExplorerPatcherWingetId,
        "--exact", "--version", ExplorerPatcherVersion, "--source", "winget", "--disable-interactivity"];
    public static IReadOnlyList<string> ExportWingetSource => ["source", "export", "winget"];
}

public sealed class WingetCapabilityProbe(IProcessRunner runner) : IWingetCapabilityProbe
{
    public async Task<WingetCapability> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync(WingetCommands.Executable, WingetCommands.Version, ct);
            if (!WingetExitCodes.IsSuccess(result.ExitCode))
                return new(WingetCapabilityStatus.CannotExecute, null, WingetExitCodes.DescribeFailure("--version", result.ExitCode));
            if (result.OutputTruncated || !WingetVersion.TryParse(result.StandardOutput, out var version))
                return new(WingetCapabilityStatus.UnsupportedOutput, null, "winget returned malformed or unsupported version output.");
            var list = await runner.RunAsync(WingetCommands.Executable, WingetCommands.ListHelp, ct);
            var install = await runner.RunAsync(WingetCommands.Executable, WingetCommands.InstallHelp, ct);
            if (!WingetExitCodes.IsSuccess(list.ExitCode) || !WingetExitCodes.IsSuccess(install.ExitCode))
                return new(WingetCapabilityStatus.CannotExecute, version, "winget help commands failed; required flags could not be checked.");
            if (list.OutputTruncated || install.OutputTruncated ||
                !HasFlags(list.StandardOutput, "--id", "--exact", "--disable-interactivity") ||
                !HasFlags(install.StandardOutput, "--id", "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"))
                return new(WingetCapabilityStatus.UnsupportedOutput, version, "winget does not advertise the required list/install options.");
            return new(WingetCapabilityStatus.Available, version, $"winget {version} is available.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
        {
            return new(WingetCapabilityStatus.MissingExecutable, null, "winget executable was not found.");
        }
        catch (Win32Exception)
        {
            return new(WingetCapabilityStatus.CannotExecute, null, "winget executable could not be started.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(WingetCapabilityStatus.CannotExecute, null, "winget executable could not be accessed.");
        }
        catch (IOException)
        {
            return new(WingetCapabilityStatus.CannotExecute, null, "winget executable could not be read or started.");
        }
    }

    private static bool HasFlags(string output, params string[] flags) =>
        flags.All(flag => output.Contains(flag, StringComparison.Ordinal));
}

public sealed class WingetProvider : IPackageProvider
{
    private readonly IProcessRunner runner;
    private readonly IWingetCapabilityProbe probe;
    private Task<WingetCapability>? capability;

    public WingetProvider(IProcessRunner runner, IWingetCapabilityProbe? probe = null)
    {
        this.runner = runner;
        this.probe = probe ?? new WingetCapabilityProbe(runner);
    }

    public PackageProvider Kind => PackageProvider.Winget;

    public async Task<bool> IsInstalledAsync(PackageSpec package, CancellationToken ct)
    {
        var arguments = WingetCommands.ListExact(package.Id!);
        await EnsureAvailableAsync(ct);
        var result = await runner.RunAsync(WingetCommands.Executable, arguments, ct);
        if (WingetExitCodes.IsSuccess(result.ExitCode)) return true;
        if (WingetExitCodes.IsNoApplicationsFound(result.ExitCode)) return false;
        throw new InvalidOperationException(WingetExitCodes.DescribeFailure("list", result.ExitCode));
    }

    public async Task InstallAsync(PackageSpec package, CancellationToken ct)
    {
        var arguments = WingetCommands.InstallExact(package.Id!);
        await EnsureAvailableAsync(ct);
        if (package.Id == ProfileLoader.ExplorerPatcherWingetId)
        {
            var source = await runner.RunAsync(WingetCommands.Executable, WingetCommands.ExportWingetSource, ct);
            if (!WingetExitCodes.IsSuccess(source.ExitCode) || source.OutputTruncated || !IsOfficialSource(source.StandardOutput))
                throw new InvalidOperationException("The winget source is not the official Microsoft community source; ExplorerPatcher installation stopped.");
            var manifest = await runner.RunAsync(WingetCommands.Executable, WingetCommands.ShowExplorerPatcher, ct);
            if (!WingetExitCodes.IsSuccess(manifest.ExitCode) || manifest.OutputTruncated ||
                !manifest.StandardOutput.Contains($"[{ProfileLoader.ExplorerPatcherWingetId}]", StringComparison.Ordinal) ||
                !manifest.StandardOutput.Contains(WingetCommands.ExplorerPatcherVersion, StringComparison.Ordinal) ||
                !manifest.StandardOutput.Contains(WingetCommands.ExplorerPatcherPublisher, StringComparison.Ordinal) ||
                !manifest.StandardOutput.Contains(WingetCommands.ExplorerPatcherInstallerUrl, StringComparison.Ordinal) ||
                !manifest.StandardOutput.Contains(WingetCommands.ExplorerPatcherInstallerSha256, StringComparison.OrdinalIgnoreCase) ||
                !manifest.StandardOutput.Contains(ProfileLoader.ExplorerPatcherWingetId, StringComparison.Ordinal))
                throw new InvalidOperationException("ExplorerPatcher winget manifest did not verify against the official valinet GitHub release; installation stopped.");
        }
        var result = await runner.RunAsync(WingetCommands.Executable, arguments, ct);
        if (!WingetExitCodes.IsSuccess(result.ExitCode))
            throw new InvalidOperationException(WingetExitCodes.DescribeFailure("install", result.ExitCode));
    }

    private static bool IsOfficialSource(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            return root.GetProperty("Name").GetString() == "winget" &&
                root.GetProperty("Arg").GetString() == "https://cdn.winget.microsoft.com/cache" &&
                root.GetProperty("Type").GetString() == "Microsoft.PreIndexed.Package" &&
                root.GetProperty("Identifier").GetString() == "Microsoft.Winget.Source_8wekyb3d8bbwe";
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task EnsureAvailableAsync(CancellationToken ct)
    {
        capability ??= probe.ProbeAsync(ct);
        try
        {
            var result = await capability;
            if (!result.IsAvailable)
            {
                capability = null;
                throw new InvalidOperationException(result.Diagnostic);
            }
        }
        catch
        {
            capability = null;
            throw;
        }
    }
}
