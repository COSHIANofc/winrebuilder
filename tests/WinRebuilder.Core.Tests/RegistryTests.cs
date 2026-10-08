using System.Text;
using WinRebuilder.Core;
using Xunit;

namespace WinRebuilder.Core.Tests;

public sealed class RegistryTests
{
    private const string Path = @"HKCU\Software\Policies\WinRebuilderTests";
    private const string Name = "TestValue";
    private const string Profile = "version: 1\nregistry:\n  - path: HKCU\\Software\\Policies\\WinRebuilderTests\n    name: TestValue\n    type: DWORD\n    value: 1";

    [Fact]
    public void BackupRoundTripsTypedValuesExactly()
    {
        var previous = RegistryValue.FromString("first\r\nsecond\u0001");
        var backup = Backup(previous);
        Assert.Equal(backup, RegistryBackupCodec.Deserialize(RegistryBackupCodec.Serialize(backup)));
        var maxDword = Backup(RegistryValue.FromDWord(uint.MaxValue));
        Assert.Equal(maxDword, RegistryBackupCodec.Deserialize(RegistryBackupCodec.Serialize(maxDword)));
        var absent = Backup(RegistryValue.Missing);
        Assert.Equal(absent, RegistryBackupCodec.Deserialize(RegistryBackupCodec.Serialize(absent)));
        Assert.Throws<FormatException>(() => RegistryBackupCodec.Serialize(Backup(RegistryValue.FromString("\uD800"))));
    }

