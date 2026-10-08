using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinRebuilder.Core;

public enum RegistryHiveKind { LocalMachine, CurrentUser }
public enum RegistryViewKind { Registry64 }
public enum RegistryScope { Generic, ExplorerPatcher }
public sealed record RegistryLocation(RegistryHiveKind Hive, string KeyPath)
{
    public string FullPath => (Hive == RegistryHiveKind.LocalMachine ? "HKLM\\" : "HKCU\\") + KeyPath;
}

public static class RegistryPathPolicy
{
    private static readonly Regex PathPattern = new(@"\A(HKLM|HKCU)\\(?:[A-Za-z0-9 _.-]+\\)*[A-Za-z0-9 _.-]+\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RegistryLocation Parse(string path)
    {
        if (path is null || !PathPattern.IsMatch(path) ||
            !(path.StartsWith("HKLM\\SOFTWARE\\Policies\\", StringComparison.OrdinalIgnoreCase) ||
              path.StartsWith("HKCU\\Software\\Policies\\", StringComparison.OrdinalIgnoreCase)) ||
            path.Split('\\').Any(segment => segment is "." or ".."))
            throw new FormatException("Registry path is outside the permitted Policies tree.");
        var separator = path.IndexOf('\\');
        return new RegistryLocation(path[..separator].Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? RegistryHiveKind.LocalMachine : RegistryHiveKind.CurrentUser, path[(separator + 1)..]);
    }

    public static void ValidateName(string name)
    {
        if (name is null || name.Length is < 1 or > 16383 || name.Contains('\\') || name.Any(char.IsControl))
            throw new FormatException("Invalid registry value name.");
    }
}

public static class ExplorerRegistryPolicy
{
    // Exact keys taken from ExplorerPatcher's official settings.reg definitions.
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        @"HKCU\Software\ExplorerPatcher",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartPage",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ExplorerPatcher",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Search",
        @"HKCU\Control Panel\Desktop"
    };
    // Names are from the same official settings.reg. A key alone is too broad: Desktop also holds executable settings.
    private static readonly Dictionary<string, string[]> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"HKCU\Software\ExplorerPatcher"] = [
            "OldTaskbar", "OrbStyle", "OldTaskbarAl", "MMOldTaskbarAl", "SkinMenus", "CenterMenus", "FlyoutMenus",
            "HideControlCenterButton", "SkinIcons", "TrayOverflowStyle", "ReplaceNetwork", "IMEStyle",
            "LegacyFileTransferDialog", "UseClassicDriveGrouping", "DisableImmersiveContextMenu",
            "ShrinkExplorerAddressBar", "HideExplorerSearchBar", "HideIconAndTitleInExplorer", "MicaEffectOnTitlebar",
            "WeatherLocation", "WeatherViewMode", "WeatherFixedSize", "WeatherToLeft", "WeatherContentUpdateMode",
            "WeatherTemperatureUnit", "WeatherLanguage", "WeatherTheme", "WeatherWindowCornerPreference",
            "WeatherIconPack", "WeatherContentsMode", "WeatherZoomFactor", "SpotlightDisableIcon",
            "SpotlightDesktopMenuMask", "SpotlightUpdateSchedule", "LastSectionInProperties", "ClockFlyoutOnWinC",
            "ToolbarSeparators", "PropertiesInWinX", "NoMenuAccelerator", "DisableOfficeHotkeys",
            "DisableWinFHotkey", "DisableAeroSnapQuadrants", "SnapAssistSettings", "LogonLogoffShutdownSounds",
            "DoNotRedirectSystemToSettingsApp", "DoNotRedirectProgramsAndFeaturesToSettingsApp",
            "DoNotRedirectDateAndTimeToSettingsApp", "DoNotRedirectNotificationIconsToSettingsApp",
            "UpdatePolicy", "UpdatePreferStaging", "UpdateAllowDowngrades", "AllocConsole", "Memcheck",
            "TaskbarAutohideOnDoubleClick", "ClassicThemeMitigations", "NoPropertiesInContextMenu",
            "EnableSymbolDownload", "PinnedItemsActAsQuickLaunch", "RemoveExtraGapAroundPinnedItems", "Language"
        ],
        [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"] = [
            "TaskbarDa", "ShowTaskViewButton", "TaskbarGlomLevel", "MMTaskbarGlomLevel", "TaskbarSmallIcons",
            "ShowSecondsInSystemClock", "TaskbarSD", "Start_ShowClassicMode", "TaskbarAl", "Start_PowerButtonAction"
        ],
        [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartPage"] = ["MonitorOverride", "MakeAllAppsDefault"],
        [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ExplorerPatcher"] = ["XamlSounds"],
        [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search"] = ["SearchboxTaskbarMode"],
        [@"HKCU\Control Panel\Desktop"] = ["PaintDesktopVersion"]
    };
    public static RegistryLocation Parse(string path)
    {
        if (path is null || !Keys.Contains(path)) throw new FormatException("ExplorerPatcher registry key is not allowlisted.");
        return new RegistryLocation(RegistryHiveKind.CurrentUser, path[5..]);
    }
    public static void ValidateValue(string path, string name)
    {
        Parse(path);
        RegistryPathPolicy.ValidateName(name);
        if (!Values[path].Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new FormatException("ExplorerPatcher value name is not allowlisted.");
    }
}

public sealed record RegistryValue(bool Exists, RegistryKind? Kind, uint? DWord, string? Text)
{
    public static RegistryValue Missing => new(false, null, null, null);
    public static RegistryValue FromDWord(uint value) => new(true, RegistryKind.DWord, value, null);
    public static RegistryValue FromString(string value) => new(true, RegistryKind.String, null, value);
    public static RegistryValue ForTarget(RegistrySpec spec) => spec.Kind switch
    {
        RegistryKind.DWord => FromDWord(uint.Parse(spec.Value, NumberStyles.None, CultureInfo.InvariantCulture)),
        RegistryKind.String => FromString(spec.Value),
        _ => throw new FormatException("Unsupported registry value kind.")
    };

    public void Validate()
    {
        if (!Exists && Kind is null && DWord is null && Text is null) return;
        if (Exists && Kind == RegistryKind.DWord && DWord is not null && Text is null) return;
        if (Exists && Kind == RegistryKind.String && DWord is null && Text is not null && Text.Length <= 32767 &&
            !Text.Contains('\0') && HasValidUtf16(Text)) return;
        throw new FormatException("Invalid registry value representation.");
    }

    private static bool HasValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

public static class RegistryBackupId
{
    private static readonly Regex Pattern = new(@"\A[0-9a-f]{32}\z", RegexOptions.Compiled);
    public static string Create() => Guid.NewGuid().ToString("N");
    public static void Validate(string id)
    {
        if (id is null || !Pattern.IsMatch(id)) throw new FormatException("Invalid backup ID.");
    }
}

public sealed record RegistryBackup(int SchemaVersion, string BackupId, string ProfileHash, string ApplicationVersion,
    string OperationId, DateTimeOffset Timestamp, RegistryHiveKind Hive, string KeyPath, RegistryViewKind View,
    string Name, RegistryValue Previous, RegistryValue Target, RegistryScope Scope = RegistryScope.Generic)
{
    private static readonly Regex Hash = new(@"\A[0-9A-Fa-f]{64}\z", RegexOptions.Compiled);
    private static readonly Regex Operation = new(@"\A[0-9a-f]{24}\z", RegexOptions.Compiled);
    public string FullPath => new RegistryLocation(Hive, KeyPath).FullPath;

    public void Validate()
    {
        if (SchemaVersion != 1) throw new FormatException("Unsupported registry backup schema version.");
        RegistryBackupId.Validate(BackupId);
        if (ProfileHash is null || !Hash.IsMatch(ProfileHash) || OperationId is null || !Operation.IsMatch(OperationId) ||
            string.IsNullOrWhiteSpace(ApplicationVersion) || ApplicationVersion.Length > 100 || ApplicationVersion.Any(char.IsControl) ||
            Timestamp == default || !Enum.IsDefined(Hive) || View != RegistryViewKind.Registry64)
            throw new FormatException("Invalid registry backup metadata.");
        if (KeyPath is null) throw new FormatException("Missing registry key path.");
        var parsed = Scope switch
        {
            RegistryScope.Generic => RegistryPathPolicy.Parse(FullPath),
            RegistryScope.ExplorerPatcher => ExplorerRegistryPolicy.Parse(FullPath),
            _ => throw new FormatException("Unsupported registry backup scope.")
        };
        if (parsed.Hive != Hive) throw new FormatException("Invalid registry hive.");
        if (Scope == RegistryScope.ExplorerPatcher) ExplorerRegistryPolicy.ValidateValue(FullPath, Name);
        else RegistryPathPolicy.ValidateName(Name);
        Previous?.Validate(); Target?.Validate();
        if (Previous is null || Target is null || (Scope == RegistryScope.Generic && !Target.Exists) || Previous == Target)
            throw new FormatException("Invalid registry backup values.");
    }
}

public static class RegistryBackupCodec
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static byte[] Serialize(RegistryBackup backup)
    {
        backup.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            backup.SchemaVersion, backup.BackupId, backup.ProfileHash, backup.ApplicationVersion, backup.OperationId,
            backup.Timestamp, Hive = backup.Hive.ToString(), backup.KeyPath, View = backup.View.ToString(), backup.Name,
            Scope = backup.Scope.ToString(),
            Previous = EncodeValue(backup.Previous), Target = EncodeValue(backup.Target)
        }, Options);
    }

    public static RegistryBackup Deserialize(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > 1024 * 1024) throw new FormatException("Invalid registry backup size.");
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            CheckFields(root, ["SchemaVersion", "BackupId", "ProfileHash", "ApplicationVersion", "OperationId", "Timestamp",
                "Hive", "KeyPath", "View", "Name", "Previous", "Target"], "Scope");
            var backup = new RegistryBackup(
                root.GetProperty("SchemaVersion").GetInt32(), GetString(root, "BackupId"), GetString(root, "ProfileHash"),
                GetString(root, "ApplicationVersion"), GetString(root, "OperationId"), root.GetProperty("Timestamp").GetDateTimeOffset(),
                ParseEnum<RegistryHiveKind>(GetString(root, "Hive")), GetString(root, "KeyPath"),
                ParseEnum<RegistryViewKind>(GetString(root, "View")), GetString(root, "Name"),
                DecodeValue(root.GetProperty("Previous")), DecodeValue(root.GetProperty("Target")),
                root.TryGetProperty("Scope", out var scope) ? ParseEnum<RegistryScope>(scope.GetString()!) : RegistryScope.Generic);
            backup.Validate();
            return backup;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException)
        { throw new FormatException("Malformed registry backup.", e); }
    }

    private static object EncodeValue(RegistryValue value) => new { value.Exists, Kind = value.Kind?.ToString(), value.DWord, value.Text };
    private static RegistryValue DecodeValue(JsonElement value)
    {
        CheckFields(value, "Exists", "Kind", "DWord", "Text");
        var kind = value.GetProperty("Kind");
        var number = value.GetProperty("DWord");
        var text = value.GetProperty("Text");
        return new RegistryValue(value.GetProperty("Exists").GetBoolean(),
            kind.ValueKind == JsonValueKind.Null ? null : ParseEnum<RegistryKind>(kind.GetString()!),
            number.ValueKind == JsonValueKind.Null ? null : number.GetUInt32(),
            text.ValueKind == JsonValueKind.Null ? null : text.GetString());
    }
    private static string GetString(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new FormatException($"Missing {name}.");
    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, false, out var result) && Enum.IsDefined(result) && string.Equals(result.ToString(), value, StringComparison.Ordinal)
            ? result : throw new FormatException("Invalid registry backup enum value.");
    private static void CheckFields(JsonElement value, params string[] names) => CheckFields(value, names, null);
    private static void CheckFields(JsonElement value, string[] names, string? optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Registry backup object expected.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!(names.Contains(property.Name, StringComparer.Ordinal) || property.Name == optional) || !seen.Add(property.Name))
                throw new FormatException("Unknown or duplicate registry backup field.");
        if (names.Any(name => !seen.Contains(name))) throw new FormatException("Missing registry backup field.");
    }
}

