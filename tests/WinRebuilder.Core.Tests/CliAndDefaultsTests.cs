using System.Diagnostics;
using WinRebuilder.Core;
using Xunit;

namespace WinRebuilder.Core.Tests;

public sealed class CliAndDefaultsTests
{
    private static string Root => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Theory]
    [InlineData("config.yml")]
    [InlineData("config.example.yml")]
    public async Task DefaultConfigurationHasExactlyTheVerifiedSoftware(string file)
    {
        var profile = await ConfigLoader.LoadAsync(Path.Combine(Root, file));
        Assert.Equal(["7zip.7zip", "CrystalDewWorld.CrystalDiskInfo", "valinet.ExplorerPatcher"],
            profile.Profile.Packages.Select(x => x.Id));
        Assert.Equal(1, Planner.Create(profile).Operations.Count(x => x.Package?.Name == "ExplorerPatcher"));
        Assert.Null(profile.Profile.ExplorerPatcher?.SettingsFile);
    }

    [Theory]
    [InlineData("--help", 0, "Usage:")]
    [InlineData("-h", 0, "wrb [command] [options]")]
    [InlineData("--version", 0, "WinRebuilder v.0.3.b-beta")]
    [InlineData("unknown-command", 2, "wrb --help")]
    [InlineData("validate", 2, "wrb --help")]
    public async Task CliCommandsReportExpectedResult(string argument, int exit, string expected)
    {
        var result = await RunCli(argument);
        Assert.Equal(exit, result.ExitCode);
        Assert.Contains(expected, result.Output);
    }

    [Fact]
    public async Task HelpDescribesAllCommandsAndNeverClaimsWrbExeAsset()
    {
        var output = (await RunCli("--help")).Output;
        foreach (var command in new[] { "validate", "plan", "apply", "backups", "rollback", "ui", "--dry-run" })
            Assert.Contains(command, output);
        Assert.DoesNotContain("wrb.exe", output);
        Assert.DoesNotContain("`wrb.exe` is the release", await File.ReadAllTextAsync(Path.Combine(Root, "README.md")));
    }

    [Fact]
    public async Task ShimQuotesBrandedCliAndForwardsArguments()
    {
        var shim = await File.ReadAllTextAsync(Path.Combine(Root, "packaging", "wrb.cmd"));
        Assert.Contains("\"%~dp0WinRebuilder.Cli.exe\" %*", shim);
        Assert.Contains("DisableDelayedExpansion", shim);
        Assert.DoesNotContain("wrb.exe", shim);
        Assert.DoesNotContain("powershell", shim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonWindowsDryRunPreviewsDefaultWithoutWrites()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Root, "config.example.yml");
        var before = await File.ReadAllBytesAsync(path);
        var result = await RunCli("apply", path, "--dry-run");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("non-Windows plan only", result.Output);
        Assert.Contains("WOULD INSTALL Shell", result.Output);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    private static async Task<(int ExitCode, string Output)> RunCli(params string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "WinRebuilder.Cli.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
