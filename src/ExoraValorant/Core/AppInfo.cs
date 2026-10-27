using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Exora.Core;

public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string DataDir { get; } = CreateDir(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exora"));

    public static string CacheDir { get; } = CreateDir(Path.Combine(DataDir, "cache"));

    public static string WebViewDir { get; } = Path.Combine(DataDir, "webview");

    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");

    public static string LogFile { get; } = Path.Combine(DataDir, "exora.log");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
