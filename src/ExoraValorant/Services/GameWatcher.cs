using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Exora.Core;
using Exora.Riot;

namespace Exora.Services;

// Polls the Riot client, follows the game loop (menus / agent select / in game) and builds the UI state.
// The polling loop and the per-state flow follow vRY main.py.
public sealed class GameWatcher : IDisposable
{
    private static readonly TimeSpan FriendsTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MineGrace = TimeSpan.FromSeconds(20);

    private readonly Func<AppSettings> _settings;
    private readonly RiotClient _riot = new();
    private readonly GameContent _content = new();
    private readonly PlayerStats _stats;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<string, (string Name, string Tag)> _names = new();

    private Task? _loop;
    private Task? _enrich;
    private string _contentLang = "";

    private string? _lastLoopState;
    private long _stateSince;
    private string? _matchId;
    private JsonNode? _coreMatch;
    private JsonArray _friendsList = [];
    private DateTime _friendsAt;
    private MeDto? _me;
    private DateTime _profileAt;
    private bool _profileDirty = true;
    private Presence? _lastMine;
    private DateTime _lastMineAt;
    private bool _mineMissingLogged;

    public event Action<UiState>? StateChanged;
    public UiState Current { get; private set; } = new() { Connection = "connecting" };

    // Data the Discord presence needs that is not part of the UI state.
    public Presence? MyPresence { get; private set; }
    public GameContent Content => _content;

