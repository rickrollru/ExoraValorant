using System.Globalization;
using System.Text.Json.Nodes;
using Exora.Core;

namespace Exora.Riot;

public sealed record AgentInfo(string Uuid, string Name, string Icon);

public sealed record MapInfo(string Uuid, string Name, string Splash, string ListIcon);

public sealed record TierInfo(int Tier, string Name, string? Icon);

// Static game data from valorant-api.com (names, icons) plus season info from Riot's content service.
// Cached on disk so the app starts instantly and works when the API is slow.
public sealed class GameContent
{
    private const string Api = "https://valorant-api.com/v1";
    private const string StandardModeUuid = "96bd3920-4f36-d026-2b28-c683eb0bcac5";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    // Seasons before Ascendant was added: tiers above 20 there map 3 tiers higher today (from vRY constants.py).
    private static readonly HashSet<string> BeforeAscendant = new(StringComparer.OrdinalIgnoreCase)
    {
        "0df5adb9-4dcb-6899-1306-3e9860661dd3", "3f61c772-4560-cd3f-5d3f-a7ab5abda6b3", "0530b9c4-4980-f2ee-df5d-09864cd00542",
        "46ea6166-4573-1128-9cea-60a15640059b", "fcf2c8f4-4324-e50b-2e23-718e4a3ab046", "97b6e739-44cc-ffa7-49ad-398ba502ceb0",
        "ab57ef51-4e59-da91-cc8d-51a5a2b9b8ff", "52e9749a-429b-7060-99fe-4595426a0cf7", "71c81c67-4fae-ceb1-844c-aab2bb8710fa",
        "2a27e5d2-4d30-c9e2-b15a-93b8909a442c", "4cb622e1-4244-6da3-7276-8daaf1c01be2", "a16955a5-4ad0-f761-5e9e-389df1c892fb",
        "97b39124-46ce-8b55-8fd1-7cbf7ffe173f", "573f53ac-41a5-3a7d-d9ce-d6a6298e5704", "d929bc38-4ab6-7da4-94f0-ee84f8ac141e",
        "3e47230a-463c-a301-eb7d-67bb60357d4f", "808202d6-4f2b-a8ff-1feb-b3a0590ad79f",
    };

    private static readonly Dictionary<string, (string En, string Ru)> Queues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["competitive"] = ("Competitive", "Рейтинг"),
        ["unrated"] = ("Unrated", "Без рейтинга"),
        ["swiftplay"] = ("Swiftplay", "Быстрая игра"),
        ["spikerush"] = ("Spike Rush", "Захват спайка"),
        ["deathmatch"] = ("Deathmatch", "Бой насмерть"),
        ["hurm"] = ("Team Deathmatch", "Командный бой насмерть"),
        ["ggteam"] = ("Escalation", "Эскалация"),
        ["onefa"] = ("Replication", "Репликация"),
        ["snowball"] = ("Snowball Fight", "Снежная битва"),
        ["premier"] = ("Premier", "Premier"),
        ["newmap"] = ("New Map", "Новая карта"),
        ["valaram"] = ("All Random One Site", "All Random One Site"),
        ["skirmish"] = ("Skirmish", "Стычка"),
        ["skirmish2v2"] = ("Skirmish 2v2", "Стычка 2 на 2"),
        ["skirmish1v1"] = ("Skirmish 1v1", "Стычка 1 на 1"),
        ["custom"] = ("Custom Game", "Своя игра"),
        [""] = ("Custom Game", "Своя игра"),
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private string _lang = "en";

    public Dictionary<string, AgentInfo> Agents { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MapInfo> MapsByUrl { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MapInfo> MapsByUuid { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, TierInfo> Tiers { get; private set; } = new();
    public Dictionary<string, string> Titles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ValorantLogo { get; private set; } = "logo";

    public string? CurrentSeasonId { get; private set; }
    public string? PreviousSeasonId { get; private set; }
    private List<(string Id, string Name, string Type)> _seasons = [];

    public bool Loaded => Agents.Count > 0;

    public async Task LoadAsync(string lang, CancellationToken ct)
    {
        _lang = lang == "ru" ? "ru-RU" : "en-US";
        var agentsTask = GetCachedAsync($"{Api}/agents?isPlayableCharacter=true&language={_lang}", ct);
        var mapsTask = GetCachedAsync($"{Api}/maps?language={_lang}", ct);
        var tiersTask = GetCachedAsync($"{Api}/competitivetiers?language={_lang}", ct);
        var titlesTask = GetCachedAsync($"{Api}/playertitles?language={_lang}", ct);
        var modesTask = GetCachedAsync($"{Api}/gamemodes", ct);
        await Task.WhenAll(agentsTask, mapsTask, tiersTask, titlesTask, modesTask);

        var agents = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Items(agentsTask.Result))
        {
            var uuid = Str(a, "uuid");
            agents[uuid] = new AgentInfo(uuid, Str(a, "displayName"), Str(a, "displayIcon"));
        }
        if (agents.Count > 0) Agents = agents;

        var byUrl = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        var byUuid = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Items(mapsTask.Result))
        {
            var info = new MapInfo(Str(m, "uuid"), Str(m, "displayName"), Str(m, "splash"), Str(m, "listViewIcon"));
            byUrl[Str(m, "mapUrl")] = info;
            byUuid[info.Uuid] = info;
        }
        if (byUrl.Count > 0)
        {
            MapsByUrl = byUrl;
            MapsByUuid = byUuid;
        }

        // The last tier table is the current one.
        var tierTable = Items(tiersTask.Result).LastOrDefault();
        if (tierTable != null)
        {
            var tiers = new Dictionary<int, TierInfo>();
            foreach (var t in tierTable["tiers"]?.AsArray() ?? [])
            {
                if (t == null) continue;
                var n = t["tier"]?.GetValue<int>() ?? 0;
                tiers[n] = new TierInfo(n, TitleCase(Str(t, "tierName")), n > 2 ? NullIfEmpty(Str(t, "smallIcon")) : null);
            }
            Tiers = tiers;
        }

        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Items(titlesTask.Result))
        {
            var text = Str(t, "titleText");
            if (text != "") titles[Str(t, "uuid")] = text;
        }
        if (titles.Count > 0) Titles = titles;

        // valorant-api has no logo endpoint; the Standard game mode icon is the Valorant "V".
        // Without it RPC falls back to an art asset named "logo" in the user's Discord application.
        var standard = Items(modesTask.Result).FirstOrDefault(m => Str(m, "uuid") == StandardModeUuid);
        ValorantLogo = standard == null ? "logo" : Str(standard, "displayIcon") is { Length: > 0 } icon ? icon : "logo";
    }

