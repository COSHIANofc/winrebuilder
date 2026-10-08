using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinRebuilder.Core;
using WinRebuilder.UI;
using Xunit;

namespace WinRebuilder.UI.Tests;

public sealed class UiWorkflowTests
{
    private const string Config = "version: 1\npackages:\n  - name: 7-Zip\n    provider: winget\n    id: 7zip.7zip\nexplorerPatcher:\n  enabled: true\n  provider: winget\n  packageId: valinet.ExplorerPatcher\n";

    [Fact]
    public void MainWindowCompositionRootConstructsOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { var app = new App(); app.InitializeComponent(); var window = new MainWindow(); Assert.NotNull(window.DataContext); window.Close(); }
            catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public async Task GuiLoadsEditsPlansAndRejectsInvalidSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "winrebuilder-ui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "config.yml");
            await File.WriteAllTextAsync(path, Config);
            var dialogs = new FakeDialogs();
            var runner = new FakeRunner();
            using var model = new MainViewModel(dialogs, path, _ => runner);
            await model.InitializeAsync();
            Assert.Equal(2, model.Packages.Count);
            Assert.Equal("WinRebuilder v.0.3.c-beta", model.WindowTitle);
            dialogs.Package = ("CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo");
            model.AddCommand.Execute(null);
            await Until(() => model.Packages.Count == 3);
            Assert.Equal(3, (await ConfigLoader.LoadAsync(path)).Profile.Packages.Count);
            model.SelectedPackage = model.Packages.Single(x => x.Name == "CrystalDiskInfo");
            model.RemoveCommand.Execute(null);
            await Until(() => model.Packages.Count == 2);
            Assert.Equal(2, (await ConfigLoader.LoadAsync(path)).Profile.Packages.Count);
            model.SelectedPackage = model.Packages.Single(x => x.Name == "7-Zip");
            model.InstallSelectedCommand.Execute(null);
            await Until(() => runner.Plans.Count == 1);
            Assert.Equal(["7-Zip"], runner.Plans[0].Operations.Select(x => x.Package?.Name));
            await Until(() => model.CanEdit);
            model.InstallAllCommand.Execute(null);
            await Until(() => runner.Plans.Count == 2);
            Assert.Equal(["7-Zip", "ExplorerPatcher"], runner.Plans[1].Operations.Select(x => x.Package?.Name));
            await Until(() => model.CanEdit);
            model.CheckStatusCommand.Execute(null);
            await Until(() => runner.Plans.Count == 3);
            await Until(() => model.CanEdit);
            Assert.True(runner.DryRuns[2]);
            Assert.All(model.Packages, row => Assert.Equal("Installed", row.Status));
            var invalid = Path.Combine(folder, "invalid.reg");
            await File.WriteAllTextAsync(invalid, "Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"Bad\"=\"x\"\r\n", Encoding.UTF8);
            dialogs.RegPath = invalid;
            model.SelectRegCommand.Execute(null);
            await Until(() => model.Status.StartsWith("Failed:", StringComparison.Ordinal));
            await Until(() => model.CanEdit);
            Assert.Equal("None", model.ExplorerSettingsFile);
            Assert.Contains("Failed:", model.Log.Last());
            var valid = Path.Combine(folder, "valid.reg");
            await File.WriteAllTextAsync(valid, "Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\r\n\"OldTaskbar\"=dword:00000002\r\n", Encoding.UTF8);
            dialogs.RegPath = valid;
            model.SelectRegCommand.Execute(null);
            await Until(() => model.ExplorerSettingsFile.StartsWith("settings/", StringComparison.Ordinal));
            Assert.Equal(model.ExplorerSettingsFile, (await ConfigLoader.LoadAsync(path)).Profile.ExplorerPatcher?.SettingsFile);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task PortableFirstLaunchCreatesConfigBesideExecutablePath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "winrebuilder-portable-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "config.yml");
            using var model = new MainViewModel(new FakeDialogs(), path, _ => new FakeRunner());
            await model.InitializeAsync();
            Assert.True(File.Exists(path));
            Assert.Equal(3, model.Packages.Count);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public (string Name, string Id)? Package;
        public string? RegPath;
        public (string Name, string Id)? NewPackage() => Package;
        public string? PickRegFile() => RegPath;
        public bool ConfirmRemove(string name) => true;
        public bool ConfirmRollback(RegistryBackup backup) => true;
    }

    private sealed class FakeRunner : IPlanRunner
    {
        public readonly List<ExecutionPlan> Plans = [];
        public readonly List<bool> DryRuns = [];
        public Task<IReadOnlyList<OperationResult>> RunAsync(ExecutionPlan plan, bool dryRun, CancellationToken ct = default)
        {
            Plans.Add(plan);
            DryRuns.Add(dryRun);
            return Task.FromResult<IReadOnlyList<OperationResult>>(plan.Operations.Select(x =>
                new OperationResult(x.Id, x.Type, Outcome.Skip, DateTimeOffset.UtcNow, "fake")).ToArray());
        }
    }
}
