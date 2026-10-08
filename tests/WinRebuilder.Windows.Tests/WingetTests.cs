using System.ComponentModel;
using System.Diagnostics;
using WinRebuilder.Core;
using WinRebuilder.Windows;
using Xunit;

namespace WinRebuilder.Windows.Tests;

public sealed class WingetTests
{
    private const string Id = "AntibodySoftware.WizTree";
    private const string Profile = "version: 1\npackages:\n  - name: WizTree\n    provider: winget\n    id: AntibodySoftware.WizTree";
    private const string ListHelp = "--id --exact --disable-interactivity";
    private const string InstallHelp = "--id --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

    [Fact]
    public async Task CapabilityProbeChecksVersionAndRequiredFlags()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(0, "v1.12.430\r\n", ""));
        runner.Replies.Enqueue(new(0, ListHelp, ""));
        runner.Replies.Enqueue(new(0, InstallHelp, ""));
        var result = await new WingetCapabilityProbe(runner).ProbeAsync(default);
        Assert.Equal(WingetCapabilityStatus.Available, result.Status);
        Assert.Equal(new Version(1, 12, 430), result.Version);
        Assert.Equal(["--version"], runner.Calls[0].Args);
        Assert.Equal(["list", "--help"], runner.Calls[1].Args);
        Assert.Equal(["install", "--help"], runner.Calls[2].Args);
    }

    [Fact]
    public async Task MissingExecutableHasDistinctStatus()
    {
        var runner = new ScriptedRunner { Error = new Win32Exception(2) };
        Assert.Equal(WingetCapabilityStatus.MissingExecutable, (await new WingetCapabilityProbe(runner).ProbeAsync(default)).Status);
    }

    [Fact]
    public async Task CannotExecuteHasDistinctStatus()
    {
        var runner = new ScriptedRunner { Error = new Win32Exception(5) };
        Assert.Equal(WingetCapabilityStatus.CannotExecute, (await new WingetCapabilityProbe(runner).ProbeAsync(default)).Status);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("v1.2.3\nmalicious text")]
    [InlineData("")]
    public async Task MalformedVersionIsUnsupported(string output)
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(0, output, ""));
        Assert.Equal(WingetCapabilityStatus.UnsupportedOutput, (await new WingetCapabilityProbe(runner).ProbeAsync(default)).Status);
    }

    [Fact]
    public async Task MissingRequiredFlagIsUnsupported()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(0, "v1.12.430", ""));
        runner.Replies.Enqueue(new(0, "--id --exact", ""));
        runner.Replies.Enqueue(new(0, InstallHelp, ""));
        Assert.Equal(WingetCapabilityStatus.UnsupportedOutput, (await new WingetCapabilityProbe(runner).ProbeAsync(default)).Status);
    }

    [Fact]
    public async Task ExactInstalledPackageUsesExitCodeWithoutParsingTable()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(0, "localized table layout", ""));
        var provider = Provider(runner);
        Assert.True(await provider.IsInstalledAsync(Package(), default));
        Assert.Equal(["list", "--id", Id, "--exact", "--disable-interactivity"], runner.Calls.Single().Args);
    }

    [Fact]
    public async Task KnownNoApplicationsFoundMeansMissing()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(WingetExitCodes.NoApplicationsFound, "", ""));
        Assert.False(await Provider(runner).IsInstalledAsync(Package(), default));
    }

    [Fact]
    public async Task UnknownListFailureRemainsFailure()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(73, "", "untrusted diagnostics"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(runner).IsInstalledAsync(Package(), default));
        Assert.Contains("73", error.Message);
        Assert.DoesNotContain("untrusted diagnostics", error.Message);
    }

    [Fact]
    public async Task InstallUsesSeparateValidatedArguments()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(0, "", ""));
        await Provider(runner).InstallAsync(Package(), default);
        Assert.Equal(WingetCommands.Executable, runner.Calls.Single().File);
        Assert.Equal(["install", "--id", Id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"], runner.Calls.Single().Args);
        Assert.Throws<ArgumentException>(() => WingetCommands.InstallExact("Vendor.App --source evil"));
        Assert.Equal("Vendor.App;echo", WingetCommands.ListExact("Vendor.App;echo")[2]);
    }

    [Fact]
    public async Task FirstApplyInstallsAndSecondSkips()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(WingetExitCodes.NoApplicationsFound, "", ""));
        runner.Replies.Enqueue(new(0, "", ""));
        runner.Replies.Enqueue(new(0, "localized table", ""));
        runner.Replies.Enqueue(new(0, "localized table", ""));
        var state = new MemoryState();
        var executor = Executor(Provider(runner), state);
        var plan = Planner.Create(ProfileLoader.Load(Profile));
        Assert.Equal(Outcome.Install, (await executor.RunAsync(plan, false)).Single().Outcome);
        Assert.Contains(plan.Operations.Single().Id, state.Saved!.Completed.Keys);
        Assert.Equal(Outcome.Skip, (await executor.RunAsync(plan, false)).Single().Outcome);
        Assert.Single(runner.Calls, x => x.Args[0] == "install");
    }

    [Fact]
    public async Task InstalledBeforeApplySkipsWithoutInstall()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(0, "localized table", ""));
        var result = await Executor(Provider(runner), new MemoryState()).RunAsync(Planner.Create(ProfileLoader.Load(Profile)), false);
        Assert.Equal(Outcome.Skip, result.Single().Outcome);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task DryRunOnlyQueriesAndNeverSavesState()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(WingetExitCodes.NoApplicationsFound, "", ""));
        var state = new MemoryState();
        var result = await Executor(Provider(runner), state).RunAsync(Planner.Create(ProfileLoader.Load(Profile)), true);
        Assert.Equal(Outcome.Install, result.Single().Outcome);
        Assert.Single(runner.Calls);
        Assert.Equal("list", runner.Calls[0].Args[0]);
        Assert.Equal(0, state.Reads);
        Assert.Equal(0, state.Writes);
    }

    [Fact]
    public async Task FailedInstallDoesNotCompleteState()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(WingetExitCodes.NoApplicationsFound, "", ""));
        runner.Replies.Enqueue(new(42, "", "untrusted diagnostics"));
        var state = new MemoryState();
        var result = await Executor(Provider(runner), state).RunAsync(Planner.Create(ProfileLoader.Load(Profile)), false);
        Assert.Equal(Outcome.Fail, result.Single().Outcome);
        Assert.Contains("42", result.Single().Message);
        Assert.Empty(state.Saved!.Completed);
        Assert.Single(state.Saved.Failed);
    }

    [Fact]
    public async Task SuccessfulExitWithoutDetectionFails()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(WingetExitCodes.NoApplicationsFound, "", ""));
        runner.Replies.Enqueue(new(0, "", ""));
        runner.Replies.Enqueue(new(WingetExitCodes.NoApplicationsFound, "", ""));
        var state = new MemoryState();
        var result = await Executor(Provider(runner), state).RunAsync(Planner.Create(ProfileLoader.Load(Profile)), false);
        Assert.Equal(Outcome.Fail, result.Single().Outcome);
        Assert.Contains("did not confirm", result.Single().Message);
        Assert.Empty(state.Saved!.Completed);
    }

    [Fact]
    public async Task FailedRecheckClearsPreviousCompletion()
    {
        var runner = new ScriptedRunner();
        runner.Replies.Enqueue(new(71, "", ""));
        var plan = Planner.Create(ProfileLoader.Load(Profile));
        var state = new MemoryState
        {
            Saved = new ExecutionState(plan.ProfileHash, "test", new() { [plan.Operations.Single().Id] = DateTimeOffset.UtcNow }, new())
        };
        var result = await Executor(Provider(runner), state).RunAsync(plan, false);
        Assert.Equal(Outcome.Fail, result.Single().Outcome);
        Assert.Empty(state.Saved!.Completed);
        Assert.Single(state.Saved.Failed);
    }

    [Fact]
    public async Task UnavailableWingetFailsWithoutInstall()
    {
        var runner = new ScriptedRunner();
        var provider = Provider(runner, new FixedProbe(WingetCapabilityStatus.MissingExecutable));
        var result = await Executor(provider, new MemoryState()).RunAsync(Planner.Create(ProfileLoader.Load(Profile)), false);
        Assert.Equal(Outcome.Fail, result.Single().Outcome);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ProbeCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WingetCapabilityProbe(new ScriptedRunner()).ProbeAsync(cts.Token));
    }

    [Fact]
    public async Task ProcessRunnerCancellationStopsLongRunningChild()
    {
        var runner = new WindowsProcessRunner();
        var executable = OperatingSystem.IsWindows() ? "ping.exe" : "/bin/sleep";
        string[] args = OperatingSystem.IsWindows() ? ["-n", "30", "127.0.0.1"] : ["30"];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(executable, args, cts.Token));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8));
    }

    private static PackageSpec Package() => ProfileLoader.Load(Profile).Profile.Packages.Single();
    private static WingetProvider Provider(ScriptedRunner runner, IWingetCapabilityProbe? probe = null) => new(runner, probe ?? new FixedProbe());
    private static Executor Executor(WingetProvider provider, MemoryState state) =>
        new([provider], new NoopRegistry(), new NoopBackup(), state, new NoopLogger(), "test");

    private sealed class FixedProbe(WingetCapabilityStatus status = WingetCapabilityStatus.Available) : IWingetCapabilityProbe
    {
        public Task<WingetCapability> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new WingetCapability(status, new Version(1, 12, 430), status == WingetCapabilityStatus.Available ? "available" : "winget unavailable"));
    }
    private sealed class ScriptedRunner : IProcessRunner
    {
        public readonly Queue<ProcessResult> Replies = new();
        public readonly List<(string File, IReadOnlyList<string> Args)> Calls = [];
        public Exception? Error;
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((file, arguments.ToArray()));
            if (Error is not null) throw Error;
            return Task.FromResult(Replies.Dequeue());
        }
    }
    private sealed class NoopRegistry : IRegistryAccess
    {
        public Task<RegistryValue> ReadAsync(string path, string name, CancellationToken ct) => throw new Xunit.Sdk.XunitException("registry queried");
        public Task WriteAsync(string path, string name, RegistryValue value, CancellationToken ct) => throw new Xunit.Sdk.XunitException("registry written");
        public Task DeleteValueAsync(string path, string name, CancellationToken ct) => throw new Xunit.Sdk.XunitException("registry value deleted");
    }
    private sealed class NoopBackup : IRegistryBackupStore
    {
        public Task SaveAsync(RegistryBackup backup, CancellationToken ct) => throw new Xunit.Sdk.XunitException("backup written");
        public Task<RegistryBackup> LoadAsync(string backupId, CancellationToken ct) => throw new Xunit.Sdk.XunitException("backup loaded");
        public Task<IReadOnlyList<RegistryBackup>> ListAsync(CancellationToken ct) => throw new Xunit.Sdk.XunitException("backups listed");
    }
    private sealed class NoopLogger : IOperationLogger { public void Log(LogEntry entry) { } }
    private sealed class MemoryState : IExecutionStateStore
    {
        public int Reads, Writes;
        public ExecutionState? Saved;
        public Task<ExecutionState?> LoadAsync(string profileHash, CancellationToken ct) { Reads++; return Task.FromResult(Saved); }
        public Task SaveAsync(ExecutionState state, CancellationToken ct) { Writes++; Saved = state; return Task.CompletedTask; }
    }
}
