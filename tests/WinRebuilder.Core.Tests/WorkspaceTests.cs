using System.Text;
using WinRebuilder.Core;
using Xunit;

namespace WinRebuilder.Core.Tests;

public sealed class WorkspaceTests
{
    private const string Basic = "version: 1\npackages:\n  - name: 7-Zip\n    provider: winget\n    id: 7zip.7zip\n";
    private const string Reg = "Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\ExplorerPatcher]\r\n\"OldTaskbar\"=dword:00000002\r\n";

    [Fact]
    public async Task AddRemoveAndFailedReloadPreserveConfiguration()
    {
        await WithWorkspace(async (path, workspace) =>
        {
            var workflow = new SoftwareWorkflow(workspace, new FakeRunner());
            await workflow.AddWingetAsync("CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo");
            Assert.Equal(2, (await ConfigLoader.LoadAsync(path)).Profile.Packages.Count);
            Assert.True(File.Exists(path + ".bak"));
            var saved = await File.ReadAllTextAsync(path);
            await Assert.ThrowsAsync<FormatException>(() => workflow.AddWingetAsync("Other", "7zip.7zip"));
            await Assert.ThrowsAsync<FormatException>(() => workflow.AddWingetAsync("CrystalDiskInfo", "Vendor.Other"));
            Assert.Equal(saved, await File.ReadAllTextAsync(path));
            await workflow.RemoveAsync(workflow.Current.Profile.Packages.Single(x => x.Name == "7-Zip"));
            Assert.Single((await ConfigLoader.LoadAsync(path)).Profile.Packages);
            await File.WriteAllTextAsync(path, "version: [");
            await Assert.ThrowsAsync<FormatException>(() => workflow.ReloadAsync());
            Assert.Single(workflow.Current.Profile.Packages);
        });
    }

    [Fact]
    public async Task SelectionAndAllUseTheSamePlannerOrder()
    {
        await WithWorkspace(async (path, workspace) =>
        {
            await workspace.SetExplorerPatcherAsync(true, null);
            var runner = new FakeRunner();
            var workflow = new SoftwareWorkflow(workspace, runner);
            await workflow.InstallSelectedAsync(workflow.Current.Profile.Packages.Single(x => x.Name == "7-Zip"));
            Assert.Single(runner.Plan!.Operations);
            await workflow.InstallAllAsync();
            Assert.Equal(["7-Zip", "ExplorerPatcher"], runner.Plan!.Operations.Select(x => x.Package!.Name));
            await workflow.InstallSelectedAsync(workflow.Current.Profile.Packages.Single(x => x.Name == "ExplorerPatcher"));
            Assert.Equal(["7-Zip", "ExplorerPatcher"], runner.Plan!.Operations.Select(x => x.Package!.Name));
            await workflow.CheckStatusAsync();
            Assert.True(runner.DryRun);
            Assert.Equal(["7-Zip", "ExplorerPatcher"], runner.Plan!.Operations.Select(x => x.Package!.Name));
        });
    }

    [Fact]
    public async Task WorkflowPropagatesExecutionFailureWithoutChangingConfig()
    {
        await WithWorkspace(async (path, workspace) =>
        {
            var before = await File.ReadAllTextAsync(path);
            var workflow = new SoftwareWorkflow(workspace, new FakeRunner { Fail = true });
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.InstallAllAsync());
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task ValidatedRegCopyAndFailedCopyLeaveConfigIntact()
    {
        await WithWorkspace(async (path, workspace) =>
        {
            var source = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "source.reg");
            await File.WriteAllTextAsync(source, Reg);
            var relative = await workspace.CopyExplorerSettingsAsync(source);
            Assert.StartsWith("settings/", relative);
            Assert.Equal(relative, workspace.Current.Profile.ExplorerPatcher?.SettingsFile);
            Assert.True(File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, relative)));
            var saved = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(source, Reg + "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"Bad\"=\"x\"");
            await Assert.ThrowsAsync<FormatException>(() => workspace.CopyExplorerSettingsAsync(source));
            Assert.Equal(saved, await File.ReadAllTextAsync(path));
        });
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    public void ParseBytesAcceptsStrictEncodings(string encoding)
    {
        byte[] bytes = encoding switch
        {
            "utf8" => Encoding.UTF8.GetBytes(Reg),
            "utf8bom" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Reg)],
            _ => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Reg)]
        };
        Assert.Single(ExplorerRegParser.ParseBytes(bytes));
    }

    [Fact]
    public void ParseBytesRejectsMalformedEncodingAndUnsafeFile()
    {
        Assert.Throws<FormatException>(() => ExplorerRegParser.ParseBytes([0xFF, 0xFE, 0x01]));
        Assert.Throws<FormatException>(() => ExplorerRegParser.ParseBytes([0xEF, 0xBB, 0xBF, 0xFF]));
        Assert.Throws<FormatException>(() => ExplorerRegParser.ParseBytes(Encoding.UTF8.GetBytes(Reg +
            "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"Bad\"=\"x\"")));
    }

    private static async Task WithWorkspace(Func<string, ConfigurationWorkspace, Task> action)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winrebuilder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "config.yml");
            await File.WriteAllTextAsync(path, Basic);
            await action(path, await ConfigurationWorkspace.OpenAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeRunner : IPlanRunner
    {
        public ExecutionPlan? Plan;
        public bool Fail;
        public bool DryRun;
        public Task<IReadOnlyList<OperationResult>> RunAsync(ExecutionPlan plan, bool dryRun, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("install failed");
            Plan = plan;
            DryRun = dryRun;
            return Task.FromResult<IReadOnlyList<OperationResult>>(plan.Operations.Select(x =>
                new OperationResult(x.Id, x.Type, Outcome.Skip, DateTimeOffset.UtcNow, "test")).ToArray());
        }
    }
}
