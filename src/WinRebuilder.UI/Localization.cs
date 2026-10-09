using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;

namespace WinRebuilder.UI;

public sealed class Localization : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("WinRebuilder.UI.Strings", typeof(Localization).Assembly);
    public static Localization Instance { get; } = new();
    private readonly string settingsPath;
    private CultureInfo culture;

    public Localization(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinRebuilder", "language.txt");
        culture = new CultureInfo(ReadLanguage(this.settingsPath));
    }

    public string Language => culture.TwoLetterISOLanguageName;
    public string this[string key] => Resources.GetString(key, culture) ?? Resources.GetString(key, CultureInfo.InvariantCulture) ?? key;
    public string Format(string key, params object[] args) => string.Format(culture, this[key], args);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Select(string language)
    {
        if (language is not ("ja" or "en")) throw new ArgumentOutOfRangeException(nameof(language));
        if (Language == language) return;
        var folder = Path.GetDirectoryName(settingsPath)!;
        if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(this["LanguageFolderLinkError"]);
        Directory.CreateDirectory(folder);
        if (File.Exists(settingsPath) && (File.GetAttributes(settingsPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(this["LanguageFileLinkError"]);
        var temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, language);
            File.Move(temp, settingsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        culture = new CultureInfo(language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    private static string ReadLanguage(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 16 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return "ja";
            var value = File.ReadAllText(path).Trim();
            return value is "ja" or "en" ? value : "ja";
        }
        catch (IOException) { return "ja"; }
        catch (UnauthorizedAccessException) { return "ja"; }
    }
}
