using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace WinRebuilder.Core;

public enum PackageProvider { Winget, Github, Url }
public enum Phase { Normal, Shell }
public enum RegistryKind { DWord, String }
public enum InstallerKind { Msi, Exe }
public enum SilentMode { None, S, Silent, VerySilent }

public sealed record PackageSpec(string Name, PackageProvider Provider, string? Id, string? Repository,
    string? Asset, Uri? Url, string? Sha256, InstallerKind? Installer, SilentMode? SilentMode,
    string? UninstallDisplayName, Phase Phase);
public sealed record RegistrySpec(string Path, string Name, RegistryKind Kind, string Value);
public sealed record ExplorerPatcherSpec(bool Enabled, string? SettingsFile, string? PackageId = null);
public sealed record Profile(int Version, IReadOnlyList<PackageSpec> Packages, IReadOnlyList<RegistrySpec> Registry,
    ExplorerPatcherSpec? ExplorerPatcher = null);
public sealed record LoadedProfile(Profile Profile, string Sha256, IReadOnlyList<ExplorerSetting>? ExplorerSettings = null);

public static class ProfileLoader
{
    public const string ExplorerPatcherWingetId = "valinet.ExplorerPatcher";
    private static readonly Regex Hex64 = new("\\A[0-9a-fA-F]{64}\\z", RegexOptions.Compiled);
    private static readonly Regex Repo = new("\\A[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.Compiled);
    private static readonly Regex Asset = new("\\A[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.Compiled);

    public static LoadedProfile Load(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > 64 * 1024) throw new FormatException("Profile exceeds 64 KiB.");
        // Anchors, aliases, custom tags and merge keys add hidden configuration. They are unsupported.
        if (Regex.IsMatch(yaml, @"(^|\s)([&*!][A-Za-z0-9_]+|<<\s*:)", RegexOptions.Multiline))
            throw new FormatException("YAML anchors, aliases, tags and merge keys are forbidden.");
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (Exception e) { throw new FormatException("Invalid YAML: " + e.Message, e); }
        if (stream.Documents.Count != 1) throw new FormatException("Exactly one YAML document is required.");
        CheckNodes(stream.Documents[0].RootNode, 0);
        var root = Map(stream.Documents[0].RootNode, "profile", "version", "packages", "registry", "explorerPatcher");
        var version = Scalar(Required(root, "version"), "version");
        if (version != "1") throw new FormatException("Only profile version 1 is supported.");
        var packages = root.TryGetValue("packages", out var p) ? Sequence(p, "packages").Select(ReadPackage).ToList() : [];
        var registry = root.TryGetValue("registry", out var r) ? Sequence(r, "registry").Select(ReadRegistry).ToArray() : [];
        var explorer = root.TryGetValue("explorerPatcher", out var ep) ? ReadExplorerPatcher(ep) : null;
        if (explorer?.Enabled == true)
            packages.Add(new PackageSpec("ExplorerPatcher", PackageProvider.Winget, explorer.PackageId,
                null, null, null, null, null, null, null, Phase.Shell));
        if (packages.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1) ||
            packages.Where(x => x.Provider == PackageProvider.Winget)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new FormatException("Duplicate package name or winget package ID.");
        var canonical = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        return new LoadedProfile(new Profile(1, packages, registry, explorer), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static ExplorerPatcherSpec ReadExplorerPatcher(YamlNode node)
    {
        var map = Map(node, "explorerPatcher", "enabled", "provider", "packageId", "settingsFile");
        var enabled = Get(map, "enabled") switch
        {
            "true" => true, "false" => false,
            _ => throw new FormatException("explorerPatcher.enabled must be true or false.")
        };
        var file = Optional(map, "settingsFile");
        var provider = Optional(map, "provider");
        var packageId = Optional(map, "packageId");
        if (enabled && (provider != "winget" || packageId != ExplorerPatcherWingetId))
            throw new FormatException("ExplorerPatcher requires the verified winget package ID valinet.ExplorerPatcher.");
        if (!enabled && (file is not null || provider is not null || packageId is not null))
            throw new FormatException("Disabled ExplorerPatcher cannot specify package or settings fields.");
        if (file is not null) ExplorerSettingsPath.Validate(file);
        return new ExplorerPatcherSpec(enabled, file, packageId);
    }

    private static PackageSpec ReadPackage(YamlNode node)
    {
        var m = Map(node, "package", "name", "provider", "id", "repository", "asset", "url", "sha256", "installer", "silentMode", "uninstallDisplayName", "phase");
        var name = Get(m, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || HasControls(name)) throw new FormatException("Invalid package name.");
        var provider = Get(m, "provider") switch { "winget" => PackageProvider.Winget, "github" => PackageProvider.Github, "url" => PackageProvider.Url, _ => throw new FormatException("Unknown package provider.") };
        var phase = Optional(m, "phase") switch { null or "normal" => Phase.Normal, "shell" => Phase.Shell, _ => throw new FormatException("Invalid phase.") };
        var id = Optional(m, "id"); var repository = Optional(m, "repository"); var asset = Optional(m, "asset");
        var rawUrl = Optional(m, "url"); var hash = Optional(m, "sha256");
        var rawInstaller = Optional(m, "installer"); var rawSilent = Optional(m, "silentMode");
        var detection = Optional(m, "uninstallDisplayName");
        var installer = rawInstaller switch { null => (InstallerKind?)null, "msi" => InstallerKind.Msi, "exe" => InstallerKind.Exe, _ => throw new FormatException("Invalid installer kind.") };
        var silent = rawSilent switch { null => (SilentMode?)null, "none" => SilentMode.None, "s" => SilentMode.S, "silent" => SilentMode.Silent, "verysilent" => SilentMode.VerySilent, _ => throw new FormatException("Invalid silent mode.") };
        if (hash is not null && !Hex64.IsMatch(hash)) throw new FormatException("sha256 must be 64 hexadecimal characters.");
        Uri? uri = null;
        if (rawUrl is not null)
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.Fragment.Length != 0)
                throw new FormatException("Download URL must be absolute HTTPS without credentials or fragment.");
        }
        if (provider == PackageProvider.Winget)
        {
            if (!WingetPackageId.IsValid(id) ||
                repository is not null || asset is not null || uri is not null || hash is not null || installer is not null || silent is not null || detection is not null)
                throw new FormatException("winget requires only an exact id.");
        }
        else
        {
            if (id is not null || installer is null || string.IsNullOrWhiteSpace(detection) || detection.Length > 100 || HasControls(detection) ||
                (installer == InstallerKind.Msi && silent is not null) || (installer == InstallerKind.Exe && silent is null))
                throw new FormatException("Downloads require installer and uninstallDisplayName; exe requires silentMode.");
            if (provider == PackageProvider.Github && (repository is null || !Repo.IsMatch(repository) || asset is null || !Asset.IsMatch(asset) || uri is not null))
                throw new FormatException("github requires repository and exact asset.");
            if (provider == PackageProvider.Url && (uri is null || repository is not null || asset is not null || hash is null))
                throw new FormatException("url requires HTTPS url and sha256.");
        }
        if (name.Equals("ExplorerPatcher", StringComparison.OrdinalIgnoreCase) ||
            repository?.Equals("valinet/ExplorerPatcher", StringComparison.OrdinalIgnoreCase) == true ||
            id?.Equals(ExplorerPatcherWingetId, StringComparison.OrdinalIgnoreCase) == true)
            throw new FormatException("Configure ExplorerPatcher only through explorerPatcher with its verified winget ID.");
        if (phase == Phase.Shell && provider == PackageProvider.Winget) throw new FormatException("Shell packages must use an explicit verified download source.");
        return new PackageSpec(name, provider, id, repository, asset, uri, hash, installer, silent, detection, phase);
    }

    private static RegistrySpec ReadRegistry(YamlNode node)
    {
        var m = Map(node, "registry entry", "path", "name", "type", "value");
        var path = Get(m, "path"); var name = Get(m, "name"); var type = Get(m, "type"); var value = Get(m, "value");
        RegistryPathPolicy.Parse(path);
        RegistryPathPolicy.ValidateName(name);
        var kind = type switch { "DWORD" => RegistryKind.DWord, "STRING" => RegistryKind.String, _ => throw new FormatException("Only DWORD and STRING registry types are supported.") };
        if (kind == RegistryKind.DWord && (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new FormatException("DWORD value must be an unsigned decimal 32-bit number.");
        if (kind == RegistryKind.String && (value.Length > 32767 || value.Contains('\0'))) throw new FormatException("Invalid registry string.");
        if (kind == RegistryKind.String) RegistryValue.FromString(value).Validate();
        return new RegistrySpec(path, name, kind, value);
    }

    private static Dictionary<string, YamlNode> Map(YamlNode node, string context, params string[] allowed)
    {
        if (node is not YamlMappingNode map) throw new FormatException($"{context} must be a mapping.");
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var pair in map.Children)
        {
            var key = Scalar(pair.Key, context + " key");
            if (!allowed.Contains(key, StringComparer.Ordinal)) throw new FormatException($"Unknown {context} field: {key}");
            if (!result.TryAdd(key, pair.Value)) throw new FormatException($"Duplicate {context} field: {key}");
        }
        return result;
    }
    private static IEnumerable<YamlNode> Sequence(YamlNode node, string context) => node is YamlSequenceNode seq ? seq.Children : throw new FormatException($"{context} must be a list.");
    private static string Scalar(YamlNode node, string context) => node is YamlScalarNode scalar && scalar.Value is not null ? scalar.Value : throw new FormatException($"{context} must be a scalar.");
    private static YamlNode Required(Dictionary<string, YamlNode> map, string key) => map.TryGetValue(key, out var value) ? value : throw new FormatException($"Missing {key}.");
    private static string Get(Dictionary<string, YamlNode> map, string key) => Scalar(Required(map, key), key);
    private static string? Optional(Dictionary<string, YamlNode> map, string key) => map.TryGetValue(key, out var value) ? Scalar(value, key) : null;
    private static bool HasControls(string value) => value.Any(char.IsControl);

    private static void CheckNodes(YamlNode node, int depth)
    {
        if (depth > 12) throw new FormatException("YAML nesting exceeds limit.");
        if (!node.Anchor.IsEmpty || !node.Tag.IsEmpty) throw new FormatException("YAML anchors and tags are forbidden.");
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var pair in map.Children) { CheckNodes(pair.Key, depth + 1); CheckNodes(pair.Value, depth + 1); }
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children) CheckNodes(child, depth + 1);
                break;
            case not YamlScalarNode:
                throw new FormatException("Unsupported YAML node.");
        }
    }
}
