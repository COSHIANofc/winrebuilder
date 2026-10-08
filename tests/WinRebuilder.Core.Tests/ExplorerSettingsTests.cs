using WinRebuilder.Core;
using Xunit;

namespace WinRebuilder.Core.Tests;

public sealed class ExplorerSettingsTests
{
    private const string Key = @"HKCU\Software\ExplorerPatcher";
    private const string Reg = "Windows Registry Editor Version 5.00\r\n\r\n; ordinary comment\r\n[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\r\n\"OldTaskbar\"=dword:00000002\r\n\"WeatherLocation\"=\"a\\\\b\"\r\n";
    private const string Config = """
        version: 1
        packages:
          - name: 7-Zip
            provider: winget
            id: 7zip.7zip
          - name: ExplorerPatcher
            provider: github
            repository: valinet/ExplorerPatcher
            asset: ep_setup.exe
            installer: exe
            silentMode: silent
            uninstallDisplayName: ExplorerPatcher
            phase: shell
        registry:
          - path: HKCU\Software\Policies\WinRebuilderTests
            name: Example
            type: DWORD
            value: 1
        explorerPatcher:
          enabled: true
          settingsFile: settings/explorerpatcher.reg
        """;

    [Fact]
    public void ParsesSupportedExportSubset()
    {
        var settings = ExplorerRegParser.Parse("\uFEFF" + Reg);
        Assert.Equal(2, settings.Count);
        Assert.Equal(RegistryValue.FromDWord(2), settings[0].Target);
        Assert.Equal(RegistryValue.FromString(@"a\b"), settings[1].Target);
        Assert.Equal(RegistryValue.Missing, ExplorerRegParser.Parse(
            "Windows Registry Editor Version 5.00\n[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"OldTaskbar\"=-")[0].Target);
    }

    [Fact]
    public void RejectsUnsupportedAndUnsafeRegOperations()
    {
        const string prefix = "Windows Registry Editor Version 5.00\n";
        var bad = new[]
        {
            "bad header\n[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"X\"=dword:00000001",
            prefix + "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\n\"X\"=\"evil\"",
            prefix + "[HKEY_LOCAL_MACHINE\\Software\\ExplorerPatcher]\n\"X\"=dword:00000001",
            prefix + "[-HKEY_CURRENT_USER\\Software\\ExplorerPatcher]",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"X\"=hex:01,02",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"UpdateURL\"=\"https://evil.example/update.exe\"",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"UpdateUseLocal\"=dword:00000001",
            prefix + "[HKEY_CURRENT_USER\\Control Panel\\Desktop]\n\"SCRNSAVE.EXE\"=\"evil.exe\"",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"X\"=dword:1",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n@=\"default\"",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"X\"=\"ok\"\nunknown",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n;\"Virtualized_foo\"=dword:00000001\n\"X\"=dword:00000001",
            prefix + "[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"X\"=dword:00000001\n\"X\"=dword:00000002"
        };
        foreach (var input in bad) Assert.Throws<FormatException>(() => ExplorerRegParser.Parse(input));
    }

    [Theory]
    [InlineData("../evil.reg")]
    [InlineData("/tmp/evil.reg")]
    [InlineData("C:/evil.reg")]
    [InlineData("\\\\server\\share\\evil.reg")]
    [InlineData("settings/../evil.reg")]
    [InlineData("settings\\evil.reg")]
    [InlineData("bad.txt")]
    public void RejectsUnsafeSettingsPath(string path) => Assert.Throws<FormatException>(() => ExplorerSettingsPath.Validate(path));

    [Fact]
    public async Task ConfigLoadsRegAndPlansAfterShellPackage()
    {
        await WithConfig(Reg, async path =>
        {
            var loaded = await ConfigLoader.LoadAsync(path);
            var operations = Planner.Create(loaded).Operations;
            Assert.Equal(5, operations.Count);
            Assert.Equal(OperationType.Package, operations[0].Type);
            Assert.Equal(OperationType.Registry, operations[1].Type);
            Assert.Equal("ExplorerPatcher", operations[2].Package?.Name);
            Assert.All(operations.Skip(3), x => Assert.Equal(OperationType.ExplorerPatcherSetting, x.Type));
            Assert.Equal(operations.Select(x => x.Id), Planner.Create(loaded).Operations.Select(x => x.Id));
        });
    }

