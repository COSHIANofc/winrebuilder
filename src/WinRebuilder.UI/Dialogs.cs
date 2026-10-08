using Microsoft.Win32;
using System.Windows;

namespace WinRebuilder.UI;

internal interface IUserDialogs
{
    (string Name, string Id)? NewPackage();
    string? PickRegFile();
    bool ConfirmRemove(string name);
}

internal sealed class Dialogs : IUserDialogs
{
    public (string Name, string Id)? NewPackage()
    {
        var window = new Window { Title = "Add winget software", Width = 380, Height = 190,
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var name = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var id = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Display name" });
        panel.Children.Add(name);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Exact winget package ID" });
        panel.Children.Add(id);
        var add = new System.Windows.Controls.Button { Content = "Add", IsDefault = true, Width = 80 };
        add.Click += (_, _) => { window.DialogResult = true; window.Close(); };
        panel.Children.Add(add);
        window.Content = panel;
        return window.ShowDialog() == true ? (name.Text, id.Text) : null;
    }

    public string? PickRegFile()
    {
        var picker = new OpenFileDialog { Filter = "Registry settings (*.reg)|*.reg", CheckFileExists = true,
            Multiselect = false, Title = "Choose ExplorerPatcher settings" };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }

    public bool ConfirmRemove(string name) => MessageBox.Show(
        $"Remove {name} from config.yml? This does not uninstall it from Windows.", "Remove software",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
