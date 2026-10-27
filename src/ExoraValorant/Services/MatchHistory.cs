using System.Text.Json.Nodes;
using Exora.Riot;

namespace Exora.Services;

// Scoreboard of a finished match for the match page opened from "Recent matches".
// Built from match-details; the full JSON is dropped right after, only the scoreboard is kept (last few matches).
public sealed class MatchHistory(GameContent content, PlayerStats stats)
{
    private const int Keep = 6;
    private const int TradeWindowMs = 5000;
    private readonly Dictionary<string, HistoryMatchDto> _cache = new();
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    // knownName: name and tag for players we already know (us, friends). Riot leaves names out of finished matches,
    // so everyone else is shown by agent.
    public async Task<HistoryMatchDto?> GetAsync(string matchId, string myPuuid, Func<string, (string Name, string Tag)?> knownName,
        CancellationToken ct)
    {
        var key = matchId + ":" + myPuuid;
        lock (_gate)
            if (_cache.TryGetValue(key, out var cached)) return cached;

        var details = await stats.GetMatchDetailsAsync(matchId, ct);
        if (details == null) return null;
        var dto = Build(matchId, details, myPuuid, knownName);

        lock (_gate)
        {
            if (_cache.TryAdd(key, dto)) _order.Enqueue(key);
            while (_order.Count > Keep) _cache.Remove(_order.Dequeue());
        }
        return dto;
    }

    private sealed class Tally
    {
        public int Damage, Received, Head, Body, Leg, FirstKills, FirstDeaths, Plants, Defuses, KastRounds, Multi3, Multi4, Multi5;
        public long Loadout;
        public int LoadoutRounds;
    }

