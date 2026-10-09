using System.Windows;

namespace WinRebuilder.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;

    public MainWindow()
    {
        InitializeComponent();
        model = new MainViewModel(new Dialogs());
        DataContext = model;
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        Loaded += async (_, _) => await model.InitializeAsync();
        Closed += (_, _) => model.Dispose();
    }
}
