using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Exora.Riot;

namespace Exora.Services;

public sealed record RankInfo(int Tier, int Rr, int Leaderboard, int PeakTier, string PeakAct, int PrevTier, int? Wr, int Games);

public sealed record CombatStats(double? Kd, int? Hs);

// Ranks, peaks and recent combat stats for any player. Port of vRY rank.py and player_stats.py.
public sealed class PlayerStats(RiotClient riot, GameContent content)
{
    private static readonly TimeSpan RankTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTime At, RankInfo Info)> _ranks = new();
    private readonly ConcurrentDictionary<string, CombatStats> _combat = new();
    private readonly ConcurrentDictionary<string, JsonNode?> _matchDetails = new();

    public void Clear()
    {
        _ranks.Clear();
        _combat.Clear();
    }

    public RankInfo? CachedRank(string puuid) => _ranks.TryGetValue(puuid, out var r) ? r.Info : null;

    public CombatStats? CachedCombat(string puuid) => _combat.TryGetValue(puuid, out var c) ? c : null;

    public async Task<RankInfo> GetRankAsync(string puuid, CancellationToken ct, bool force = false)
    {
        if (!force && _ranks.TryGetValue(puuid, out var cached) && DateTime.UtcNow - cached.At < RankTtl)
            return cached.Info;

        var mmr = await riot.PdGetAsync($"/mmr/v1/players/{puuid}", ct);
        var info = ParseRank(mmr);
        _ranks[puuid] = (DateTime.UtcNow, info);
        return info;
    }

    private RankInfo ParseRank(JsonNode? mmr)
    {
        var seasons = mmr?["QueueSkills"]?["competitive"]?["SeasonalInfoBySeasonID"] as JsonObject;
        var current = content.CurrentSeasonId;
        int tier = 0, rr = 0, lb = 0, games = 0, prevTier = 0;
        int? wr = null;

        if (seasons != null && current != null && seasons[current] is JsonObject cur)
        {
            tier = Int(cur, "CompetitiveTier");
            if (tier <= 2) tier = 0;
            else
            {
                rr = Int(cur, "RankedRating");
                lb = Int(cur, "LeaderboardRank");
            }
            games = Int(cur, "NumberOfGames");
            var wins = Int(cur, "NumberOfWinsWithPlacements");
            if (games > 0) wr = (int)Math.Round(wins * 100.0 / games);
        }

        if (seasons != null && content.PreviousSeasonId != null && seasons[content.PreviousSeasonId] is JsonObject prev)
        {
            prevTier = Int(prev, "CompetitiveTier");
            if (prevTier <= 2) prevTier = 0;
        }

        // Peak = highest tier the player has won a game at, across all seasons.
        var peak = tier;
        var peakSeason = current ?? "";
        if (seasons != null)
        {
            foreach (var (seasonId, season) in seasons)
            {
                if (season?["WinsByTier"] is not JsonObject winsByTier) continue;
                foreach (var (tierKey, _) in winsByTier)
                {
                    if (!int.TryParse(tierKey, out var t)) continue;
                    t = GameContent.NormalizePeakTier(seasonId, t);
                    if (t > peak)
                    {
                        peak = t;
                        peakSeason = seasonId;
                    }
                }
            }
        }

        return new RankInfo(tier, rr, lb, peak, content.ActLabel(peakSeason), prevTier, wr, games);
    }

    // K/D and headshot % from the player's latest competitive match.
    public async Task<CombatStats> GetCombatAsync(string puuid, CancellationToken ct)
    {
        if (_combat.TryGetValue(puuid, out var cached)) return cached;

        var updates = await riot.PdGetAsync($"/mmr/v1/players/{puuid}/competitiveupdates?startIndex=0&endIndex=1&queue=competitive", ct);
        var matchId = updates?["Matches"]?[0]?["MatchID"]?.GetValue<string>();
        var stats = new CombatStats(null, null);
        if (matchId != null)
        {
            var details = await GetMatchDetailsAsync(matchId, ct);
            if (details != null) stats = ComputeCombat(details, puuid);
        }
        _combat[puuid] = stats;
        return stats;
    }

    public async Task<JsonNode?> GetMatchDetailsAsync(string matchId, CancellationToken ct)
    {
        if (_matchDetails.TryGetValue(matchId, out var cached)) return cached;
        var details = await riot.PdGetAsync($"/match-details/v1/matches/{matchId}", ct);
        if (details != null) _matchDetails[matchId] = details;
        return details;
    }

    private static CombatStats ComputeCombat(JsonNode match, string puuid)
    {
        int hits = 0, heads = 0;
        foreach (var round in match["roundResults"]?.AsArray() ?? [])
        foreach (var ps in round?["playerStats"]?.AsArray() ?? [])
        {
            if (ps?["subject"]?.GetValue<string>() != puuid) continue;
            foreach (var d in ps["damage"]?.AsArray() ?? [])
            {
                if (d == null) continue;
                var h = Int(d, "headshots");
                heads += h;
                hits += h + Int(d, "bodyshots") + Int(d, "legshots");
            }
        }

        double? kd = null;
        foreach (var p in match["players"]?.AsArray() ?? [])
        {
            if (p?["subject"]?.GetValue<string>() != puuid) continue;
            var kills = Int(p["stats"], "kills");
            var deaths = Int(p["stats"], "deaths");
            kd = Math.Round(deaths == 0 ? kills : (double)kills / deaths, 2);
        }

        return new CombatStats(kd, hits > 0 ? (int)Math.Round(heads * 100.0 / hits) : null);
    }

    public static int Int(JsonNode? node, string key)
    {
        if (node?[key] is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return 0;
    }
}