    // Season list from Riot's content service; needed for current-act rank and peak act names.
    public async Task LoadSeasonsAsync(RiotClient riot, CancellationToken ct)
    {
        var content = await riot.SharedGetAsync("/content-service/v3/content", ct);
        if (content?["Seasons"] is not JsonArray seasons) return;

        _seasons = seasons.Where(s => s != null)
            .Select(s => (Str(s!, "ID"), Str(s!, "Name"), Str(s!, "Type")))
            .ToList();

        JsonNode? current = seasons.FirstOrDefault(s => s?["IsActive"]?.GetValue<bool>() == true && Str(s, "Type") == "act");
        CurrentSeasonId = current == null ? null : Str(current, "ID");
        if (current != null)
        {
            var start = Str(current, "StartTime");
            var prev = seasons.FirstOrDefault(s => s != null && Str(s, "Type") == "act" && Str(s, "EndTime") == start);
            PreviousSeasonId = prev == null ? null : Str(prev, "ID");
        }
    }

    public AgentInfo? Agent(string? uuid) =>
        uuid != null && Agents.TryGetValue(uuid, out var a) ? a : null;

    public MapInfo? MapByUrl(string? url) =>
        url != null && MapsByUrl.TryGetValue(url, out var m) ? m : null;

    public TierInfo Tier(int tier) =>
        Tiers.TryGetValue(tier, out var t) ? t : new TierInfo(tier, tier <= 2 ? (_lang == "ru-RU" ? "Без ранга" : "Unranked") : $"Tier {tier}", null);

    public string? Title(string? uuid) =>
        uuid != null && Titles.TryGetValue(uuid, out var t) ? t : null;

    public static string? CardWide(string? uuid) =>
        string.IsNullOrEmpty(uuid) ? null : $"https://media.valorant-api.com/playercards/{uuid}/wideart.png";

    public static string? CardSmall(string? uuid) =>
        string.IsNullOrEmpty(uuid) ? null : $"https://media.valorant-api.com/playercards/{uuid}/smallart.png";

    public string QueueName(string? queueId, bool custom = false)
    {
        if (custom) queueId = "custom";
        queueId ??= "";
        if (Queues.TryGetValue(queueId, out var q)) return _lang == "ru-RU" ? q.Ru : q.En;
        return TitleCase(queueId);
    }

    public static int NormalizePeakTier(string seasonId, int tier) =>
        BeforeAscendant.Contains(seasonId) && tier > 20 ? tier + 3 : tier;

    // "E9 A2" style label for a season id, or "" when unknown.
    public string ActLabel(string? actId)
    {
        if (actId == null || _seasons.Count == 0) return "";
        string? episode = null;
        string? act = null;
        foreach (var s in _seasons)
        {
            if (s.Type == "episode") episode = s.Name;
            if (string.Equals(s.Id, actId, StringComparison.OrdinalIgnoreCase))
            {
                act = s.Name;
                break;
            }
        }
        if (act == null) return "";
        var a = SeasonNumber(act);
        var e = episode == null ? null : SeasonNumber(episode);
        // Newer episodes are named like "V26" instead of a number; show those as is.
        if (e == null) return $"A{a}";
        return int.TryParse(e, out _) ? $"E{e} A{a}" : $"{e} A{a}";
    }

    private static string? SeasonNumber(string name)
    {
        var last = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last == null) return null;
        if (int.TryParse(last, out var n)) return n.ToString();
        var roman = RomanToInt(last);
        return roman > 0 ? roman.ToString() : last.ToUpperInvariant();
    }

    private static int RomanToInt(string s)
    {
        var map = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100 };
        int total = 0, prev = 0;
        foreach (var c in s.ToUpperInvariant().Reverse())
        {
            if (!map.TryGetValue(c, out var v)) return 0;
            total += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }
        return total;
    }

    private async Task<JsonNode?> GetCachedAsync(string url, CancellationToken ct)
    {
        var file = Path.Combine(AppInfo.CacheDir, "api_" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16] + ".json");
        var fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < CacheTtl;
        if (!fresh)
        {
            try
            {
                var text = await _http.GetStringAsync(url, ct);
                await File.WriteAllTextAsync(file, text, ct);
                return JsonNode.Parse(text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"valorant-api request failed ({url}): {ex.Message}");
            }
        }
        if (!File.Exists(file)) return null;
        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(file, ct));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonNode> Items(JsonNode? root) =>
        (root?["data"] as JsonArray)?.Where(n => n != null).Select(n => n!) ?? [];

    private static string Str(JsonNode node, string key)
    {
        try
        {
            return node[key]?.GetValue<string>() ?? "";
        }
        catch (InvalidOperationException)
        {
            return node[key]?.ToString() ?? "";
        }
    }

    private static string? NullIfEmpty(string s) => s == "" ? null : s;

    private static string TitleCase(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}
