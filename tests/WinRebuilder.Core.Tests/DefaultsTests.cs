using WinRebuilder.Core;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace WinRebuilder.Core.Tests;

public sealed class DefaultsTests
{
    private static string Root => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Theory]
    [InlineData("config.yml")]
    [InlineData("config.example.yml")]
    public async Task DefaultConfigurationHasExactlyTheVerifiedSoftware(string file)
    {
        var profile = await ConfigLoader.LoadAsync(Path.Combine(Root, file));
        Assert.Equal(["7zip.7zip", "CrystalDewWorld.CrystalDiskInfo", "valinet.ExplorerPatcher"],
            profile.Profile.Packages.Select(x => x.Id));
        Assert.Equal(1, Planner.Create(profile).Operations.Count(x => x.Package?.Name == "ExplorerPatcher"));
        Assert.Null(profile.Profile.ExplorerPatcher?.SettingsFile);
    }

    [Fact]
    public void ReleaseSourcesRequireGuiAndExactPackageManifest()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Build.props"));
        var group = props.Root!.Element("PropertyGroup")!;
        Assert.Equal("0.4.0", group.Element("Version")?.Value);
        Assert.Equal("0.4.0.0", group.Element("AssemblyVersion")?.Value);
        Assert.Equal("0.4.0.0", group.Element("FileVersion")?.Value);
        Assert.Equal("v.1.0.a-pre1", group.Element("InformationalVersion")?.Value);
        Assert.Equal("v.1.0.a-pre1", typeof(Planner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        var ui = File.ReadAllText(Path.Combine(Root, "src/WinRebuilder.UI/WinRebuilder.UI.csproj"));
        Assert.Contains("<OutputType>WinExe</OutputType>", ui);
        Assert.Contains("<AssemblyName>WinRebuilder</AssemblyName>", ui);
        Assert.Contains("<ApplicationIcon>Assets/WinRebuilder.ico</ApplicationIcon>", ui);
        Assert.True(File.Exists(Path.Combine(Root, "src/WinRebuilder.UI/Assets/WinRebuilder.ico")));
        Assert.Contains("インストール", File.ReadAllText(Path.Combine(Root, "README.md")));
        var solution = File.ReadAllText(Path.Combine(Root, "WinRebuilder.slnx"));
        Assert.DoesNotContain("WinRebuilder.Cli", solution);
        Assert.False(File.Exists(Path.Combine(Root, "src/WinRebuilder.Cli/WinRebuilder.Cli.csproj")));
        var release = File.ReadAllText(Path.Combine(Root, ".github/workflows/release.yml"));
        Assert.Contains("release-assets/WinRebuilder.zip release-assets/WinRebuilder-portable.exe", release);
        Assert.DoesNotContain("WinRebuilder.Cli.exe", release);
        Assert.DoesNotContain("wrb.exe", release);
        var script = File.ReadAllText(Path.Combine(Root, "packaging/Build-Release.ps1"));
        Assert.DoesNotContain("WINDOWS_SIGNING_", release);
        Assert.DoesNotContain("WINDOWS_SIGNING_", script);
        Assert.DoesNotContain("SignTool", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Verify-Signature", script);
        var copyPosition = script.IndexOf("Copy-Item $final release-assets/WinRebuilder-portable.exe", StringComparison.Ordinal);
        Assert.True(copyPosition >= 0);
        Assert.Contains("Get-FileHash $final -Algorithm SHA256", script);
        foreach (var name in new[] { "README.md", "WinRebuilder.exe", "config.yml", "config.example.yml" })
            Assert.Contains("'" + name + "'", script);
        Assert.Contains("if (($actual -join '|') -ne ($expected -join '|'))", script);
        Assert.Contains("*.pdb", script);
    }
}