    public GameWatcher(Func<AppSettings> settings)
    {
        _settings = settings;
        _stats = new PlayerStats(_riot, _content);
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (RiotNotRunningException ex)
            {
                if (_riot.IsConnected) Log.Info("Lost Riot client: " + ex.Message);
                ResetSession();
                Publish(new UiState { Connection = RiotClient.IsValorantRunning() ? "connecting" : "none", ValorantRunning = RiotClient.IsValorantRunning() });
            }
            catch (Exception ex)
            {
                Log.Error("Watcher tick failed", ex);
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(_settings().RefreshSec, 1, 30));
            try
            {
                await _wake.WaitAsync(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ResetSession()
    {
        _riot.Disconnect();
        _lastLoopState = null;
        _matchId = null;
        _coreMatch = null;
        _me = null;
        _profileDirty = true;
        _friendsList = [];
        _friendsAt = default;
        MyPresence = null;
        _lastMine = null;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var lang = _settings().Lang;
        if (_contentLang != lang)
        {
            await _content.LoadAsync(lang, ct);
            _contentLang = lang;
            _me = null;
            _profileDirty = true;
        }

        var running = RiotClient.IsValorantRunning();
        if (!running)
        {
            if (_riot.IsConnected) ResetSession();
            Publish(new UiState { Connection = "none" });
            return;
        }

        if (!_riot.IsConnected)
        {
            Publish(new UiState { Connection = "connecting", ValorantRunning = true });
            await _riot.ConnectAsync(ct);
            await _content.LoadSeasonsAsync(_riot, ct);
        }

        var presences = Presence.Parse(await _riot.LocalGetAsync("/chat/v4/presences", ct));
        var mine = presences.FirstOrDefault(p => p.Puuid == _riot.Puuid && p.IsValorant);
        if (mine != null)
        {
            _lastMine = mine;
            _lastMineAt = DateTime.UtcNow;
            _mineMissingLogged = false;
        }
        else
        {
            if (!_mineMissingLogged)
            {
                var own = presences.Where(p => p.Puuid == _riot.Puuid).Select(p => p.Describe()).ToList();
                Log.Warn($"Own Valorant presence not found ({presences.Count} presences; own entries: {(own.Count == 0 ? "none" : string.Join(" | ", own))})");
                _mineMissingLogged = true;
            }
            // The chat service sometimes skips our entry for a poll or two; keep the last one briefly.
            if (_lastMine != null && DateTime.UtcNow - _lastMineAt < MineGrace) mine = _lastMine;
        }
        MyPresence = mine;

        var loopState = mine?.SessionLoopState;
        if (loopState != _lastLoopState)
        {
            Log.Info($"Game state {_lastLoopState ?? "-"} -> {loopState ?? "-"}");
            _stateSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (loopState == "MENUS") _profileDirty = true;
            if (loopState != "INGAME")
            {
                _coreMatch = null;
            }
            if (loopState == "MENUS") _matchId = null;
            _lastLoopState = loopState;
        }

        if (_profileDirty || _me == null || DateTime.UtcNow - _profileAt > ProfileTtl)
        {
            _me = await BuildProfileAsync(mine, ct);
            _profileAt = DateTime.UtcNow;
            _profileDirty = false;
        }
        else if (mine != null)
        {
            _me = _me with
            {
                Level = mine.AccountLevel > 0 ? mine.AccountLevel : _me.Level,
                Card = GameContent.CardWide(mine.PlayerCardId) ?? _me.Card,
                CardSmall = GameContent.CardSmall(mine.PlayerCardId) ?? _me.CardSmall,
                Title = _content.Title(mine.PlayerTitleId) ?? _me.Title,
            };
        }

        MatchDto? match = null;
        var isRange = mine?.MatchMap?.Contains("Range", StringComparison.OrdinalIgnoreCase) == true;
        if (!isRange && loopState == "PREGAME") match = await BuildPregameAsync(mine!, presences, ct);
        else if (!isRange && loopState == "INGAME") match = await BuildCoregameAsync(mine!, presences, ct);

        if (match != null)
        {
            var me = match.Allies.FirstOrDefault(p => p.IsMe);
            if (me?.Agent != null) _me = _me with { Agent = me.Agent };
        }

        var friends = await BuildFriendsAsync(presences, ct);

        StatusDto? status = null;
        if (mine != null && loopState != null)
        {
            var mode = isRange ? (lang == "ru" ? "Стрельбище" : "The Range") : _content.QueueName(mine.QueueId, mine.IsCustomGame);
            var score = loopState == "INGAME" && mine.ScoreAlly != null
                ? new ScoreDto(mine.ScoreAlly ?? 0, mine.ScoreEnemy ?? 0)
                : null;
            status = new StatusDto(loopState, mode, mine.QueueId ?? "", new PartyDto(mine.PartySize, mine.MaxPartySize), score, _stateSince);
        }

        Publish(new UiState
        {
            Connection = "ok",
            ValorantRunning = true,
            Status = status,
            Me = _me,
            Match = match,
            Friends = friends,
            Discord = Current.Discord,
        });
    }

    public void SetDiscord(bool connected)
    {
        if (Current.Discord.Connected == connected) return;
        Publish(Current with { Discord = new DiscordDto(connected) });
    }

    private void Publish(UiState state)
    {
        state = state with { Discord = Current.Discord };
        Current = state;
        StateChanged?.Invoke(state);
    }

    // ---------- Profile ----------

    private async Task<MeDto> BuildProfileAsync(Presence? mine, CancellationToken ct)
    {
        var puuid = _riot.Puuid;
        await ResolveNamesAsync([puuid], ct);
        var (name, tag) = _names.TryGetValue(puuid, out var n) ? n : ("", "");
        var rank = await _stats.GetRankAsync(puuid, ct, force: true);
        var recent = await BuildRecentAsync(puuid, ct);

        return new MeDto
        {
            Name = name,
            Tag = tag,
            Title = _content.Title(mine?.PlayerTitleId),
            Level = mine?.AccountLevel ?? 0,
            Card = GameContent.CardWide(mine?.PlayerCardId),
            CardSmall = GameContent.CardSmall(mine?.PlayerCardId),
            Rank = Rank(rank.Tier),
            Rr = rank.Rr,
            Peak = Peak(rank),
            Wr = rank.Wr,
            Games = rank.Games,
            Agent = recent.Agent,
            Recent = recent.Matches,
        };
    }

    private async Task<(List<RecentDto> Matches, AgentDto? Agent)> BuildRecentAsync(string puuid, CancellationToken ct)
    {
        var list = new List<RecentDto>();
        AgentDto? lastAgent = null;
        var history = await _riot.PdGetAsync($"/match-history/v1/history/{puuid}?startIndex=0&endIndex=5", ct);
        foreach (var h in history?["History"]?.AsArray() ?? [])
        {
            var id = h?["MatchID"]?.GetValue<string>();
            if (id == null) continue;
            var details = await _stats.GetMatchDetailsAsync(id, ct);
            if (details == null) continue;

            var me = details["players"]?.AsArray().FirstOrDefault(p => p?["subject"]?.GetValue<string>() == puuid);
            if (me == null) continue;
            var teamId = me["teamId"]?.GetValue<string>();
            var teams = details["teams"]?.AsArray() ?? [];
            var myTeam = teams.FirstOrDefault(t => t?["teamId"]?.GetValue<string>() == teamId);
            var other = teams.FirstOrDefault(t => t?["teamId"]?.GetValue<string>() != teamId);
            var won = myTeam?["won"]?.GetValue<bool>() == true;
            // Deathmatch and similar modes have a "team" per player: no round score, no draws.
            var twoTeams = teams.Count == 2;
            var draw = twoTeams && myTeam != null && other != null && !won && other["won"]?.GetValue<bool>() != true;

            var info = details["matchInfo"];
            var agent = _content.Agent(me["characterId"]?.GetValue<string>());
            var agentDto = agent == null ? null : new AgentDto(agent.Name, agent.Icon);
            lastAgent ??= agentDto;
            var stats = me["stats"];
            var score = twoTeams && myTeam != null && other != null
                ? $"{PlayerStats.Int(myTeam, "roundsWon")}–{PlayerStats.Int(other, "roundsWon")}"
                : $"{PlayerStats.Int(stats, "kills")} {(_contentLang == "ru" ? "уб." : "K")}";

            list.Add(new RecentDto(
                draw ? "D" : won ? "W" : "L",
                agentDto,
                _content.MapByUrl(info?["mapId"]?.GetValue<string>())?.Name ?? "",
                _content.QueueName(info?["queueID"]?.GetValue<string>(), info?["provisioningFlowID"]?.GetValue<string>() == "CustomGame"),
                score,
                $"{PlayerStats.Int(stats, "kills")}/{PlayerStats.Int(stats, "deaths")}/{PlayerStats.Int(stats, "assists")}"));
        }
        return (list, lastAgent);
    }

    // ---------- Match ----------

    private async Task<MatchDto?> BuildPregameAsync(Presence mine, List<Presence> presences, CancellationToken ct)
    {
        var player = await _riot.GlzGetAsync($"/pregame/v1/players/{_riot.Puuid}", ct);
        var matchId = player?["MatchID"]?.GetValue<string>();
        if (matchId == null) return null;
        _matchId = matchId;

        var m = await _riot.GlzGetAsync($"/pregame/v1/matches/{matchId}", ct);
        if (m == null) return null;

        var players = m["AllyTeam"]?["Players"]?.AsArray().Where(p => p != null).Select(p => p!).ToList() ?? [];
        var dtos = await BuildPlayersAsync(players, presences, mine, ct, p =>
            (p["CharacterID"]?.GetValue<string>(), p["CharacterSelectionState"]?.GetValue<string>() == "locked"));

        var map = MapByAnyId(m["MapID"]?.GetValue<string>());
        return new MatchDto
        {
            Id = matchId,
            Map = map,
            Mode = _content.QueueName(m["QueueID"]?.GetValue<string>() ?? mine.QueueId, mine.IsCustomGame),
            State = "PREGAME",
            Allies = dtos,
        };
    }

    private async Task<MatchDto?> BuildCoregameAsync(Presence mine, List<Presence> presences, CancellationToken ct)
    {
        if (_coreMatch == null)
        {
            var player = await _riot.GlzGetAsync($"/core-game/v1/players/{_riot.Puuid}", ct);
            var matchId = player?["MatchID"]?.GetValue<string>();
            if (matchId == null) return null;
            _coreMatch = await _riot.GlzGetAsync($"/core-game/v1/matches/{matchId}", ct);
            if (_coreMatch == null) return null;
            _matchId = matchId;
        }

        var m = _coreMatch;
        var all = m["Players"]?.AsArray().Where(p => p != null).Select(p => p!).ToList() ?? [];
        var myTeam = all.FirstOrDefault(p => p["Subject"]?.GetValue<string>() == _riot.Puuid)?["TeamID"]?.GetValue<string>();
        var queue = m["MatchmakingData"]?["QueueID"]?.GetValue<string>() ?? mine.QueueId;
        var single = (m["ModeID"]?.GetValue<string>() ?? "").Contains("Deathmatch", StringComparison.OrdinalIgnoreCase)
                     || all.Select(p => p["TeamID"]?.GetValue<string>()).Distinct().Count() > 2;

        var dtos = await BuildPlayersAsync(all, presences, mine, ct, p => (p["CharacterID"]?.GetValue<string>(), true));
        var allyIds = all.Where(p => single || p["TeamID"]?.GetValue<string>() == myTeam).Select(p => p["Subject"]?.GetValue<string>()).ToHashSet();

        return new MatchDto
        {
            Id = _matchId ?? "",
            Map = MapByAnyId(m["MapID"]?.GetValue<string>()),
            Mode = _content.QueueName(queue, m["ProvisioningFlow"]?.GetValue<string>() == "CustomGame"),
            State = "INGAME",
            Score = mine.ScoreAlly != null ? new ScoreDto(mine.ScoreAlly ?? 0, mine.ScoreEnemy ?? 0) : null,
            Allies = dtos.Where(d => allyIds.Contains(d.Puuid)).ToList(),
            Enemies = single ? [] : dtos.Where(d => !allyIds.Contains(d.Puuid)).ToList(),
            Single = single,
        };
    }

    private MapDto? MapByAnyId(string? mapId)
    {
        var map = _content.MapByUrl(mapId);
        return map == null ? null : new MapDto(map.Name, map.Splash, map.ListIcon);
    }

    private async Task<List<PlayerDto>> BuildPlayersAsync(List<JsonNode> players, List<Presence> presences, Presence mine,
        CancellationToken ct, Func<JsonNode, (string? Agent, bool Locked)> agentOf)
    {
        var puuids = players.Select(p => p["Subject"]?.GetValue<string>() ?? "").Where(s => s != "").ToList();
        await ResolveNamesAsync(puuids, ct);
        StartEnrichment(puuids);

        // Parties: players sharing a party id, counted only when two or more of them are in this match.
        var partyOf = presences.Where(p => puuids.Contains(p.Puuid) && p.IsValorant && p.PartyId != null)
            .ToDictionary(p => p.Puuid, p => p.PartyId!);
        var partyIndex = partyOf.Values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key)
            .Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        var result = new List<PlayerDto>();
        foreach (var p in players)
        {
            var puuid = p["Subject"]?.GetValue<string>() ?? "";
            var identity = p["PlayerIdentity"];
            var isMe = puuid == _riot.Puuid;
            var sameParty = partyOf.TryGetValue(puuid, out var pid) && pid == mine.PartyId;
            var (agentId, locked) = agentOf(p);
            var agent = _content.Agent(agentId);
            var rank = _stats.CachedRank(puuid);
            var combat = _stats.CachedCombat(puuid);
            var (name, tag) = _names.TryGetValue(puuid, out var n) ? n : ("", "");
            var incognito = identity?["Incognito"]?.GetValue<bool>() == true;
            var hideLevel = identity?["HideAccountLevel"]?.GetValue<bool>() == true;

            result.Add(new PlayerDto
            {
                Puuid = puuid,
                Name = name,
                Tag = tag,
                Hidden = incognito && !isMe && !sameParty,
                IsMe = isMe,
                Agent = agent == null ? null : new AgentDto(agent.Name, agent.Icon),
                Locked = locked,
                Title = _content.Title(identity?["PlayerTitleID"]?.GetValue<string>()),
                Level = PlayerStats.Int(identity, "AccountLevel"),
                LevelHidden = hideLevel && !isMe && !sameParty,
                Rank = rank == null ? null : Rank(rank.Tier),
                Rr = rank?.Rr ?? 0,
                Leaderboard = rank?.Leaderboard ?? 0,
                Peak = rank == null ? null : Peak(rank),
                Prev = rank == null ? null : Rank(rank.PrevTier),
                Wr = rank?.Wr,
                Games = rank?.Games ?? 0,
                Kd = combat?.Kd,
                Hs = combat?.Hs,
                Party = partyOf.TryGetValue(puuid, out var party) && partyIndex.TryGetValue(party, out var idx) ? idx : null,
                Card = GameContent.CardWide(identity?["PlayerCardID"]?.GetValue<string>()),
            });
        }
        return result;
    }

    // Loads ranks and then combat stats for the match players in the background, one request at a time,
    // so the table shows up immediately and fills in without hitting rate limits.
    private void StartEnrichment(List<string> puuids)
    {
        if (_enrich is { IsCompleted: false }) return;
        var missingRank = puuids.Where(p => _stats.CachedRank(p) == null).ToList();
        var missingCombat = puuids.Where(p => _stats.CachedCombat(p) == null).ToList();
        if (missingRank.Count == 0 && missingCombat.Count == 0) return;

        var ct = _cts.Token;
        _enrich = Task.Run(async () =>
        {
            try
            {
                foreach (var p in missingRank)
                {
                    await _stats.GetRankAsync(p, ct);
                    Wake();
                }
                foreach (var p in missingCombat)
                {
                    await _stats.GetCombatAsync(p, ct);
                    await Task.Delay(150, ct);
                }
                Wake();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Enrichment stopped: " + ex.Message);
            }
        }, ct);
    }

    private async Task ResolveNamesAsync(List<string> puuids, CancellationToken ct)
    {
        var missing = puuids.Where(p => !_names.ContainsKey(p)).Distinct().ToList();
        if (missing.Count == 0) return;
        var resp = await _riot.PdPutAsync("/name-service/v2/players", missing, ct);
        foreach (var e in resp?.AsArray() ?? [])
        {
            var id = e?["Subject"]?.GetValue<string>();
            if (id != null)
                _names[id] = (e!["GameName"]?.GetValue<string>() ?? "", e["TagLine"]?.GetValue<string>() ?? "");
        }
    }

    // ---------- Friends ----------

    private async Task<List<FriendDto>> BuildFriendsAsync(List<Presence> presences, CancellationToken ct)
    {
        if (DateTime.UtcNow - _friendsAt > FriendsTtl)
        {
            var resp = await _riot.LocalGetAsync("/chat/v4/friends", ct);
            if (resp?["friends"] is JsonArray arr) _friendsList = arr;
            _friendsAt = DateTime.UtcNow;
        }

        var byPuuid = presences.Where(p => p.IsValorant).GroupBy(p => p.Puuid).ToDictionary(g => g.Key, g => g.First());
        var list = new List<FriendDto>();
        foreach (var f in _friendsList)
        {
            var puuid = f?["puuid"]?.GetValue<string>();
            if (puuid == null) continue;
            var name = f!["game_name"]?.GetValue<string>() ?? "";
            var tag = f["game_tag"]?.GetValue<string>() ?? "";
            if (name == "") continue;

            if (byPuuid.TryGetValue(puuid, out var p))
            {
                var status = p.SessionLoopState switch { "INGAME" => "ingame", "PREGAME" => "pregame", _ => "menus" };
                list.Add(new FriendDto(name, tag, status, _content.QueueName(p.QueueId, p.IsCustomGame), Rank(p.CompetitiveTier),
                    GameContent.CardWide(p.PlayerCardId)));
            }
            else
            {
                list.Add(new FriendDto(name, tag, "offline", "", null, null));
            }
        }

        var order = new Dictionary<string, int> { ["ingame"] = 0, ["pregame"] = 1, ["menus"] = 2, ["offline"] = 3 };
        return list.OrderBy(f => order[f.Status]).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Helpers ----------

    private RankDto Rank(int tier)
    {
        var t = _content.Tier(tier);
        return new RankDto(tier, t.Name, t.Icon);
    }

    private PeakDto Peak(RankInfo r)
    {
        var t = _content.Tier(r.PeakTier);
        return new PeakDto(r.PeakTier, t.Name, t.Icon, r.PeakAct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        _riot.Dispose();
        _cts.Dispose();
    }
}