public sealed record RollbackResult(Outcome Outcome, string BackupId, string Path, string Name, string Message);

public sealed class RegistryRollback(IRegistryAccess registry, IRegistryBackupStore backups, IOperationLogger logger,
    IExplorerRegistryAccess? explorerRegistry = null)
{
    public async Task<RollbackResult> RunAsync(string backupId, bool dryRun, CancellationToken ct = default)
    {
        RegistryBackupId.Validate(backupId);
        var backup = await backups.LoadAsync(backupId, ct);
        backup.Validate();
        var path = backup.FullPath;
        var access = backup.Scope == RegistryScope.ExplorerPatcher
            ? explorerRegistry ?? throw new InvalidOperationException("ExplorerPatcher registry adapter unavailable.")
            : registry;
        Outcome outcome;
        string message;
        try
        {
            var current = await access.ReadAsync(path, backup.Name, ct);
            if (current == backup.Previous)
            {
                outcome = Outcome.Skip;
                message = "Already restored.";
            }
            else if (current != backup.Target)
            {
                outcome = Outcome.Fail;
                message = "Registry value differs from the recorded applied value; rollback refused.";
            }
            else if (dryRun)
            {
                outcome = Outcome.Restore;
                message = "Would restore registry value.";
            }
            else
            {
                if (backup.Previous.Exists)
                    await access.WriteAsync(path, backup.Name, backup.Previous, ct);
                else
                    await access.DeleteValueAsync(path, backup.Name, ct);
                var verified = await access.ReadAsync(path, backup.Name, ct);
                if (verified != backup.Previous) throw new InvalidOperationException("Rollback verification failed.");
                outcome = Outcome.Restore;
                message = "Registry value restored and verified.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            outcome = Outcome.Fail;
            message = e is InvalidOperationException ? e.Message : $"{e.GetType().Name} during rollback.";
        }
        logger.Log(new LogEntry(DateTimeOffset.UtcNow, backup.OperationId, OperationType.Registry, outcome,
            $"{path}\\{backup.Name}: {message} Backup: {backup.BackupId}."));
        return new RollbackResult(outcome, backup.BackupId, path, backup.Name, message);
    }
}
