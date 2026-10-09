using System;
using System.IO;
using System.Linq;
using System.Resources;
using System.Globalization;
using WinRebuilder.UI;
using Xunit;

namespace WinRebuilder.UI.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void JapaneseAndEnglishResourcesHaveEveryUiKey()
    {
        var manager = new ResourceManager("WinRebuilder.UI.Strings", typeof(Localization).Assembly);
        var english = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)!;
        var japanese = manager.GetResourceSet(new CultureInfo("ja"), true, false)!;
        var keys = english.Cast<System.Collections.DictionaryEntry>().Select(x => (string)x.Key).ToArray();
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(manager.GetString(key, CultureInfo.InvariantCulture)));
            Assert.False(string.IsNullOrWhiteSpace(japanese.GetString(key)));
        }
        Assert.Equal(keys.Length, japanese.Cast<System.Collections.DictionaryEntry>().Count());
    }

    [Fact]
    public void SelectionPersistsAndUnsupportedCultureFallsBackToJapanese()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wrb-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "language.txt");
            var language = new Localization(path);
            Assert.Equal("ja", language.Language);
            Assert.Equal("ソフトウェア", language["NavSoftware"]);
            language.Select("en");
            Assert.Equal("en", new Localization(path).Language);
            Assert.Equal("Software", language["NavSoftware"]);
            Assert.Equal("UnknownKey", language["UnknownKey"]);
            Assert.Throws<ArgumentOutOfRangeException>(() => { language.Select("fr"); });
            File.WriteAllText(path, "fr");
            Assert.Equal("ja", new Localization(path).Language);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void DarkPaletteUsesDarkSurfaces()
    {
        foreach (var key in new[] { "CanvasBrush", "SidebarBrush", "CardBrush", "HoverBrush", "SelectionBrush" })
            Assert.NotEqual("#FFFFFF", ThemePalette.ColorFor(key, true));
        Assert.Equal("#FFFFFF", ThemePalette.ColorFor("CardBrush", false));
    }
}
