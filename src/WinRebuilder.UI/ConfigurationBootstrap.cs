using System;
using System.IO;
using System.Reflection;
using System.Text;
using WinRebuilder.Core;

namespace WinRebuilder.UI;

internal static class ConfigurationBootstrap
{
    public static void Ensure(string path)
    {
        if (File.Exists(path)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(Localization.Instance["AppFolderUnavailable"]);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinRebuilder.DefaultConfig")
            ?? throw new InvalidOperationException(Localization.Instance["DefaultConfigMissing"]);
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        ProfileLoader.Load(Encoding.UTF8.GetString(bytes.ToArray()));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                bytes.Position = 0;
                bytes.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temporary, path);
        }
        catch (IOException) when (File.Exists(path)) { }
        catch (UnauthorizedAccessException e) { throw new IOException(Localization.Instance["ConfigWriteError"], e); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
