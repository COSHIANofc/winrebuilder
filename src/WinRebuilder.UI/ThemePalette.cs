using System.Windows;
using System.Windows.Media;

namespace WinRebuilder.UI;

internal static class ThemePalette
{
    private static readonly string[] Keys = ["CanvasBrush", "SidebarBrush", "CardBrush", "TextBrush", "MutedBrush", "BorderBrushSoft",
        "AccentBrush", "AccentHoverBrush", "HoverBrush", "SelectionBrush", "ErrorBrush"];
    private static readonly string[] Dark = ["#171A20", "#20242D", "#292E38", "#F5F6FA", "#AFB6C4", "#3B424E", "#3F72C6", "#2D5EAE", "#343D4D", "#31496B", "#F28B82"];
    private static readonly string[] Light = ["#F3F4F7", "#ECEEF3", "#FFFFFF", "#20232B", "#687080", "#DFE2E9", "#3976D5", "#2B65C3", "#E5EAF3", "#DDE9FA", "#B42318"];
    public static bool IsDark { get; private set; }
    internal static string ColorFor(string key, bool dark) => (dark ? Dark : Light)[Array.IndexOf(Keys, key)];

    public static void Toggle()
    {
        if (Application.Current is null) return;
        IsDark = !IsDark;
        for (var i = 0; i < Keys.Length; i++)
            Application.Current.Resources[Keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorFor(Keys[i], IsDark)));
        foreach (Window window in Application.Current.Windows) WindowAppearance.Apply(window);
    }
}
