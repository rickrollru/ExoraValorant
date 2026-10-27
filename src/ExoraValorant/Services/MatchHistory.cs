using System.Text.Json.Nodes;
using Exora.Riot;

namespace Exora.Services;

// Scoreboard of a finished match for the match page opened from "Recent matches".
// Built from match-details; the full JSON is dropped right after, only the scoreboard is kept (last few matches).
public sealed class MatchHistory(GameContent content, PlayerStats stats)
{
    private const int Keep = 6;
    private readonly Dictionary<string, HistoryMatchDto> _cache = new();
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public async Task<HistoryMatchDto?> GetAsync(string matchId, string myPuuid, CancellationToken ct)
    {
        lock (_gate)
            if (_cache.TryGetValue(matchId, out var cached)) return cached;

        var details = await stats.GetMatchDetailsAsync(matchId, ct);
        if (details == null) return null;
        var dto = Build(matchId, details, myPuuid);

        lock (_gate)
        {
            if (_cache.TryAdd(matchId, dto)) _order.Enqueue(matchId);
            while (_order.Count > Keep) _cache.Remove(_order.Dequeue());
        }
        return dto;
    }

    private HistoryMatchDto Build(string matchId, JsonNode details, string myPuuid)
    {
        var info = details["matchInfo"];
        var players = details["players"]?.AsArray().Where(p => p != null && p["isObserver"]?.GetValue<bool>() != true)
            .Select(p => p!).ToList() ?? [];
        var teams = details["teams"]?.AsArray().Where(t => t != null).Select(t => t!).ToList() ?? [];
        var rounds = Math.Max(1, details["roundResults"]?.AsArray().Count ?? 1);

        // Damage and shots per player, summed over all rounds.
        var damage = new Dictionary<string, (int Damage, int Head, int Body, int Leg)>();
        foreach (var round in details["roundResults"]?.AsArray() ?? [])
        foreach (var ps in round?["playerStats"]?.AsArray() ?? [])
        {
            var subject = ps?["subject"]?.GetValue<string>();
            if (subject == null) continue;
            var d = damage.GetValueOrDefault(subject);
            foreach (var hit in ps!["damage"]?.AsArray() ?? [])
            {
                d.Damage += PlayerStats.Int(hit, "damage");
                d.Head += PlayerStats.Int(hit, "headshots");
                d.Body += PlayerStats.Int(hit, "bodyshots");
                d.Leg += PlayerStats.Int(hit, "legshots");
            }
            damage[subject] = d;
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
            var d = damage.GetValueOrDefault(subject);
            var shots = d.Head + d.Body + d.Leg;
            var agent = content.Agent(p["characterId"]?.GetValue<string>());
            var tier = content.Tier(PlayerStats.Int(p, "competitiveTier"));
            var name = p["gameName"]?.GetValue<string>() ?? "";
            var partyId = p["partyId"]?.GetValue<string>() ?? "";
            return new HistoryPlayerDto
            {
                Puuid = subject,
                Name = name,
                Tag = p["tagLine"]?.GetValue<string>() ?? "",
                // Riot leaves the name out for players who hide it; show their agent instead, like in a live match.
                Hidden = name == "" && subject != myPuuid,
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
                Adr = d.Damage / rounds,
                Hs = shots > 0 ? (int)Math.Round(d.Head * 100.0 / shots) : null,
                Party = partyIndex.TryGetValue(partyId, out var idx) ? idx : null,
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
