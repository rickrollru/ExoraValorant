using System.Text.Json;
using Exora.Core;

namespace Exora.Services;

// Profile of the last account played on, kept on disk so Home isn't empty while Valorant is closed.
public static class LastAccount
{
    private static readonly string FilePath = Path.Combine(AppInfo.DataDir, "last_account.json");

    public static MeDto? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<MeDto>(File.ReadAllText(FilePath), AppInfo.Json) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to read the last account: " + ex.Message);
            return null;
        }
    }

    public static void Save(MeDto me)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(me, AppInfo.Json));
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to save the last account: " + ex.Message);
        }
    }
}