    [Fact]
    public async Task MissingAndMalformedSettingsFailBeforeExecution()
    {
        await WithConfig(Reg, async path =>
        {
            File.Delete(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "settings", "explorerpatcher.reg"));
            await Assert.ThrowsAsync<FileNotFoundException>(() => ConfigLoader.LoadAsync(path));
        });
        await WithConfig(Reg + "\n[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\n\"Evil\"=\"x\"", async path =>
            await Assert.ThrowsAsync<FormatException>(() => ConfigLoader.LoadAsync(path)));
    }

    [Fact]
    public async Task MissingConfigFails()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => ConfigLoader.LoadAsync(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.yml")));
    }

    [Fact]
    public async Task SettingsBytesContributeToConfigurationIdentity()
    {
        await WithConfig(Reg, async path =>
        {
            var first = await ConfigLoader.LoadAsync(path);
            var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "settings", "explorerpatcher.reg");
            await File.AppendAllTextAsync(settingsPath, "; another comment\r\n");
            var second = await ConfigLoader.LoadAsync(path);
            Assert.NotEqual(first.Sha256, second.Sha256);
        });
    }

    [Fact]
    public async Task NamedValueDeletionIsBackedUpAndRollbackRestoresOnlyThatValue()
    {
        const string deleting = "Windows Registry Editor Version 5.00\n[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\n\"OldTaskbar\"=-";
        await WithConfig(deleting, async path =>
        {
            var plan = Planner.Create(await ConfigLoader.LoadAsync(path));
            var fake = new Fake { Installed = true };
            fake.Values[(Key, "OldTaskbar")] = RegistryValue.FromDWord(2);
            fake.Values[(Key, "Unrelated")] = RegistryValue.FromString("keep");
            var executor = new Executor([fake, new GithubFake()], fake, fake, fake, fake, "v.0.2.a-beta", explorerRegistry: fake);
            var applied = await executor.RunAsync(plan, false);
            Assert.Equal(Outcome.Change, applied[^1].Outcome);
            Assert.False(fake.Values.ContainsKey((Key, "OldTaskbar")));
            Assert.Equal(RegistryValue.FromString("keep"), fake.Values[(Key, "Unrelated")]);
            var backup = Assert.Single(fake.Backups.Values, x => x.Scope == RegistryScope.ExplorerPatcher);
            Assert.Equal(RegistryValue.Missing, backup.Target);
            var rollback = await new RegistryRollback(fake, fake, fake, fake).RunAsync(backup.BackupId, false);
            Assert.Equal(Outcome.Restore, rollback.Outcome);
            Assert.Equal(RegistryValue.FromDWord(2), fake.Values[(Key, "OldTaskbar")]);
            Assert.Equal(RegistryValue.FromString("keep"), fake.Values[(Key, "Unrelated")]);
        });
    }

    [Fact]
    public async Task FailedExplorerBackupPreventsEveryExplorerWrite()
    {
        await WithConfig(Reg, async path =>
        {
            var plan = Planner.Create(await ConfigLoader.LoadAsync(path));
            var fake = new Fake { Installed = true, FailBackupAt = 3 };
            var executor = new Executor([fake, new GithubFake()], fake, fake, fake, fake, "v.0.2.a-beta", explorerRegistry: fake);
            var results = await executor.RunAsync(plan, false);
            Assert.Equal(Outcome.Fail, results[^1].Outcome);
            Assert.Null(fake.BackupsAtFirstExplorerWrite);
            Assert.False(fake.Values.ContainsKey((Key, "OldTaskbar")));
        });
    }

    [Fact]
    public async Task ShellSettingsDryRunIsReadOnlyAndRealApplyCanRollback()
    {
        await WithConfig(Reg, async path =>
        {
            var plan = Planner.Create(await ConfigLoader.LoadAsync(path));
            var fake = new Fake();
            var executor = new Executor([fake, new GithubFake()], fake, fake, fake, fake, "v.0.2.a-beta", explorerRegistry: fake);
            var dry = await executor.RunAsync(plan, true);
            Assert.Contains(dry, x => x.Type == OperationType.ExplorerPatcherSetting && x.Outcome == Outcome.Change);
            Assert.Equal(0, fake.Mutations);
            Assert.Empty(fake.Backups);
            Assert.Equal(0, fake.StateWrites);
            var applied = await executor.RunAsync(plan, false);
            Assert.Equal(Outcome.Change, applied[^1].Outcome);
            Assert.Equal(3, fake.Backups.Count);
            Assert.Equal(3, fake.BackupsAtFirstExplorerWrite);
            var epBackup = fake.Backups.Values.First(x => x.Scope == RegistryScope.ExplorerPatcher);
            Assert.Equal(Outcome.Restore, (await new RegistryRollback(fake, fake, fake, fake).RunAsync(epBackup.BackupId, false)).Outcome);
        });
    }

    [Fact]
    public void StrictConfigRejectsUnknownFieldsAndMissingPackage()
    {
        Assert.Throws<FormatException>(() => ProfileLoader.Load(Config.Replace("enabled: true", "enabled: yes")));
        Assert.Throws<FormatException>(() => ProfileLoader.Load(Config + "\nunknown: true"));
        Assert.Throws<FormatException>(() => ProfileLoader.Load(Config.Replace("settingsFile: settings/explorerpatcher.reg", "settingsFile: ../evil.reg")));
        Assert.Throws<FormatException>(() => ProfileLoader.Load(Config.Replace("  - name: ExplorerPatcher", "  - name: Other")));
    }

    private static async Task WithConfig(string reg, Func<string, Task> action)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winrebuilder-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "settings"));
        try
        {
            var config = System.IO.Path.Combine(root, "config.yml");
            await File.WriteAllTextAsync(config, Config);
            await File.WriteAllTextAsync(System.IO.Path.Combine(root, "settings", "explorerpatcher.reg"), reg);
            await action(config);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fake : IPackageProvider, IRegistryAccess, IExplorerRegistryAccess, IRegistryBackupStore, IExecutionStateStore, IOperationLogger
    {
        public PackageProvider Kind => PackageProvider.Winget;
        public readonly Dictionary<(string Path, string Name), RegistryValue> Values = [];
        public readonly Dictionary<string, RegistryBackup> Backups = [];
        public int Mutations, StateWrites;
        public int? FailBackupAt;
        public int? BackupsAtFirstExplorerWrite;
        public bool Installed;
        public Task<bool> IsInstalledAsync(PackageSpec package, CancellationToken ct) => Task.FromResult(Installed);
        public Task InstallAsync(PackageSpec package, CancellationToken ct) { Installed = true; Mutations++; return Task.CompletedTask; }
        public Task<RegistryValue> ReadAsync(string path, string name, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault((path, name), RegistryValue.Missing));
        public Task WriteAsync(string path, string name, RegistryValue value, CancellationToken ct)
        { if (path == Key) BackupsAtFirstExplorerWrite ??= Backups.Count; Values[(path, name)] = value; Mutations++; return Task.CompletedTask; }
        public Task DeleteValueAsync(string path, string name, CancellationToken ct)
        { if (path == Key) BackupsAtFirstExplorerWrite ??= Backups.Count; Values.Remove((path, name)); Mutations++; return Task.CompletedTask; }
        public Task SaveAsync(RegistryBackup backup, CancellationToken ct)
        { if (Backups.Count + 1 == FailBackupAt) throw new IOException("backup failed"); Backups.Add(backup.BackupId, backup); return Task.CompletedTask; }
        Task<RegistryBackup> IRegistryBackupStore.LoadAsync(string id, CancellationToken ct) => Task.FromResult(Backups[id]);
        public Task<IReadOnlyList<RegistryBackup>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RegistryBackup>>(Backups.Values.ToArray());
        public Task<ExecutionState?> LoadAsync(string hash, CancellationToken ct) => Task.FromResult<ExecutionState?>(null);
        public Task SaveAsync(ExecutionState state, CancellationToken ct) { StateWrites++; return Task.CompletedTask; }
        public void Log(LogEntry entry) { }
    }
    private sealed class GithubFake : IPackageProvider
    {
        public PackageProvider Kind => PackageProvider.Github;
        public Task<bool> IsInstalledAsync(PackageSpec package, CancellationToken ct) => Task.FromResult(true);
        public Task InstallAsync(PackageSpec package, CancellationToken ct) => throw new Xunit.Sdk.XunitException("unexpected install");
    }
}
