using System.ComponentModel;
using WinRebuilder.Core;

namespace WinRebuilder.Windows;

public static class WingetCommands
{
    public const string Executable = "winget";
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
        return ["install", "--id", id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];
    }
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
        var result = await runner.RunAsync(WingetCommands.Executable, arguments, ct);
        if (!WingetExitCodes.IsSuccess(result.ExitCode))
            throw new InvalidOperationException(WingetExitCodes.DescribeFailure("install", result.ExitCode));
    }

    private async Task EnsureAvailableAsync(CancellationToken ct)
    {
        capability ??= probe.ProbeAsync(ct);
        var result = await capability;
        if (!result.IsAvailable) throw new InvalidOperationException(result.Diagnostic);
    }
}
