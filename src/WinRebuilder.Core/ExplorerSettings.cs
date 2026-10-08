using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinRebuilder.Core;

public sealed record ExplorerSetting(string Path, string Name, RegistryValue Target);

public static class ExplorerSettingsPath
{
    private static readonly Regex Segment = new(@"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*\z", RegexOptions.Compiled);
    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':') ||
            path.StartsWith('/') || !path.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) ||
            path.Split('/').Any(x => x is "." or ".." || x.EndsWith('.') || !Segment.IsMatch(x)))
            throw new FormatException("ExplorerPatcher settingsFile must be a safe relative .reg path.");
    }
    public static string Resolve(string configPath, string relative)
    {
        Validate(relative);
        var root = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) throw new FileNotFoundException("ExplorerPatcher settings file not found.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new FormatException("ExplorerPatcher settingsFile cannot traverse a symbolic link.");
        }
        if (!File.Exists(current)) throw new FileNotFoundException("ExplorerPatcher settings file not found.");
        return current;
    }
}

public static class ConfigLoader
{
    public static async Task<LoadedProfile> LoadAsync(string configPath, CancellationToken ct = default)
    {
        if (!File.Exists(configPath)) throw new FileNotFoundException("Configuration file not found.");
        var profile = ProfileLoader.Load(await File.ReadAllTextAsync(configPath, ct));
        if (profile.Profile.ExplorerPatcher is not { Enabled: true, SettingsFile: { } file }) return profile;
        var resolved = ExplorerSettingsPath.Resolve(configPath, file);
        var bytes = await File.ReadAllBytesAsync(resolved, ct);
        if (bytes.Length is 0 or > 64 * 1024) throw new FormatException("ExplorerPatcher settings file size is invalid.");
        string content;
        try
        {
            content = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                ? new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2)
                : new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException e) { throw new FormatException("ExplorerPatcher settings encoding is invalid.", e); }
        var settings = ExplorerRegParser.Parse(content);
        var identity = Encoding.UTF8.GetBytes(profile.Sha256 + ":" + Convert.ToHexString(SHA256.HashData(bytes)));
        return profile with { Sha256 = Convert.ToHexString(SHA256.HashData(identity)), ExplorerSettings = settings };
    }
}

public static class ExplorerRegParser
{
    private static readonly Regex Assignment = new("\\A\"(?<name>(?:[^\"\\\\]|\\\\[\"\\\\])*)\"=(?<data>.*)\\z", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("\\A\"(?<value>(?:[^\"\\\\]|\\\\[\"\\\\])*)\"\\z", RegexOptions.Compiled);
    private static readonly Regex DWord = new(@"\Adword:(?<value>[0-9a-fA-F]{8})\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static IReadOnlyList<ExplorerSetting> Parse(string content)
    {
        if (content.Length is 0 or > 64 * 1024) throw new FormatException("ExplorerPatcher settings file size is invalid.");
        var lines = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines[0] != "Windows Registry Editor Version 5.00") throw new FormatException("Invalid .reg header.");
        var result = new List<ExplorerSetting>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? path = null;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith(';'))
            {
                if (line.Contains("Virtualized_", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("ExplorerPatcher virtualized settings need its own importer.");
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1];
                if (!section.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Only allowlisted HKEY_CURRENT_USER settings are supported.");
                path = "HKCU\\" + section[18..];
                ExplorerRegistryPolicy.Parse(path);
                continue;
            }
            if (path is null) throw new FormatException(".reg value appears before a key.");
            var match = Assignment.Match(line);
            if (!match.Success) throw new FormatException("Unsupported .reg statement.");
            var name = Unescape(match.Groups["name"].Value);
            ExplorerRegistryPolicy.ValidateValue(path, name);
            var data = match.Groups["data"].Value;
            RegistryValue target;
            if (data == "-") target = RegistryValue.Missing;
            else if (DWord.Match(data) is { Success: true } number)
                target = RegistryValue.FromDWord(uint.Parse(number.Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            else if (Quoted.Match(data) is { Success: true } quoted)
                target = RegistryValue.FromString(Unescape(quoted.Groups["value"].Value));
            else throw new FormatException("Unsupported .reg value type or syntax.");
            target.Validate();
            if (!identities.Add(path + "\\" + name)) throw new FormatException("Duplicate ExplorerPatcher setting.");
            result.Add(new ExplorerSetting(path, name, target));
        }
        if (result.Count == 0) throw new FormatException("ExplorerPatcher settings file has no supported values.");
        return result;
    }
    private static string Unescape(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { result.Append(text[i]); continue; }
            if (++i == text.Length || text[i] is not ('\\' or '"')) throw new FormatException("Unsupported .reg escape sequence.");
            result.Append(text[i]);
        }
        return result.ToString();
    }
}