    private HistoryMatchDto Build(string matchId, JsonNode details, string myPuuid, Func<string, (string Name, string Tag)?> knownName)
    {
        var info = details["matchInfo"];
        var players = details["players"]?.AsArray().Where(p => p != null && p["isObserver"]?.GetValue<bool>() != true)
            .Select(p => p!).ToList() ?? [];
        var teams = details["teams"]?.AsArray().Where(t => t != null).Select(t => t!).ToList() ?? [];
        var roundResults = details["roundResults"]?.AsArray().Where(r => r != null).Select(r => r!).ToList() ?? [];
        var rounds = Math.Max(1, roundResults.Count);
        var teamOf = players.ToDictionary(p => p["subject"]?.GetValue<string>() ?? "", p => p["teamId"]?.GetValue<string>() ?? "");
        var tally = players.ToDictionary(p => p["subject"]?.GetValue<string>() ?? "", _ => new Tally());
        Tally T(string? s) => s != null && tally.TryGetValue(s, out var t) ? t : new Tally();

        foreach (var round in roundResults)
        {
            var kills = new List<(long Time, string Killer, string Victim, List<string> Assists)>();
            foreach (var ps in round["playerStats"]?.AsArray() ?? [])
            {
                var subject = ps?["subject"]?.GetValue<string>();
                if (subject == null) continue;
                var t = T(subject);
                foreach (var hit in ps!["damage"]?.AsArray() ?? [])
                {
                    var dmg = PlayerStats.Int(hit, "damage");
                    t.Damage += dmg;
                    t.Head += PlayerStats.Int(hit, "headshots");
                    t.Body += PlayerStats.Int(hit, "bodyshots");
                    t.Leg += PlayerStats.Int(hit, "legshots");
                    T(hit?["receiver"]?.GetValue<string>()).Received += dmg;
                }
                if (ps["economy"] is JsonObject eco)
                {
                    t.Loadout += PlayerStats.Int(eco, "loadoutValue");
                    t.LoadoutRounds++;
                }
                foreach (var k in ps["kills"]?.AsArray() ?? [])
                {
                    if (k == null) continue;
                    var time = k["roundTime"]?.GetValue<long>() ?? k["gameTime"]?.GetValue<long>() ?? 0;
                    var assists = k["assistants"]?.AsArray().Select(a => a?.GetValue<string>() ?? "").ToList() ?? [];
                    kills.Add((time, k["killer"]?.GetValue<string>() ?? subject, k["victim"]?.GetValue<string>() ?? "", assists));
                }
            }

            kills.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (kills.Count > 0)
            {
                T(kills[0].Killer).FirstKills++;
                T(kills[0].Victim).FirstDeaths++;
            }
            foreach (var g in kills.GroupBy(k => k.Killer))
            {
                var n = g.Count();
                if (n == 3) T(g.Key).Multi3++;
                else if (n == 4) T(g.Key).Multi4++;
                else if (n >= 5) T(g.Key).Multi5++;
            }

            var planter = round["bombPlanter"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(planter)) T(planter).Plants++;
            var defuser = round["bombDefuser"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(defuser)) T(defuser).Defuses++;

            // KAST: a kill, an assist, survived, or died and was traded within 5 seconds.
            foreach (var (subject, t) in tally)
            {
                var death = kills.FirstOrDefault(k => k.Victim == subject);
                var died = death.Victim == subject;
                var traded = died && kills.Any(k => k.Victim == death.Killer && k.Time >= death.Time && k.Time - death.Time <= TradeWindowMs
                                                    && teamOf.GetValueOrDefault(k.Killer) == teamOf.GetValueOrDefault(subject));
                if (kills.Any(k => k.Killer == subject) || kills.Any(k => k.Assists.Contains(subject)) || !died || traded)
                    t.KastRounds++;
            }
        }

        // Parties as recorded by Riot for this match: exact, not guessed.
        var partyIndex = players.Select(p => p["partyId"]?.GetValue<string>() ?? "")
            .Where(id => id != "")
            .GroupBy(id => id).Where(g => g.Count() > 1)
            .Select((g, i) => (g.Key, i)).ToDictionary(x => x.Key, x => x.i);

        var me = players.FirstOrDefault(p => p["subject"]?.GetValue<string>() == myPuuid);
        var myTeam = me?["teamId"]?.GetValue<string>();
        var single = teams.Count != 2;

        var rows = players.Select(p =>
        {
            var subject = p["subject"]?.GetValue<string>() ?? "";
            var s = p["stats"];
            var t = T(subject);
            var shots = t.Head + t.Body + t.Leg;
            var agent = content.Agent(p["characterId"]?.GetValue<string>());
            var tier = content.Tier(PlayerStats.Int(p, "competitiveTier"));
            var name = p["gameName"]?.GetValue<string>() ?? "";
            var tag = p["tagLine"]?.GetValue<string>() ?? "";
            if (name == "" && knownName(subject) is { } known) (name, tag) = known;
            var partyId = p["partyId"]?.GetValue<string>() ?? "";
            var casts = s?["abilityCasts"];
            return new HistoryPlayerDto
            {
                Puuid = subject,
                Name = name,
                Tag = tag,
                Hidden = name == "",
                IsMe = subject == myPuuid,
                Team = p["teamId"]?.GetValue<string>() ?? "",
                Agent = agent == null ? null : new AgentDto(agent.Name, agent.Icon),
                Rank = new RankDto(tier.Tier, tier.Name, tier.Icon),
                Level = PlayerStats.Int(p, "accountLevel"),
                Title = content.Title(p["playerTitle"]?.GetValue<string>()),
                Card = GameContent.CardWide(p["playerCard"]?.GetValue<string>()),
                Kills = PlayerStats.Int(s, "kills"),
                Deaths = PlayerStats.Int(s, "deaths"),
                Assists = PlayerStats.Int(s, "assists"),
                Acs = PlayerStats.Int(s, "score") / rounds,
                Adr = t.Damage / rounds,
                Hs = shots > 0 ? (int)Math.Round(t.Head * 100.0 / shots) : null,
                Party = partyIndex.TryGetValue(partyId, out var idx) ? idx : null,
                Details = new HistoryDetailsDto
                {
                    FirstKills = t.FirstKills,
                    FirstDeaths = t.FirstDeaths,
                    Kast = (int)Math.Round(t.KastRounds * 100.0 / rounds),
                    Plants = t.Plants,
                    Defuses = t.Defuses,
                    Multi3 = t.Multi3,
                    Multi4 = t.Multi4,
                    Multi5 = t.Multi5,
                    Loadout = t.LoadoutRounds > 0 ? (int)(t.Loadout / t.LoadoutRounds) : null,
                    Damage = t.Damage,
                    Received = t.Received,
                    Head = shots > 0 ? (int)Math.Round(t.Head * 100.0 / shots) : 0,
                    Body = shots > 0 ? (int)Math.Round(t.Body * 100.0 / shots) : 0,
                    Leg = shots > 0 ? (int)Math.Round(t.Leg * 100.0 / shots) : 0,
                    Casts = casts == null ? null : new CastsDto(
                        PlayerStats.Int(casts, "grenadeCasts"), PlayerStats.Int(casts, "ability1Casts"),
                        PlayerStats.Int(casts, "ability2Casts"), PlayerStats.Int(casts, "ultimateCasts")),
                },
            };
        }).OrderByDescending(r => r.Acs).ToList();

        var myTeamNode = teams.FirstOrDefault(t => t["teamId"]?.GetValue<string>() == myTeam);
        var otherNode = teams.FirstOrDefault(t => t["teamId"]?.GetValue<string>() != myTeam);
        var won = myTeamNode?["won"]?.GetValue<bool>() == true;
        var draw = !single && !won && otherNode?["won"]?.GetValue<bool>() != true;
        var myRow = rows.FirstOrDefault(r => r.IsMe);

        var map = content.MapByUrl(info?["mapId"]?.GetValue<string>());
        return new HistoryMatchDto
        {
            Id = matchId,
            Map = map == null ? null : new MapDto(map.Name, map.Splash, map.ListIcon),
            Mode = content.QueueName(info?["queueID"]?.GetValue<string>(), info?["provisioningFlowID"]?.GetValue<string>() == "CustomGame"),
            Started = info?["gameStartMillis"]?.GetValue<long>() ?? 0,
            Length = info?["gameLengthMillis"]?.GetValue<long>() ?? 0,
            Result = draw ? "D" : won ? "W" : "L",
            Score = !single && myTeamNode != null && otherNode != null
                ? new ScoreDto(PlayerStats.Int(myTeamNode, "roundsWon"), PlayerStats.Int(otherNode, "roundsWon"))
                : null,
            Placement = single && myRow != null ? rows.Count(r => r.Kills > myRow.Kills) + 1 : null,
            Single = single,
            Allies = single ? rows : rows.Where(r => r.Team == myTeam).ToList(),
            Enemies = single ? [] : rows.Where(r => r.Team != myTeam).ToList(),
        };
    }
}
