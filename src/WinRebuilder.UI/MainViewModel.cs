using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WinRebuilder.Core;
using WinRebuilder.Windows;

namespace WinRebuilder.UI;

internal sealed class SoftwareRow(PackageSpec package) : INotifyPropertyChanged
{
    public PackageSpec Package { get; } = package;
    public string Name => Package.Name;
    public string Provider => Package.Provider.ToString().ToLowerInvariant();
    public string Id => Package.Id ?? Package.Repository ?? Package.Url?.ToString() ?? "";
    private string status = "Pending";
    public string Status { get => status; set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class UiCommand(Action action, Func<bool>? enabled = null) : ICommand
{
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IUserDialogs dialogs;
    private readonly UiCommand[] commands;
    private WindowsExecutionSession? session;
    private SoftwareWorkflow? workflow;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool explorerPatcherEnabled;
    private SoftwareRow? selectedPackage;
    private string status = "Loading config.yml…";
    private string explorerSettingsFile = "None";

    public MainViewModel(IUserDialogs dialogs)
    {
        this.dialogs = dialogs;
        AddCommand = Command(() => _ = RunAsync(AddAsync), () => Ready);
        RemoveCommand = Command(() => _ = RunAsync(RemoveAsync), () => Ready && SelectedPackage is not null && SelectedPackage.Name != "ExplorerPatcher");
        InstallSelectedCommand = Command(() => _ = RunAsync(InstallSelectedAsync), () => Ready && SelectedPackage is not null);
        InstallAllCommand = Command(() => _ = RunAsync(InstallAllAsync), () => Ready);
        ReloadCommand = Command(() => _ = RunAsync(ReloadAsync), () => Ready);
        CancelCommand = Command(() => cancellation?.Cancel(), () => busy);
        SelectRegCommand = Command(() => _ = RunAsync(SelectRegAsync), () => Ready && ExplorerPatcherEnabled);
        ApplySettingsCommand = Command(() => _ = RunAsync(ApplySettingsAsync), () => Ready && ExplorerPatcherEnabled && ExplorerSettingsFile != "None");
        commands = [AddCommand, RemoveCommand, InstallSelectedCommand, InstallAllCommand,
            ReloadCommand, CancelCommand, SelectRegCommand, ApplySettingsCommand];
    }

    public ObservableCollection<SoftwareRow> Packages { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public SoftwareRow? SelectedPackage { get => selectedPackage; set { selectedPackage = value; Changed(); RefreshCommands(); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string ExplorerSettingsFile { get => explorerSettingsFile; private set { explorerSettingsFile = value; Changed(); RefreshCommands(); } }
    public bool ExplorerPatcherEnabled
    {
        get => explorerPatcherEnabled;
        set
        {
            if (explorerPatcherEnabled == value) return;
            if (!Ready) return;
            _ = RunAsync(async ct =>
            {
                await workflow!.SetExplorerPatcherAsync(value, ct);
                RefreshProfile();
            });
        }
    }
    public UiCommand AddCommand { get; }
    public UiCommand RemoveCommand { get; }
    public UiCommand InstallSelectedCommand { get; }
    public UiCommand InstallAllCommand { get; }
    public UiCommand ReloadCommand { get; }
    public UiCommand CancelCommand { get; }
    public UiCommand SelectRegCommand { get; }
    public UiCommand ApplySettingsCommand { get; }
    private bool Ready => workflow is not null && !busy;
    public bool CanEdit => Ready;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync()
    {
        try
        {
            var config = System.IO.Path.Combine(AppContext.BaseDirectory, "config.yml");
            var workspace = await ConfigurationWorkspace.OpenAsync(config);
            var progress = new Progress<LogEntry>(entry =>
            {
                Log.Add($"{entry.Outcome}: {entry.Message}");
                if (Log.Count > 300) Log.RemoveAt(0);
            });
            session = new WindowsExecutionSession(new ProgressLogger(progress));
            workflow = new SoftwareWorkflow(workspace, session.Executor);
            RefreshProfile();
            Status = $"Ready: {config}";
        }
        catch (Exception e) { Status = "Failed to open config.yml: " + e.Message; }
        RefreshCommands();
    }

    private UiCommand Command(Action action, Func<bool>? enabled = null) => new(action, enabled);
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void RefreshCommands()
    {
        Changed(nameof(CanEdit));
        if (commands is not null) foreach (var command in commands) command.Refresh();
    }

    private void RefreshProfile()
    {
        var previous = Packages.ToDictionary(x => x.Package, x => x.Status);
        Packages.Clear();
        foreach (var package in workflow!.Current.Profile.Packages)
            Packages.Add(new SoftwareRow(package) { Status = previous.GetValueOrDefault(package, "Pending") });
        explorerPatcherEnabled = workflow.Current.Profile.ExplorerPatcher?.Enabled == true;
        Changed(nameof(ExplorerPatcherEnabled));
        ExplorerSettingsFile = workflow.Current.Profile.ExplorerPatcher?.SettingsFile ?? "None";
        RefreshCommands();
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (!Ready) return;
        busy = true;
        cancellation = new CancellationTokenSource();
        RefreshCommands();
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { Changed(nameof(ExplorerPatcherEnabled)); Status = "Cancelled."; }
        catch (Exception e)
        {
            foreach (var row in Packages.Where(x => x.Status == "Installing")) row.Status = "Failed";
            Changed(nameof(ExplorerPatcherEnabled));
            Status = "Failed: " + e.Message;
            Log.Add(Status);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null; busy = false; RefreshCommands();
        }
    }

    private async Task AddAsync(CancellationToken ct)
    {
        var input = dialogs.NewPackage();
        if (input is null) return;
        await workflow!.AddWingetAsync(input.Value.Name, input.Value.Id, ct);
        RefreshProfile(); Status = "Added to config.yml.";
    }

    private async Task RemoveAsync(CancellationToken ct)
    {
        var selected = SelectedPackage;
        if (selected is null || !dialogs.ConfirmRemove(selected.Name)) return;
        await workflow!.RemoveAsync(selected.Package, ct);
        RefreshProfile(); Status = "Removed from config.yml; application remains installed.";
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await workflow!.ReloadAsync(ct);
        RefreshProfile(); Status = "Reloaded config.yml.";
    }

    private async Task InstallSelectedAsync(CancellationToken ct)
    {
        var selected = SelectedPackage;
        if (selected is null) return;
        selected.Status = "Installing"; Status = "Installing " + selected.Name;
        var results = await workflow!.InstallSelectedAsync(selected.Package, ct);
        ShowResults(results);
    }

    private async Task InstallAllAsync(CancellationToken ct)
    {
        Status = "Installing configured software…";
        var results = await workflow!.InstallAllAsync(ct);
        ShowResults(results);
    }

    private async Task SelectRegAsync(CancellationToken ct)
    {
        var source = dialogs.PickRegFile();
        if (source is null) return;
        await workflow!.SelectExplorerSettingsAsync(source, ct);
        RefreshProfile(); Status = "Validated and copied ExplorerPatcher settings.";
    }

    private async Task ApplySettingsAsync(CancellationToken ct)
    {
        Status = "Applying ExplorerPatcher settings…";
        ShowResults(await workflow!.ApplyExplorerSettingsAsync(ct));
    }

    private void ShowResults(IReadOnlyList<OperationResult> results)
    {
        var operations = Planner.Create(workflow!.Current);
        foreach (var result in results)
        {
            var package = operations.Operations.FirstOrDefault(x => x.Id == result.OperationId)?.Package;
            var row = Packages.FirstOrDefault(x => x.Package == package);
            if (row is not null) row.Status = result.Outcome switch
            {
                Outcome.Install => "Installed", Outcome.Skip => "Already installed", Outcome.Fail => "Failed", _ => result.Outcome.ToString()
            };
        }
        Status = results.Any(x => x.Outcome == Outcome.Fail) ? "Installation failed; see log." : "Completed.";
    }

    public void Dispose() { cancellation?.Cancel(); session?.Dispose(); }

    private sealed class ProgressLogger(IProgress<LogEntry> progress) : IOperationLogger
    { public void Log(LogEntry entry) => progress.Report(entry); }
}
