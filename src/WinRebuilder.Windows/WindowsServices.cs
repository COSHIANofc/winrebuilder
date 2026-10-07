using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinRebuilder.Core;

namespace WinRebuilder.Windows;

public static class WindowsPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinRebuilder");
    public static string Backups => Path.Combine(Root, "registry-backups");
    public static string Artifacts => Path.Combine(Root, "artifacts");
    public static string States => Path.Combine(Root, "state");
}
public static class Administrator
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
public sealed class WindowsProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new Win32Exception("Process could not be started.");
        try
        {
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput, ct);
            var stderr = ReadBoundedAsync(process.StandardError, ct);
            await process.WaitForExitAsync(ct);
            var outResult = await stdout;
            var errResult = await stderr;
            return new ProcessResult(process.ExitCode, outResult.Text, errResult.Text, outResult.Truncated || errResult.Truncated);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (PlatformNotSupportedException)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 64 * 1024;
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            var count = Math.Min(read, limit - text.Length);
            if (count > 0) text.Append(buffer, 0, count);
            if (count < read) truncated = true;
        }
        return (text.ToString(), truncated);
    }
}
public sealed class WindowsRegistry : IRegistryAccess
{
    public Task<RegistryValue> ReadAsync(string path, string name, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ct.ThrowIfCancellationRequested();
        var (hive, subkey) = Split(path);
        using var key = hive.OpenSubKey(subkey, false);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(new RegistryValue(false, null, null));
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return Task.FromResult(kind switch
        {
            RegistryValueKind.DWord => new RegistryValue(true, RegistryKind.DWord, unchecked((uint)(int)value!).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.String => new RegistryValue(true, RegistryKind.String, (string?)value),
            _ => throw new InvalidOperationException("Existing registry type is unsupported; refusing to overwrite it.")
        });
    }
    public Task WriteAsync(string path, string name, RegistryKind kind, string value, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ct.ThrowIfCancellationRequested();
        var (hive, subkey) = Split(path);
        using var key = hive.CreateSubKey(subkey, true) ?? throw new InvalidOperationException("Could not create registry key.");
        key.SetValue(name, kind == RegistryKind.DWord ? unchecked((int)uint.Parse(value, CultureInfo.InvariantCulture)) : value,
            kind == RegistryKind.DWord ? RegistryValueKind.DWord : RegistryValueKind.String);
        return Task.CompletedTask;
    }
    private static (RegistryKey Hive, string Subkey) Split(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var index = path.IndexOf('\\');
        return (path[..index].ToUpperInvariant() switch { "HKLM" => Registry.LocalMachine, "HKCU" => Registry.CurrentUser, _ => throw new ArgumentException("Unsupported hive.") }, path[(index + 1)..]);
    }
}
public sealed class JsonRegistryBackupStore : IRegistryBackupStore
{
    public Task SaveAsync(RegistryBackup backup, CancellationToken ct)
    {
        var file = Path.Combine(WindowsPaths.Backups, backup.OperationId + "-" + Guid.NewGuid().ToString("N") + ".json");
        return AtomicJson.WriteAsync(file, backup, ct);
    }
}
public sealed class JsonExecutionStateStore : IExecutionStateStore
{
    public async Task<ExecutionState?> LoadAsync(string profileHash, CancellationToken ct)
    {
        var file = Path.Combine(WindowsPaths.States, profileHash + ".json");
        if (!File.Exists(file)) return null;
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<ExecutionState>(stream, cancellationToken: ct);
    }
    public Task SaveAsync(ExecutionState state, CancellationToken ct) => AtomicJson.WriteAsync(Path.Combine(WindowsPaths.States, state.ProfileHash + ".json"), state, ct);
}
internal static class AtomicJson
{
    public static async Task WriteAsync<T>(string file, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed class ConsoleOperationLogger : IOperationLogger
{
    private readonly IReadOnlyDictionary<string, PlannedOperation> operations;
    public ConsoleOperationLogger(ExecutionPlan plan) => operations = plan.Operations.ToDictionary(x => x.Id);
    public void Log(LogEntry entry)
    {
        operations.TryGetValue(entry.OperationId, out var operation);
        var name = operation?.Package?.Name ?? operation?.Registry?.Name ?? entry.OperationId;
        Console.WriteLine($"{entry.Outcome.ToString().ToUpperInvariant(),-7} {name} [{entry.OperationId}]");
        Console.WriteLine($"        {entry.Message}");
    }
}
public sealed class JsonFileOperationLogger : IOperationLogger
{
    private readonly string path;
    public JsonFileOperationLogger(string? path = null) => this.path = path ?? Path.Combine(WindowsPaths.Root, "operations.jsonl");
    public void Log(LogEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }
}
public sealed class UninstallRegistryDetector
{
    public bool IsInstalled(string displayName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var subName in uninstall.GetSubKeyNames())
            {
                using var sub = uninstall.OpenSubKey(subName);
                if (string.Equals(sub?.GetValue("DisplayName") as string, displayName, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
public sealed class VerifiedDownloader(HttpClient client) : IDownloads
{
    public async Task<string> DownloadVerifiedAsync(Uri url, string sha256, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("HTTPS required.");
        if (!Regex.IsMatch(sha256, "\\A[0-9a-fA-F]{64}\\z")) throw new ArgumentException("Invalid SHA-256.");
        Directory.CreateDirectory(WindowsPaths.Artifacts);
        var destination = Path.Combine(WindowsPaths.Artifacts, sha256.ToLowerInvariant());
        if (File.Exists(destination) && await Matches(destination, sha256, ct)) return destination;
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var response = await GetHttpsResponseAsync(url, ct))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(response.Content, output, ct);
            if (!await Matches(temp, sha256, ct)) throw new InvalidOperationException("SHA-256 mismatch.");
            File.Move(temp, destination, true);
            return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<string> DownloadTemporaryAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("HTTPS required.");
        Directory.CreateDirectory(WindowsPaths.Artifacts);
        var destination = Path.Combine(WindowsPaths.Artifacts, Guid.NewGuid().ToString("N") + ".download");
        var temp = destination + ".tmp";
        try
        {
            using (var response = await GetHttpsResponseAsync(url, ct))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(response.Content, output, ct);
            File.Move(temp, destination);
            return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async Task<HttpResponseMessage> GetHttpsResponseAsync(Uri url, CancellationToken ct)
    {
        for (var redirects = 0; redirects < 5; redirects++)
        {
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidOperationException("Redirect missing location.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0) throw new InvalidOperationException("Unsafe download redirect.");
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidOperationException("Too many download redirects.");
    }
    private static async Task<bool> Matches(string file, string hash, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)), hash, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task CopyBoundedAsync(HttpContent content, Stream output, CancellationToken ct)
    {
        const long maxBytes = 2L * 1024 * 1024 * 1024;
        if (content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("Download exceeds 2 GiB limit.");
        await using var input = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > maxBytes) throw new InvalidOperationException("Download exceeds 2 GiB limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
}
public sealed class DownloadPackageProvider(PackageProvider kind, IDownloads downloader, HttpClient client, IProcessRunner runner, UninstallRegistryDetector detector) : IPackageProvider
{
    public PackageProvider Kind => kind;
    public Task<bool> IsInstalledAsync(PackageSpec package, CancellationToken ct) => Task.FromResult(detector.IsInstalled(package.UninstallDisplayName!));
    public async Task InstallAsync(PackageSpec package, CancellationToken ct)
    {
        var url = package.Provider == PackageProvider.Github ? await GetGithubAssetAsync(package, ct) : package.Url!;
        var file = package.Sha256 is null ? await downloader.DownloadTemporaryAsync(url, ct) : await downloader.DownloadVerifiedAsync(url, package.Sha256, ct);
        var args = package.Installer switch
        {
            InstallerKind.Msi => new[] { "/i", file, "/qn", "/norestart" },
            InstallerKind.Exe => new[] { file }.Concat(package.SilentMode switch
            {
                SilentMode.None => Array.Empty<string>(), SilentMode.S => ["/S"], SilentMode.Silent => ["/silent"], SilentMode.VerySilent => ["/VERYSILENT"],
                _ => throw new InvalidOperationException("Unsupported silent mode.")
            }).ToArray(),
            _ => throw new InvalidOperationException("Unsupported installer.")
        };
        try
        {
            var result = package.Installer == InstallerKind.Msi
                ? await runner.RunAsync("msiexec.exe", args, ct)
                : await runner.RunAsync(args[0], args[1..], ct);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Installer exited with code {result.ExitCode}.");
            if (!detector.IsInstalled(package.UninstallDisplayName!)) throw new InvalidOperationException("Installer exited successfully but package detection did not confirm installation.");
        }
        finally { if (package.Sha256 is null && File.Exists(file)) File.Delete(file); }
    }
    private async Task<Uri> GetGithubAssetAsync(PackageSpec package, CancellationToken ct)
    {
        var uri = new Uri($"https://api.github.com/repos/{package.Repository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WinRebuilder", "0.1"));
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != package.Asset) continue;
            var download = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (download.Scheme != Uri.UriSchemeHttps || download.Host != "github.com") throw new InvalidOperationException("Unexpected GitHub asset URL.");
            return download;
        }
        throw new InvalidOperationException("Exact GitHub release asset not found.");
    }
}
