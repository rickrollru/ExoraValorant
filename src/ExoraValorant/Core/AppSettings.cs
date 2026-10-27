using System.Text.Json;

namespace Exora.Core;

// Mirrors DEFAULT_SETTINGS in wwwroot/js/app.js.
public sealed class AppSettings
{
    public string Lang { get; set; } = "ru";
    public string Theme { get; set; } = "dark";
    public string Accent { get; set; } = "#4F8CFF";
    public int Tint { get; set; }
    public string Font { get; set; } = "Manrope";
    public double Scale { get; set; } = 1;
    public string Anim { get; set; } = "full";
    public bool StreamerMode { get; set; }
    public bool MapBackground { get; set; } = true;
    public int RefreshSec { get; set; } = 3;
    public RpcSettings Rpc { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppInfo.SettingsFile))
            {
                var json = File.ReadAllText(AppInfo.SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, AppInfo.Json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read settings, using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var tmp = AppInfo.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions(AppInfo.Json) { WriteIndented = true }));
            File.Move(tmp, AppInfo.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }
}

public sealed class RpcSettings
{
    public bool Enabled { get; set; } = true;
    public string ClientId { get; set; } = "";
    public RpcImages Game { get; set; } = new() { Large = "map", Small = "agent" };
    public RpcImages Lobby { get; set; } = new() { Large = "logo", Small = "rank" };
    public bool ShowName { get; set; }
    public bool ShowParty { get; set; } = true;
    public bool ShowScore { get; set; } = true;
    public bool ShowTimer { get; set; } = true;
    public RpcButton Button { get; set; } = new();
}

public sealed class RpcImages
{
    public string Large { get; set; } = "none";
    public string Small { get; set; } = "none";
}

public sealed class RpcButton
{
    public bool Enabled { get; set; }
    public string Label { get; set; } = "Exora";
    public string Url { get; set; } = "";
}
