using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServerBrain;

public static class AppPaths
{
    public static string DataDir { get; } = Resolve();

    public static string LogsDir => Directory.CreateDirectory(Path.Combine(DataDir, "logs")).FullName;
    public static string BackupsDir => Directory.CreateDirectory(Path.Combine(DataDir, "backups")).FullName;
    public static string WebViewDir => Directory.CreateDirectory(Path.Combine(DataDir, "webview")).FullName;
    public static string StateFile => Path.Combine(DataDir, "projects.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ActivityFile => Path.Combine(DataDir, "activity.json");
    public static string SecretFile => Path.Combine(DataDir, "secret.key");

    private static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("SB_DATA");
        string dir;
        if (!string.IsNullOrWhiteSpace(env)) dir = env;
        else if (OperatingSystem.IsWindows())
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerBrain");
        else
            dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
                ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "serverbrain");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>Atomic JSON file storage with a one-deep .bak copy.</summary>
public sealed class JsonFile<T> where T : class, new()
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonFile(string path) => _path = path;

    public T Load()
    {
        lock (_gate)
        {
            foreach (var candidate in new[] { _path, _path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var obj = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), Json.Options);
                    if (obj != null) return obj;
                }
                catch { /* fall through to the backup */ }
            }
            return new T();
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Pretty));
            if (File.Exists(_path))
            {
                try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { }
                File.Move(tmp, _path, overwrite: true);
            }
            else File.Move(tmp, _path);
        }
    }
}

/// <summary>Protects small secrets (tunnel tokens). DPAPI on Windows, an AES key file elsewhere.</summary>
public static class Secrets
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedDataWrap.Protect(data));
        using var aes = Aes.Create();
        aes.Key = FileKey();
        aes.GenerateIV();
        var enc = aes.EncryptCbc(data, aes.IV);
        return "aes:" + Convert.ToBase64String(aes.IV.Concat(enc).ToArray());
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedDataWrap.Unprotect(Convert.FromBase64String(stored[6..])));
            if (stored.StartsWith("aes:"))
            {
                var raw = Convert.FromBase64String(stored[4..]);
                using var aes = Aes.Create();
                aes.Key = FileKey();
                return Encoding.UTF8.GetString(aes.DecryptCbc(raw.AsSpan(16), raw.AsSpan(0, 16)));
            }
        }
        catch { }
        return "";
    }

    private static byte[] FileKey()
    {
        var p = AppPaths.SecretFile;
        if (!File.Exists(p)) File.WriteAllBytes(p, RandomNumberGenerator.GetBytes(32));
        return File.ReadAllBytes(p);
    }

    /// <summary>Per-install random secret used to sign public access cookies.</summary>
    public static byte[] SigningKey() => FileKey();
}

internal static class ProtectedDataWrap
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static byte[] Protect(byte[] d) =>
        ProtectedData.Protect(d, null, DataProtectionScope.CurrentUser);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static byte[] Unprotect(byte[] d) =>
        ProtectedData.Unprotect(d, null, DataProtectionScope.CurrentUser);
}
