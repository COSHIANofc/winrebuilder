using System.Globalization;
using System.Security.Cryptography;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace WinRebuilder.Core;

public sealed class ConfigurationWorkspace
{
    public string Path { get; }
    public LoadedProfile Current { get; private set; }

    private ConfigurationWorkspace(string path, LoadedProfile current)
    { Path = System.IO.Path.GetFullPath(path); Current = current; }

    public static async Task<ConfigurationWorkspace> OpenAsync(string path, CancellationToken ct = default)
    {
        var loaded = await ConfigLoader.LoadAsync(path, ct);
        Planner.Create(loaded);
        return new(path, loaded);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var next = await ConfigLoader.LoadAsync(Path, ct);
        Planner.Create(next);
        Current = next;
    }

    public Task AddPackageAsync(PackageSpec package, CancellationToken ct = default)
    {
        var packages = Current.Profile.Packages.Append(package).ToArray();
        return SaveAsync(Current.Profile with { Packages = packages }, ct);
    }

    public Task RemovePackageAsync(PackageSpec package, CancellationToken ct = default)
    {
        if (package.Name == "ExplorerPatcher" && package.Phase == Phase.Shell)
            throw new FormatException("Disable ExplorerPatcher through its integration controls.");
        var packages = Current.Profile.Packages.Where(x => x != package).ToArray();
        if (packages.Length == Current.Profile.Packages.Count) throw new FormatException("Package is not in the configuration.");
        return SaveAsync(Current.Profile with { Packages = packages }, ct);
    }

    public Task SetExplorerPatcherAsync(bool enabled, string? settingsFile, CancellationToken ct = default) =>
        SaveAsync(Current.Profile with { ExplorerPatcher = new ExplorerPatcherSpec(enabled, settingsFile,
            enabled ? ProfileLoader.ExplorerPatcherWingetId : null) }, ct);

    public async Task<string> CopyExplorerSettingsAsync(string source, CancellationToken ct = default)
    {
        if (!source.EndsWith(".reg", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Select a .reg file.");
        var bytes = await File.ReadAllBytesAsync(source, ct);
        ExplorerRegParser.ParseBytes(bytes);
        var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "settings");
        if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Settings directory cannot be a symbolic link.");
        Directory.CreateDirectory(folder);
        var name = "explorerpatcher-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".reg";
        var destination = System.IO.Path.Combine(folder, name);
        if (File.Exists(destination))
        {
            var existing = await File.ReadAllBytesAsync(destination, ct);
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0 ||
                !bytes.SequenceEqual(existing))
                throw new IOException("An existing settings file conflicts with the selected file.");
        }
        else await AtomicWriteAsync(destination, bytes, ct);
        var relative = "settings/" + name;
        await SetExplorerPatcherAsync(true, relative, ct);
        return relative;
    }

    public async Task SaveAsync(Profile profile, CancellationToken ct = default)
    {
        var yaml = Serialize(profile);
        var parsed = ProfileLoader.Load(yaml);
        if (parsed.Profile.ExplorerPatcher is not { Enabled: true, SettingsFile: not null }) Planner.Create(parsed);
        var path = Path;
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Configuration file cannot be a symbolic link.");
        var backup = path + ".bak";
        if (File.Exists(backup) && (File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Configuration backup cannot be a symbolic link.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await AtomicWriteAsync(temp, System.Text.Encoding.UTF8.GetBytes(yaml), ct);
            var validated = await ConfigLoader.LoadAsync(temp, ct);
            Planner.Create(validated);
            if (File.Exists(path))
            {
                var currentOnDisk = await ConfigLoader.LoadAsync(path, ct);
                if (currentOnDisk.Sha256 != Current.Sha256)
                    throw new IOException("Configuration changed on disk. Reload before saving.");
            }
            if (File.Exists(path)) File.Replace(temp, path, backup, true);
            else File.Move(temp, path);
            Current = validated;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task AtomicWriteAsync(string destination, byte[] bytes, CancellationToken ct)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string Serialize(Profile profile)
    {
        var root = new YamlMappingNode();
        root.Add("version", Scalar("1"));
        var packages = new YamlSequenceNode();
        foreach (var package in profile.Packages)
        {
            if (package.Name == "ExplorerPatcher" && package.Phase == Phase.Shell &&
                package.Provider == PackageProvider.Winget && package.Id == ProfileLoader.ExplorerPatcherWingetId)
                continue;
            var node = new YamlMappingNode();
            node.Add("name", Scalar(package.Name));
            node.Add("provider", Scalar(package.Provider.ToString().ToLowerInvariant()));
            Add(node, "id", package.Id); Add(node, "repository", package.Repository); Add(node, "asset", package.Asset);
            Add(node, "url", package.Url?.ToString()); Add(node, "sha256", package.Sha256);
            Add(node, "installer", package.Installer?.ToString().ToLowerInvariant());
            Add(node, "silentMode", package.SilentMode?.ToString().ToLowerInvariant());
            Add(node, "uninstallDisplayName", package.UninstallDisplayName);
            if (package.Phase == Phase.Shell) node.Add("phase", Scalar("shell"));
            packages.Add(node);
        }
        root.Add("packages", packages);
        var registry = new YamlSequenceNode();
        foreach (var item in profile.Registry)
        {
            var node = new YamlMappingNode();
            node.Add("path", Scalar(item.Path)); node.Add("name", Scalar(item.Name));
            node.Add("type", Scalar(item.Kind == RegistryKind.DWord ? "DWORD" : "STRING"));
            node.Add("value", Scalar(item.Value)); registry.Add(node);
        }
        root.Add("registry", registry);
        if (profile.ExplorerPatcher is { } explorer)
        {
            var node = new YamlMappingNode();
            node.Add("enabled", Scalar(explorer.Enabled ? "true" : "false"));
            if (explorer.Enabled)
            {
                node.Add("provider", Scalar("winget"));
                node.Add("packageId", Scalar(explorer.PackageId ?? ProfileLoader.ExplorerPatcherWingetId));
                Add(node, "settingsFile", explorer.SettingsFile);
            }
            root.Add("explorerPatcher", node);
        }
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return writer.ToString();
    }
    private static YamlScalarNode Scalar(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };
    private static void Add(YamlMappingNode node, string key, string? value)
    { if (value is not null) node.Add(key, Scalar(value)); }
}
