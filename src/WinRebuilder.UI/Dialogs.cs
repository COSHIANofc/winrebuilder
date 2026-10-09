using Microsoft.Win32;
using System.Windows;
using WinRebuilder.Core;

namespace WinRebuilder.UI;

internal interface IUserDialogs
{
    (string Name, string Id)? NewPackage();
    string? PickRegFile();
    bool ConfirmRemove(string name);
    bool ConfirmRollback(RegistryBackup backup);
}

internal sealed class Dialogs : IUserDialogs
{
    private static Localization L => Localization.Instance;
    public (string Name, string Id)? NewPackage()
    {
        var window = new Window { Title = L["DialogAddTitle"], Width = 420, Height = 250,
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize };
        window.SourceInitialized += (_, _) => WindowAppearance.Apply(window);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
        var name = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var id = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = L["DisplayName"] });
        panel.Children.Add(name);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = L["ExactWingetId"] });
        panel.Children.Add(id);
        var add = new System.Windows.Controls.Button { Content = L["AddSoftware"], IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        add.Click += (_, _) => { window.DialogResult = true; window.Close(); };
        panel.Children.Add(add);
        window.Content = panel;
        return window.ShowDialog() == true ? (name.Text, id.Text) : null;
    }

    public string? PickRegFile()
    {
        var picker = new OpenFileDialog { Filter = L["RegFilter"] + "|*.reg", CheckFileExists = true,
            Multiselect = false, Title = L["RegPickerTitle"] };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }

    public bool ConfirmRemove(string name) => MessageBox.Show(
        L.Format("RemoveConfirm", name), L["RemoveTitle"],
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ConfirmRollback(RegistryBackup backup) => MessageBox.Show(
        L.Format("RestoreConfirm", backup.FullPath, backup.Name),
        L["RestoreTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