    [Fact]
    public void ReadsHistoricalGenericBackupWithoutRewritingItsApplicationVersion()
    {
        var old = Backup(RegistryValue.FromDWord(2)) with { ApplicationVersion = "v.0.1.a-beta" };
        var json = Encoding.UTF8.GetString(RegistryBackupCodec.Serialize(old));
        json = json.Replace("  \"Scope\": \"Generic\",\n", "", StringComparison.Ordinal);
        var read = RegistryBackupCodec.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.Equal("v.0.1.a-beta", read.ApplicationVersion);
        Assert.Equal(RegistryScope.Generic, read.Scope);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public void CorruptedBackupIsRejected(string json) =>
        Assert.Throws<FormatException>(() => RegistryBackupCodec.Deserialize(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void UnsupportedSchemaAndInvalidKindAreRejected()
    {
        var json = Encoding.UTF8.GetString(RegistryBackupCodec.Serialize(Backup(RegistryValue.FromDWord(2))));
        Assert.Throws<FormatException>(() => RegistryBackupCodec.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2"))));
        Assert.Throws<FormatException>(() => RegistryBackupCodec.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"Kind\": \"DWord\"", "\"Kind\": \"Binary\""))));
    }

    [Fact]
    public void MissingUnknownAndDuplicateFieldsAreRejected()
    {
        var json = Encoding.UTF8.GetString(RegistryBackupCodec.Serialize(Backup(RegistryValue.Missing)));
        Assert.Throws<FormatException>(() => RegistryBackupCodec.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"Name\": \"TestValue\",", ""))));
        Assert.Throws<FormatException>(() => RegistryBackupCodec.Deserialize(Encoding.UTF8.GetBytes(json.Replace("  \"Name\": \"TestValue\",", "  \"Name\": \"TestValue\", \"Unexpected\": 1,"))));
        Assert.Throws<FormatException>(() => RegistryBackupCodec.Deserialize(Encoding.UTF8.GetBytes(json.Replace("  \"Name\": \"TestValue\",", "  \"Name\": \"TestValue\", \"Name\": \"TestValue\","))));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("C:\\Windows")]
    [InlineData("CON")]
    [InlineData("ABCDEF")]
    public void InvalidBackupIdIsRejected(string id) => Assert.Throws<FormatException>(() => RegistryBackupId.Validate(id));

    [Fact]
    public void BackupCannotEscapePolicyTree()
    {
        Assert.Throws<FormatException>(() => (Backup(RegistryValue.Missing) with { KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run" }).Validate());
        Assert.Throws<FormatException>(() => (Backup(RegistryValue.Missing) with { KeyPath = @"Software\Policies\Test\..\Run" }).Validate());
        Assert.Throws<FormatException>(() => (Backup(RegistryValue.Missing) with { Name = @"..\Other" }).Validate());
    }

    [Fact]
    public async Task ApplyBacksUpBeforeWriteAndRollbackRestoresExistingValue()
    {
        var rig = new Rig();
        rig.Registry.Values[(Path, Name)] = RegistryValue.FromDWord(2);
        Assert.Equal(Outcome.Change, (await rig.Apply()).Single().Outcome);
        Assert.Equal(["read", "backup", "load", "read", "write", "read"], rig.Events);
        Assert.Equal(RegistryValue.FromDWord(1), rig.Registry.Values[(Path, Name)]);
        var backup = Assert.Single(rig.Backups.Items.Values);
        Assert.Equal(RegistryValue.FromDWord(2), backup.Previous);
        Assert.Equal(RegistryValue.FromDWord(1), backup.Target);
        Assert.Equal("v.0.3.c-beta", backup.ApplicationVersion);
        Assert.Equal(Planner.Create(ProfileLoader.Load(Profile)).ProfileHash, backup.ProfileHash);
        Assert.Equal(Outcome.Restore, (await rig.Rollback(backup.BackupId)).Outcome);
        Assert.Equal(RegistryValue.FromDWord(2), rig.Registry.Values[(Path, Name)]);
        Assert.Equal(Outcome.Skip, (await rig.Rollback(backup.BackupId)).Outcome);
    }

    [Fact]
    public async Task RollbackDeletesOnlyValueCreatedByApply()
    {
        var rig = new Rig();
        rig.Registry.Values[(Path, "Unrelated")] = RegistryValue.FromString("keep");
        Assert.Equal(Outcome.Change, (await rig.Apply()).Single().Outcome);
        var backup = Assert.Single(rig.Backups.Items.Values);
        Assert.False(backup.Previous.Exists);
        Assert.Equal(Outcome.Restore, (await rig.Rollback(backup.BackupId)).Outcome);
        Assert.False(rig.Registry.Values.ContainsKey((Path, Name)));
        Assert.Equal(RegistryValue.FromString("keep"), rig.Registry.Values[(Path, "Unrelated")]);
        Assert.Equal(Outcome.Skip, (await rig.Rollback(backup.BackupId)).Outcome);
    }

    [Fact]
    public async Task FailedBackupPreventsMutation()
    {
        var rig = new Rig(); rig.Backups.FailSave = true;
        Assert.Equal(Outcome.Fail, (await rig.Apply()).Single().Outcome);
        Assert.DoesNotContain("write", rig.Events);
        Assert.Empty(rig.State!.Completed);
    }

    [Fact]
    public async Task ConcurrentChangeAfterBackupPreventsWrite()
    {
        var rig = new Rig();
        rig.Backups.AfterSave = () => rig.Registry.Values[(Path, Name)] = RegistryValue.FromDWord(3);
        var result = (await rig.Apply()).Single();
        Assert.Equal(Outcome.Fail, result.Outcome);
        Assert.DoesNotContain("write", rig.Events);
        Assert.Equal(RegistryValue.FromDWord(3), rig.Registry.Values[(Path, Name)]);
        Assert.Contains(rig.Backups.Items.Keys.Single(), result.Message);
    }

    [Fact]
    public async Task UnreadableBackupPreventsWrite()
    {
        var rig = new Rig(); rig.Backups.FailLoad = true;
        var result = (await rig.Apply()).Single();
        Assert.Equal(Outcome.Fail, result.Outcome);
        Assert.DoesNotContain("write", rig.Events);
        Assert.Contains(rig.Backups.Items.Keys.Single(), result.Message);
    }

    [Fact]
    public async Task FailedWriteDoesNotCompleteOperation()
    {
        var rig = new Rig(); rig.Registry.FailWrite = true;
        var result = (await rig.Apply()).Single();
        Assert.Equal(Outcome.Fail, result.Outcome);
        Assert.Empty(rig.State!.Completed);
        Assert.Single(rig.Backups.Items);
        Assert.Contains(rig.Backups.Items.Keys.Single(), result.Message);
    }

    [Fact]
    public async Task FailedApplyVerificationDoesNotCompleteOperation()
    {
        var rig = new Rig(); rig.Registry.ReadAfterMutation = RegistryValue.Missing;
        Assert.Equal(Outcome.Fail, (await rig.Apply()).Single().Outcome);
        Assert.Empty(rig.State!.Completed);
        Assert.Single(rig.Backups.Items);
    }

    [Fact]
    public async Task FailedRollbackWriteAndVerificationReportFailure()
    {
        var rig = new Rig();
        var backup = Backup(RegistryValue.FromDWord(2));
        rig.Backups.Items.Add(backup.BackupId, backup);
        rig.Registry.Values[(Path, Name)] = backup.Target;
        rig.Registry.FailWrite = true;
        Assert.Equal(Outcome.Fail, (await rig.Rollback(backup.BackupId)).Outcome);
        rig.Registry.FailWrite = false;
        rig.Registry.ReadAfterMutation = backup.Target;
        Assert.Equal(Outcome.Fail, (await rig.Rollback(backup.BackupId)).Outcome);
    }

    [Fact]
    public async Task RollbackRefusesLaterUnrelatedChanges()
    {
        var rig = new Rig();
        var backup = Backup(RegistryValue.FromDWord(2));
        rig.Backups.Items.Add(backup.BackupId, backup);
        rig.Registry.Values[(Path, Name)] = RegistryValue.FromDWord(3);
        Assert.Equal(Outcome.Fail, (await rig.Rollback(backup.BackupId)).Outcome);
        Assert.Equal(RegistryValue.FromDWord(3), rig.Registry.Values[(Path, Name)]);
        Assert.DoesNotContain("write", rig.Events);
    }

    [Fact]
    public async Task ApplyAndRollbackDryRunsNeverMutate()
    {
        var rig = new Rig();
        Assert.Equal(Outcome.Change, (await rig.Apply(true)).Single().Outcome);
        Assert.Empty(rig.Backups.Items);
        Assert.DoesNotContain(rig.Events, x => x is "write" or "delete" or "backup");
        Assert.Null(rig.State);
        var backup = Backup(RegistryValue.Missing);
        rig.Backups.Items.Add(backup.BackupId, backup);
        rig.Registry.Values[(Path, Name)] = backup.Target;
        Assert.Equal(Outcome.Restore, (await rig.Rollback(backup.BackupId, true)).Outcome);
        Assert.True(rig.Registry.Values.ContainsKey((Path, Name)));
        Assert.DoesNotContain("delete", rig.Events);
    }

    private static RegistryBackup Backup(RegistryValue previous) => new(1, RegistryBackupId.Create(), new string('A', 64),
        "v.0.3.c-beta", new string('a', 24), DateTimeOffset.UtcNow, RegistryHiveKind.CurrentUser,
        @"Software\Policies\WinRebuilderTests", RegistryViewKind.Registry64, Name, previous, RegistryValue.FromDWord(1));

    private sealed class Rig
    {
        public readonly List<string> Events = [];
        public readonly FakeRegistry Registry;
        public readonly FakeBackups Backups;
        public ExecutionState? State;
        private readonly IOperationLogger logger = new NullLogger();
        public Rig() { Registry = new FakeRegistry(Events); Backups = new FakeBackups(Events); }
        public Task<IReadOnlyList<OperationResult>> Apply(bool dryRun = false) =>
            new Executor([], Registry, Backups, new FakeState(this), logger, "v.0.3.c-beta")
                .RunAsync(Planner.Create(ProfileLoader.Load(Profile)), dryRun);
        public Task<RollbackResult> Rollback(string id, bool dryRun = false) =>
            new RegistryRollback(Registry, Backups, logger).RunAsync(id, dryRun);
    }
    private sealed class FakeRegistry(List<string> events) : IRegistryAccess
    {
        public readonly Dictionary<(string, string), RegistryValue> Values = [];
        public bool FailWrite;
        public RegistryValue? ReadAfterMutation;
        private bool mutated;
        public Task<RegistryValue> ReadAsync(string path, string name, CancellationToken ct)
        {
            events.Add("read");
            return Task.FromResult(mutated && ReadAfterMutation is not null ? ReadAfterMutation :
                Values.GetValueOrDefault((path, name), RegistryValue.Missing));
        }
        public Task WriteAsync(string path, string name, RegistryValue value, CancellationToken ct)
        {
            events.Add("write");
            if (FailWrite) throw new IOException("simulated registry write failure");
            Values[(path, name)] = value; mutated = true;
            return Task.CompletedTask;
        }
        public Task DeleteValueAsync(string path, string name, CancellationToken ct)
        {
            events.Add("delete");
            if (FailWrite) throw new IOException("simulated registry delete failure");
            Values.Remove((path, name)); mutated = true;
            return Task.CompletedTask;
        }
    }
    private sealed class FakeBackups(List<string> events) : IRegistryBackupStore
    {
        public readonly Dictionary<string, RegistryBackup> Items = [];
        public bool FailSave;
        public bool FailLoad;
        public Action? AfterSave;
        public Task SaveAsync(RegistryBackup backup, CancellationToken ct)
        {
            events.Add("backup");
            if (FailSave) throw new IOException("simulated backup write failure");
            Items.Add(backup.BackupId, backup);
            AfterSave?.Invoke();
            return Task.CompletedTask;
        }
        public Task<RegistryBackup> LoadAsync(string backupId, CancellationToken ct)
        {
            events.Add("load");
            if (FailLoad) throw new IOException("simulated backup read failure");
            return Task.FromResult(Items[backupId]);
        }
        public Task<IReadOnlyList<RegistryBackup>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RegistryBackup>>(Items.Values.ToArray());
    }
    private sealed class FakeState(Rig rig) : IExecutionStateStore
    {
        public Task<ExecutionState?> LoadAsync(string profileHash, CancellationToken ct) => Task.FromResult(rig.State);
        public Task SaveAsync(ExecutionState state, CancellationToken ct) { rig.State = state; return Task.CompletedTask; }
    }
    private sealed class NullLogger : IOperationLogger { public void Log(LogEntry entry) { } }
}
