using System.Globalization;
using System.Text.RegularExpressions;

namespace WinRebuilder.Core;

public enum WingetCapabilityStatus { Available, MissingExecutable, CannotExecute, UnsupportedOutput }

public sealed record WingetCapability(WingetCapabilityStatus Status, Version? Version, string Diagnostic)
{
    public bool IsAvailable => Status == WingetCapabilityStatus.Available;
}

public interface IWingetCapabilityProbe
{
    Task<WingetCapability> ProbeAsync(CancellationToken ct);
}

public static class WingetPackageId
{
    private const string Forbidden = "\\/:*?\"<>|";

    public static bool IsValid(string? id)
    {
        if (id is not { Length: > 0 and <= 128 } || !char.IsLetterOrDigit(id[0])) return false;
        var segments = id.Split('.');
        if (segments.Length > 8 || segments.Any(segment => segment.Length is < 1 or > 32)) return false;
        return id.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) &&
            CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.Surrogate) && !Forbidden.Contains(c));
    }
}

public static class WingetVersion
{
    private static readonly Regex Pattern = new(
        @"\Av?(?<number>[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:-[0-9A-Za-z.-]+)?\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string output, out Version? version)
    {
        version = null;
        var match = Pattern.Match(output.Trim());
        return match.Success && Version.TryParse(match.Groups["number"].Value, out version);
    }
}

public static class WingetExitCodes
{
    // Microsoft AppInstallerErrors.h: APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND.
    public const int NoApplicationsFound = unchecked((int)0x8A150014);

    public static bool IsSuccess(int code) => code == 0;
    public static bool IsNoApplicationsFound(int code) => code == NoApplicationsFound;
    public static string DescribeFailure(string operation, int code) =>
        $"winget {operation} exited with code {code} (0x{unchecked((uint)code):X8}).";
}
