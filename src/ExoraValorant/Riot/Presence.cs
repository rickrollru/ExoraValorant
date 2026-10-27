using System.Text;
using System.Text.Json.Nodes;

namespace Exora.Riot;

// One entry of /chat/v4/presences with the Valorant "private" blob decoded.
// Riot switches between a flat layout and one nested under matchPresenceData / partyPresenceData /
// playerPresenceData (see vRY presences.py), so every field lookup checks both.
public sealed class Presence
{
    private static readonly string[] Sections = ["matchPresenceData", "partyPresenceData", "playerPresenceData"];

    private readonly JsonObject? _private;

    public string Puuid { get; }
    public string GameName { get; }
    public string GameTag { get; }
    public string Product { get; }
    public string ChatState { get; }

    private Presence(JsonNode entry)
    {
        Puuid = entry["puuid"]?.GetValue<string>() ?? "";
        GameName = entry["game_name"]?.GetValue<string>() ?? "";
        GameTag = entry["game_tag"]?.GetValue<string>() ?? "";
        Product = entry["product"]?.GetValue<string>() ?? "";
        ChatState = entry["state"]?.GetValue<string>() ?? "";
        _private = Product == "valorant" ? Decode(entry["private"]?.GetValue<string>()) : null;
    }

    public static List<Presence> Parse(JsonNode? root)
    {
        var list = new List<Presence>();
        if (root?["presences"] is not JsonArray arr) return list;
        foreach (var e in arr)
            if (e != null) list.Add(new Presence(e));
        return list;
    }

    public bool IsValorant => _private != null && Bool("isValid") != false;

    public string? SessionLoopState => Str("sessionLoopState");
    public string? QueueId => Str("queueId");
    public string? MatchMap => Str("matchMap");
    public string? ProvisioningFlow => Str("provisioningFlow");
    public string? PartyId => Str("partyId");
    public int PartySize => Int("partySize") ?? 1;
    public int MaxPartySize => Int("maxPartySize") ?? 5;
    public string? PartyAccessibility => Str("partyAccessibility");
    public string? PartyState => Str("partyState");
    public int? ScoreAlly => Int("partyOwnerMatchScoreAllyTeam");
    public int? ScoreEnemy => Int("partyOwnerMatchScoreEnemyTeam");
    public bool IsIdle => Bool("isIdle") == true;
    public int AccountLevel => Int("accountLevel") ?? 0;
    public int CompetitiveTier => Int("competitiveTier") ?? 0;
    public string? PlayerCardId => Str("playerCardId");
    public string? PlayerTitleId => Str("playerTitleId");

    public bool IsCustomGame => ProvisioningFlow == "CustomGame" || PartyState == "CUSTOM_GAME_SETUP";

    private JsonNode? Find(string key)
    {
        if (_private == null) return null;
        if (_private.TryGetPropertyValue(key, out var v) && v != null) return v;
        foreach (var section in Sections)
            if (_private[section] is JsonObject o && o.TryGetPropertyValue(key, out var nested) && nested != null)
                return nested;
        return null;
    }

    private string? Str(string key)
    {
        var v = Find(key);
        if (v is not JsonValue jv) return null;
        return jv.TryGetValue<string>(out var s) ? s : jv.ToString();
    }

    private int? Int(string key)
    {
        var v = Find(key);
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<long>(out var l)) return (int)l;
        if (jv.TryGetValue<double>(out var d)) return (int)d;
        return jv.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
    }

    private bool? Bool(string key)
    {
        var v = Find(key);
        return v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : null;
    }

    private static JsonObject? Decode(string? b64)
    {
        if (string.IsNullOrEmpty(b64) || b64.Contains('{')) return null;
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))) as JsonObject;
        }
        catch
        {
            return null;
        }
    }
}
