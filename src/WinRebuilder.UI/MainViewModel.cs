using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinRebuilder.Core;
using WinRebuilder.Windows;

namespace WinRebuilder.UI;

internal sealed class SoftwareRow(PackageSpec package) : INotifyPropertyChanged
{
    public PackageSpec Package { get; } = package;
    public string Name => Package.Name;
    public string Provider => Package.Provider.ToString().ToLowerInvariant();
    public string Id => Package.Id ?? Package.Repository ?? Package.Url?.ToString() ?? "";
    private string statusKey = "Pending";
    public string StatusKey { get => statusKey; set { statusKey = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public string Status => Localization.Instance[statusKey];
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(Status)));
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
    private string statusKey = "StatusLoading";
    private object[] statusArgs = [];
    private string explorerSettingsFile = "None";
    private string page = "Software";
    private readonly string configPath;
    private readonly Func<ConfigurationWorkspace, IPlanRunner>? runnerFactory;
    private RegistryBackup? selectedBackup;

    public MainViewModel(IUserDialogs dialogs, string? configPath = null,
        Func<ConfigurationWorkspace, IPlanRunner>? runnerFactory = null)
    {
        this.dialogs = dialogs;
        this.configPath = configPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "config.yml");
        this.runnerFactory = runnerFactory;
        AddCommand = Command(() => _ = RunAsync(AddAsync), () => Ready);
        RemoveCommand = Command(() => _ = RunAsync(RemoveAsync), () => Ready && SelectedPackage is not null && SelectedPackage.Name != "ExplorerPatcher");
        InstallSelectedCommand = Command(() => _ = RunAsync(InstallSelectedAsync), () => Ready && SelectedPackage is not null);
        InstallAllCommand = Command(() => _ = RunAsync(InstallAllAsync), () => Ready && Packages.Count > 0);
        CheckStatusCommand = Command(() => _ = RunAsync(CheckStatusAsync), () => Ready && Packages.Count > 0);
        ReloadCommand = Command(() => _ = RunAsync(ReloadAsync), () => Ready);
        CancelCommand = Command(() => cancellation?.Cancel(), () => busy);
        SelectRegCommand = Command(() => _ = RunAsync(SelectRegAsync), () => Ready && ExplorerPatcherEnabled);
        ApplySettingsCommand = Command(() => _ = RunAsync(ApplySettingsAsync), () => Ready && ExplorerPatcherEnabled && explorerSettingsFile != "None");
        SoftwarePageCommand = Command(() => Page = "Software");
        ExplorerPageCommand = Command(() => Page = "ExplorerPatcher");
        ConfigurationPageCommand = Command(() => Page = "Configuration");
        AboutPageCommand = Command(() => Page = "About");
        LoadBackupsCommand = Command(() => _ = RunAsync(LoadBackupsAsync), () => Ready);
        RollbackCommand = Command(() => _ = RunAsync(RollbackAsync), () => Ready && SelectedBackup is not null);
        ToggleThemeCommand = Command(() => { ThemePalette.Toggle(); Changed(nameof(ThemeLabel)); Changed(nameof(SoftwareNavBrush)); Changed(nameof(ExplorerNavBrush)); Changed(nameof(ConfigurationNavBrush)); Changed(nameof(AboutNavBrush)); Changed(nameof(StatusBrush)); });
        JapaneseCommand = Command(() => SelectLanguage("ja"));
        EnglishCommand = Command(() => SelectLanguage("en"));
        commands = [AddCommand, RemoveCommand, InstallSelectedCommand, InstallAllCommand, CheckStatusCommand,
            ReloadCommand, CancelCommand, SelectRegCommand, ApplySettingsCommand, LoadBackupsCommand, RollbackCommand];
    }

    public ObservableCollection<SoftwareRow> Packages { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<RegistryBackup> Backups { get; } = [];
    public RegistryBackup? SelectedBackup { get => selectedBackup; set { selectedBackup = value; Changed(); RefreshCommands(); } }
    public string PublicVersion => typeof(Executor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
    public string WindowTitle => "WinRebuilder " + PublicVersion;
    public string ThemeLabel => Localization.Instance[ThemePalette.IsDark ? "ThemeLight" : "ThemeDark"];
    public string LanguageDisplay => Localization.Instance["LanguageLabel"] + ": " +
        Localization.Instance[Localization.Instance.Language == "ja" ? "LanguageJapanese" : "LanguageEnglish"];
    public string ConfigurationPath => configPath;
    public string Page { get => page; private set { page = value; Changed(); Changed(nameof(SoftwareVisibility)); Changed(nameof(ExplorerVisibility)); Changed(nameof(ConfigurationVisibility)); Changed(nameof(AboutVisibility)); Changed(nameof(SoftwareNavBrush)); Changed(nameof(ExplorerNavBrush)); Changed(nameof(ConfigurationNavBrush)); Changed(nameof(AboutNavBrush)); } }
    private Brush NavBrush(string name) => Page == name ? Application.Current?.Resources["SelectionBrush"] as Brush ?? Brushes.LightBlue : Brushes.Transparent;
    public Brush SoftwareNavBrush => NavBrush("Software");
    public Brush ExplorerNavBrush => NavBrush("ExplorerPatcher");
    public Brush ConfigurationNavBrush => NavBrush("Configuration");
    public Brush AboutNavBrush => NavBrush("About");
    public Visibility SoftwareVisibility => Page == "Software" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExplorerVisibility => Page == "ExplorerPatcher" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConfigurationVisibility => Page == "Configuration" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AboutVisibility => Page == "About" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptySoftwareVisibility => Packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public SoftwareRow? SelectedPackage { get => selectedPackage; set { selectedPackage = value; Changed(); RefreshCommands(); } }
    public string Status => Localization.Instance.Format(statusKey, statusArgs);
    private void SetStatus(string key, params object[] args) { statusKey = key; statusArgs = args; Changed(nameof(Status)); Changed(nameof(StatusBrush)); }
    public Brush StatusBrush => statusKey is "StatusFailed" or "StatusOpenFailed" or "StatusInstallFailed" or "StatusCheckFailed" or "StatusRollbackFailed" ?
        Application.Current?.Resources["ErrorBrush"] as Brush ?? Brushes.IndianRed :
        Application.Current?.Resources["MutedBrush"] as Brush ?? Brushes.Gray;
    public string ExplorerSettingsFile { get => explorerSettingsFile == "None" ? Localization.Instance["None"] : explorerSettingsFile; private set { explorerSettingsFile = value; Changed(); RefreshCommands(); } }
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
    public UiCommand CheckStatusCommand { get; }
    public UiCommand ReloadCommand { get; }
    public UiCommand CancelCommand { get; }
    public UiCommand SelectRegCommand { get; }
    public UiCommand ApplySettingsCommand { get; }
    public UiCommand SoftwarePageCommand { get; }
    public UiCommand ExplorerPageCommand { get; }
    public UiCommand ConfigurationPageCommand { get; }
    public UiCommand AboutPageCommand { get; }
    public UiCommand LoadBackupsCommand { get; }
    public UiCommand RollbackCommand { get; }
    public UiCommand ToggleThemeCommand { get; }
    public UiCommand JapaneseCommand { get; }
    public UiCommand EnglishCommand { get; }
    private void SelectLanguage(string language)
    {
        try { Localization.Instance.Select(language); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("StatusFailed", e.Message);
            AppendLog(Status);
            return;
        }
        Changed(nameof(ThemeLabel)); Changed(nameof(LanguageDisplay)); Changed(nameof(Status)); Changed(nameof(ExplorerSettingsFile));
        foreach (var row in Packages) row.RefreshLanguage();
    }
    private bool Ready => workflow is not null && !busy;
    public bool CanEdit => Ready;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync()
    {
        try
        {
            ConfigurationBootstrap.Ensure(configPath);
            var workspace = await ConfigurationWorkspace.OpenAsync(configPath);
            var progress = new Progress<LogEntry>(entry =>
            {
                AppendLog($"{Localization.Instance["Outcome" + entry.Outcome]}: {entry.Message}");
            });
            IPlanRunner runner;
            if (runnerFactory is null)
            {
                session = new WindowsExecutionSession(new ProgressLogger(progress));
                runner = session.Executor;
            }
            else runner = runnerFactory(workspace);
            workflow = new SoftwareWorkflow(workspace, runner);
            RefreshProfile();
            SetStatus("StatusReady", configPath);
        }
        catch (Exception e) { SetStatus("StatusOpenFailed", e.Message); }
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
        var previous = Packages.ToDictionary(x => x.Package, x => x.StatusKey);
        Packages.Clear();
        foreach (var package in workflow!.Current.Profile.Packages)
            Packages.Add(new SoftwareRow(package) { StatusKey = previous.GetValueOrDefault(package, "Pending") });
        Changed(nameof(EmptySoftwareVisibility));
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
        catch (OperationCanceledException) { Changed(nameof(ExplorerPatcherEnabled)); SetStatus("StatusCancelled"); }
        catch (Exception e)
        {
            foreach (var row in Packages.Where(x => x.StatusKey == "Installing")) row.StatusKey = "FailedRow";
            Changed(nameof(ExplorerPatcherEnabled));
            SetStatus("StatusFailed", e.Message);
            AppendLog(Status);
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
        RefreshProfile(); SetStatus("StatusAdded");
    }

    private async Task RemoveAsync(CancellationToken ct)
    {
        var selected = SelectedPackage;
        if (selected is null || !dialogs.ConfirmRemove(selected.Name)) return;
        await workflow!.RemoveAsync(selected.Package, ct);
        RefreshProfile(); SetStatus("StatusRemoved");
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await workflow!.ReloadAsync(ct);
        RefreshProfile(); SetStatus("StatusReloaded");
    }

    private async Task InstallSelectedAsync(CancellationToken ct)
    {
        var selected = SelectedPackage;
        if (selected is null) return;
        selected.StatusKey = "Installing"; SetStatus("StatusInstalling", selected.Name);
        var results = await workflow!.InstallSelectedAsync(selected.Package, ct);
        ShowResults(results);
    }

    private async Task InstallAllAsync(CancellationToken ct)
    {
        SetStatus("StatusInstallingAll");
        var results = await workflow!.InstallAllAsync(ct);
        ShowResults(results);
    }

    private async Task CheckStatusAsync(CancellationToken ct)
    {
        SetStatus("StatusChecking");
        var results = await workflow!.CheckStatusAsync(ct);
        var operations = Planner.Create(workflow.Current).Operations;
        foreach (var result in results)
        {
            var package = operations.FirstOrDefault(x => x.Id == result.OperationId)?.Package;
            var row = Packages.FirstOrDefault(x => x.Package == package);
            if (row is not null) row.StatusKey = result.Outcome switch
            {
                Outcome.Skip => "Installed", Outcome.Install => "NotInstalled", Outcome.Fail => "CheckFailed", _ => "Outcome" + result.Outcome
            };
        }
        SetStatus(results.Any(x => x.Outcome == Outcome.Fail) ? "StatusCheckFailed" : "StatusUpdated");
    }

    private async Task SelectRegAsync(CancellationToken ct)
    {
        var source = dialogs.PickRegFile();
        if (source is null) return;
        await workflow!.SelectExplorerSettingsAsync(source, ct);
        RefreshProfile(); SetStatus("StatusRegSelected");
    }

    private async Task ApplySettingsAsync(CancellationToken ct)
    {
        SetStatus("StatusApplying");
        ShowResults(await workflow!.ApplyExplorerSettingsAsync(ct));
    }

    private async Task LoadBackupsAsync(CancellationToken ct)
    {
        var items = await new JsonRegistryBackupStore().ListAsync(ct);
        Backups.Clear();
        foreach (var item in items.OrderByDescending(x => x.Timestamp)) Backups.Add(item);
        SetStatus("StatusBackupsLoaded", Backups.Count);
    }

    private async Task RollbackAsync(CancellationToken ct)
    {
        var backup = SelectedBackup;
        if (backup is null || !dialogs.ConfirmRollback(backup)) return;
        var logger = new ProgressLogger(new Progress<LogEntry>(entry => AppendLog($"{Localization.Instance["Outcome" + entry.Outcome]}: {entry.Message}")));
        var rollback = new RegistryRollback(new WindowsRegistry(), new JsonRegistryBackupStore(), logger,
            new ExplorerWindowsRegistry());
        var result = await rollback.RunAsync(backup.BackupId, false, ct);
        SetStatus(result.Outcome == Outcome.Fail ? "StatusRollbackFailed" : "StatusRollback", result.Message);
    }

    private void ShowResults(IReadOnlyList<OperationResult> results)
    {
        var operations = Planner.Create(workflow!.Current);
        foreach (var result in results)
        {
            var package = operations.Operations.FirstOrDefault(x => x.Id == result.OperationId)?.Package;
            var row = Packages.FirstOrDefault(x => x.Package == package);
            if (row is not null) row.StatusKey = result.Outcome switch
            {
                Outcome.Install => "Installed", Outcome.Skip => "AlreadyInstalled", Outcome.Fail => "FailedRow", _ => "Outcome" + result.Outcome
            };
        }
        SetStatus(results.Any(x => x.Outcome == Outcome.Fail) ? "StatusInstallFailed" : "StatusCompleted");
    }

    public void Dispose() { cancellation?.Cancel(); session?.Dispose(); }

    private void AppendLog(string entry)
    {
        Log.Add(entry);
        if (Log.Count > 300) Log.RemoveAt(0);
    }

    private sealed class ProgressLogger(IProgress<LogEntry> progress) : IOperationLogger
    { public void Log(LogEntry entry) => progress.Report(entry); }
}
