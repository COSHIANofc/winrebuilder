using WinRebuilder.Core;
using WinRebuilder.Windows;
using Xunit;

namespace WinRebuilder.Windows.Tests;

public sealed class WindowsOnlyTests
{
    [Fact]
    public void ProgramDataRootIsStable()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.EndsWith("WinRebuilder", WindowsPaths.Root, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(WindowsPaths.Root, "backups"), WindowsPaths.Backups);
    }

    [Fact]
    public async Task BackupStoreUsesSafeNamesAndRejectsCorruption()
    {
        var folder = Path.Combine(Path.GetTempPath(), "winrebuilder-backup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonRegistryBackupStore(folder);
            var backup = new RegistryBackup(1, RegistryBackupId.Create(), new string('A', 64), "v.1.0.a-pre1",
                new string('a', 24), DateTimeOffset.UtcNow, RegistryHiveKind.CurrentUser,
                @"Software\Policies\WinRebuilderTests", RegistryViewKind.Registry64, "Value",
                RegistryValue.Missing, RegistryValue.FromDWord(1));
            await store.SaveAsync(backup, default);
            Assert.Equal(backup, await store.LoadAsync(backup.BackupId, default));
            Assert.Single(await store.ListAsync(default));
            Assert.Single(Directory.GetFiles(folder));
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(backup, default));
            await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync("../escape", default));
            await File.WriteAllTextAsync(Path.Combine(folder, backup.BackupId + ".json"), "broken");
            await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync(backup.BackupId, default));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
