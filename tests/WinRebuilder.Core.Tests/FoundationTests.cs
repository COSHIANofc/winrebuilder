using System.Diagnostics;
using System.Reflection;
using WinRebuilder.Core;
using Xunit;

namespace WinRebuilder.Core.Tests;

public sealed class FoundationTests
{
    [Fact] public void PublicVersionMetadataIsExact()
    {
        var assembly = typeof(Executor).Assembly;
        Assert.Equal(new Version(0, 4, 0, 0), assembly.GetName().Version);
        Assert.Equal("0.4.0.0", assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
        Assert.Equal("v.1.0.a-pre1", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }
    private const string Base = """
        version: 1
        packages:
          - name: WizTree
            provider: winget
            id: AntibodySoftware.WizTree
        registry:
          - path: HKCU\Software\Policies\WinRebuilderTest
            name: Example
            type: DWORD
            value: 1
        explorerPatcher:
          enabled: true
          provider: winget
          packageId: valinet.ExplorerPatcher
        """;

    [Fact] public void ValidYamlAndPhaseOrdering()
    {
        var loaded = ProfileLoader.Load(Base);
        Assert.Equal(2, loaded.Profile.Packages.Count);
        var ops = Planner.Create(loaded).Operations;
        Assert.Equal(3, ops.Count);
        Assert.Equal(Phase.Shell, ops[^1].Phase);
        Assert.Equal("ExplorerPatcher", ops[^1].Package?.Name);
        Assert.Equal(OperationType.Registry, ops[1].Type);
        Assert.Equal(Planner.Create(loaded).Operations.Select(x => x.Id), ops.Select(x => x.Id));
    }
    [Theory]
    [InlineData("version: [")]
    [InlineData("version: 1\nunknown: true")]
    [InlineData("version: 1\nversion: 1")]
    [InlineData("version: 1\npackages: {bad: x}")]
    [InlineData("version: 1\npackages: &x []")]
    [InlineData("version: 2")]
    public void RejectsMalformedYaml(string yaml) => Assert.Throws<FormatException>(() => ProfileLoader.Load(yaml));

    [Theory]
    [InlineData("bad")]
    [InlineData("powershell")]
    public void RejectsUnknownProvider(string provider) => Assert.Throws<FormatException>(() => ProfileLoader.Load($"version: 1\npackages:\n  - name: X\n    provider: {provider}\n    id: A.B"));

    [Theory]
    [InlineData("QWORD")]
    [InlineData("BINARY")]
    public void RejectsRegistryType(string type) => Assert.Throws<FormatException>(() => ProfileLoader.Load($"version: 1\nregistry:\n  - path: HKCU\\Software\\Policies\\Test\n    name: X\n    type: {type}\n    value: 1"));

    [Theory]
    [InlineData("http://example.com/a.msi")]
    [InlineData("https://user:pass@example.com/a.msi")]
    [InlineData("file:///a.msi")]
    public void RejectsUnsafeUrl(string url) => Assert.Throws<FormatException>(() => ProfileLoader.Load(UrlProfile(url)));

    [Fact] public void AcceptsHttpsAndRejectsInvalidHash()
    {
        Assert.Single(ProfileLoader.Load(UrlProfile("https://example.com/a.msi")).Profile.Packages);
        Assert.Throws<FormatException>(() => ProfileLoader.Load(UrlProfile("https://example.com/a.msi").Replace(new string('a', 64), "abc")));
    }

    [Fact] public void RejectsExplorerPatcherAsOrdinaryPackage() => Assert.Throws<FormatException>(() => ProfileLoader.Load(Base.Replace("  - name: WizTree", "  - name: ExplorerPatcher")));
    [Fact] public void RejectsArbitraryCommandField() => Assert.Throws<FormatException>(() => ProfileLoader.Load(Base.Replace("id: AntibodySoftware.WizTree", "id: AntibodySoftware.WizTree\n    command: evil")));
    [Fact] public void RejectsRegistryOutsidePolicyTree() => Assert.Throws<FormatException>(() => ProfileLoader.Load(RegistryProfile.Replace(@"Software\Policies\Test", @"Software\Microsoft\Windows\CurrentVersion\Run")));
    [Fact] public void RejectsExplicitYamlTag() => Assert.Throws<FormatException>(() => ProfileLoader.Load("version: !!str 1"));

    [Theory]
    [InlineData("")]
    [InlineData("A..B")]
    [InlineData("A.B.")]
    [InlineData("A.B\n--source evil")]
    [InlineData("A.B --source evil")]
    [InlineData("-A.B")]
    public void RejectsMalformedWingetIds(string id) =>
        Assert.Throws<FormatException>(() => ProfileLoader.Load($"version: 1\npackages:\n  - name: X\n    provider: winget\n    id: '{id}'"));

    [Fact]
    public void AllowsLegitimateWingetPunctuationWithoutShellInterpretation() =>
        Assert.True(WingetPackageId.IsValid("Vendor.App+Tools"));

    [Theory]
    [InlineData("v1.12.430", true)]
    [InlineData("1.2.3.4\r\n", true)]
    [InlineData("v1.12.430-preview", true)]
    [InlineData("winget v1.12.430", false)]
    [InlineData("v1.2", false)]
    [InlineData("v1.2.3\nextra", false)]
    public void WingetVersionParsing(string output, bool expected) =>
        Assert.Equal(expected, WingetVersion.TryParse(output, out _));

    [Fact] public async Task InstalledPackageSkips()
    {
        var f = new Fake(); f.Installed = true;
        var results = await f.Run("version: 1\npackages:\n  - name: A\n    provider: winget\n    id: A.B", false);
        Assert.Equal(Outcome.Skip, results.Single().Outcome);
        Assert.Equal(0, f.Installs);
    }
    [Fact] public async Task MissingPackageInstallsOnceThenSkips()
    {
        var f = new Fake();
        const string yaml = "version: 1\npackages:\n  - name: A\n    provider: winget\n    id: A.B";
        Assert.Equal(Outcome.Install, (await f.Run(yaml, false)).Single().Outcome);
        Assert.Equal(Outcome.Skip, (await f.Run(yaml, false)).Single().Outcome);
        Assert.Equal(1, f.Installs);
    }
    [Fact] public async Task MatchingRegistrySkips()
    {
        var f = new Fake { Value = RegistryValue.FromDWord(1) };
        var results = await f.Run(RegistryProfile, false);
        Assert.Equal(Outcome.Skip, results.Single().Outcome);
        Assert.Equal(0, f.Writes);
        Assert.Equal(0, f.Backups);
    }
    [Fact] public async Task DifferingRegistryChangesWithBackup()
    {
        var f = new Fake { Value = RegistryValue.FromDWord(2) };
        var results = await f.Run(RegistryProfile, false);
        Assert.Equal(Outcome.Change, results.Single().Outcome);
        Assert.Equal(1, f.Writes);
        Assert.Equal(1, f.Backups);
        Assert.Equal((uint)2, f.LastBackup?.Previous.DWord);
        Assert.True(f.BackupBeforeWrite);
    }
    [Fact] public async Task FailedBackupPreventsRegistryWrite()
    {
        var f = new Fake { BackupFails = true };
        var results = await f.Run(RegistryProfile, false);
        Assert.Equal(Outcome.Fail, results.Single().Outcome);
        Assert.Equal(0, f.Writes);
    }
    [Fact] public async Task RepeatedApplySkipsAfterVerifiedChange()
    {
        var f = new Fake();
        Assert.Equal(Outcome.Change, (await f.Run(RegistryProfile, false)).Single().Outcome);
        Assert.Equal(Outcome.Skip, (await f.Run(RegistryProfile, false)).Single().Outcome);
        Assert.Equal(1, f.Writes);
        Assert.Equal(1, f.Backups);
    }
    [Fact] public async Task DryRunDoesNotMutateOrPersist()
    {
        var f = new Fake { Value = RegistryValue.Missing };
        var results = await f.Run("version: 1\npackages:\n  - name: A\n    provider: winget\n    id: A.B\n" + RegistryProfile["version: 1\n".Length..], true);
        Assert.Equal([Outcome.Install, Outcome.Change], results.Select(x => x.Outcome));
        Assert.Equal(0, f.Installs);
        Assert.Equal(0, f.Writes);
        Assert.Equal(0, f.Backups);
        Assert.Equal(0, f.StateReads);
        Assert.Equal(0, f.StateWrites);
    }
    private const string RegistryProfile = "version: 1\nregistry:\n  - path: HKCU\\Software\\Policies\\Test\n    name: X\n    type: DWORD\n    value: 1";
    private static string UrlProfile(string url) => $"version: 1\npackages:\n  - name: A\n    provider: url\n    url: {url}\n    sha256: {new string('a', 64)}\n    installer: msi\n    uninstallDisplayName: A";

    private sealed class Fake : IPackageProvider, IRegistryAccess, IRegistryBackupStore, IExecutionStateStore, IOperationLogger
    {
        public PackageProvider Kind => PackageProvider.Winget;
        public bool Installed; public RegistryValue Value = RegistryValue.Missing;
        public int Installs, Writes, Backups, StateReads, StateWrites; public bool BackupBeforeWrite, BackupFails;
        public RegistryBackup? LastBackup;
        public Task<bool> IsInstalledAsync(PackageSpec package, CancellationToken ct) => Task.FromResult(Installed);
        public Task InstallAsync(PackageSpec package, CancellationToken ct) { Installs++; Installed = true; return Task.CompletedTask; }
        public Task<RegistryValue> ReadAsync(string path, string name, CancellationToken ct) => Task.FromResult(Value);
        public Task WriteAsync(string path, string name, RegistryValue value, CancellationToken ct)
        { BackupBeforeWrite = Backups == 1; Writes++; Value = value; return Task.CompletedTask; }
        public Task DeleteValueAsync(string path, string name, CancellationToken ct)
        { Value = RegistryValue.Missing; return Task.CompletedTask; }
        public Task SaveAsync(RegistryBackup backup, CancellationToken ct) { if (BackupFails) throw new IOException("backup failed"); Backups++; LastBackup = backup; return Task.CompletedTask; }
        Task<RegistryBackup> IRegistryBackupStore.LoadAsync(string backupId, CancellationToken ct) => Task.FromResult(LastBackup!);
        public Task<IReadOnlyList<RegistryBackup>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RegistryBackup>>(LastBackup is null ? [] : [LastBackup]);
        public Task<ExecutionState?> LoadAsync(string hash, CancellationToken ct) { StateReads++; return Task.FromResult<ExecutionState?>(null); }
        public Task SaveAsync(ExecutionState state, CancellationToken ct) { StateWrites++; return Task.CompletedTask; }
        public void Log(LogEntry entry) { }
        public Task<IReadOnlyList<OperationResult>> Run(string yaml, bool dryRun)
        { var executor = new Executor([this], this, this, this, this, "test"); return executor.RunAsync(Planner.Create(ProfileLoader.Load(yaml)), dryRun); }
    }
}
