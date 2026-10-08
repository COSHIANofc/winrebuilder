namespace WinRebuilder.Core;

public sealed class SoftwareWorkflow(ConfigurationWorkspace workspace, IPlanRunner runner)
{
    public LoadedProfile Current => workspace.Current;
    public Task ReloadAsync(CancellationToken ct = default) => workspace.ReloadAsync(ct);

    public Task AddWingetAsync(string name, string id, CancellationToken ct = default) =>
        workspace.AddPackageAsync(new PackageSpec(name, PackageProvider.Winget, id, null, null, null,
            null, null, null, null, Phase.Normal), ct);

    public Task RemoveAsync(PackageSpec package, CancellationToken ct = default) => workspace.RemovePackageAsync(package, ct);

    public Task SetExplorerPatcherAsync(bool enabled, CancellationToken ct = default) =>
        workspace.SetExplorerPatcherAsync(enabled, enabled ? workspace.Current.Profile.ExplorerPatcher?.SettingsFile : null, ct);

    public Task<string> SelectExplorerSettingsAsync(string source, CancellationToken ct = default) =>
        workspace.CopyExplorerSettingsAsync(source, ct);

    public Task<IReadOnlyList<OperationResult>> InstallSelectedAsync(PackageSpec package, CancellationToken ct = default) =>
        RunAsync(x => x.Package == package ||
            package is { Name: "ExplorerPatcher", Phase: Phase.Shell } &&
            x.Package is { Phase: Phase.Normal }, ct);

    public Task<IReadOnlyList<OperationResult>> InstallAllAsync(CancellationToken ct = default) =>
        RunAsync(x => x.Type == OperationType.Package, ct);

    public Task<IReadOnlyList<OperationResult>> CheckStatusAsync(CancellationToken ct = default) =>
        RunAsync(x => x.Type == OperationType.Package, ct, true);

    public Task<IReadOnlyList<OperationResult>> ApplyExplorerSettingsAsync(CancellationToken ct = default) =>
        RunAsync(x => x.Type == OperationType.ExplorerPatcherSetting ||
            x.Type == OperationType.Package, ct);

    private Task<IReadOnlyList<OperationResult>> RunAsync(Func<PlannedOperation, bool> select, CancellationToken ct, bool dryRun = false)
    {
        var plan = Planner.Create(workspace.Current);
        var operations = plan.Operations.Where(select).ToArray();
        if (operations.Length == 0) throw new InvalidOperationException("No matching configured operation.");
        return runner.RunAsync(plan with { Operations = operations }, dryRun, ct);
    }
}
