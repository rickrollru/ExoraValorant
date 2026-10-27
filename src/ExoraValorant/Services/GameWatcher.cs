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
    private static readonly TimeSpan PresenceGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MyPartyTtl = TimeSpan.FromSeconds(30);

    private readonly Func<AppSettings> _settings;
    private readonly RiotClient _riot = new();
    private readonly GameContent _content = new();
    private readonly PlayerStats _stats;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<string, (string Name, string Tag)> _names = new();

    private Task? _loop;
    private Task? _enrich;
    private Task? _friendRanks;
    private readonly ConcurrentDictionary<string, MatchSummary> _recentCache = new();
    private readonly ConcurrentDictionary<string, bool> _statsRequested = new();
    private readonly FriendCards _friendCards = new();
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
    private List<Presence> _lastPresences = [];
    private DateTime _lastPresencesAt;
    private readonly Dictionary<string, (Presence Presence, DateTime At)> _friendSeen = new();
    private string? _myPartyId;
    private HashSet<string> _myParty = [];
    private DateTime _myPartyAt;

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
                var running = RiotClient.IsValorantRunning();
                Publish(new UiState { Connection = running ? "connecting" : "none", ValorantRunning = running });
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
        _lastPresences = [];
        _friendSeen.Clear();
        _myPartyId = null;
        _myParty = [];
        _myPartyAt = default;
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

        // A failed poll returns nothing; reuse the last good list for a while instead of showing everyone offline.
        var presencesNode = await _riot.LocalGetAsync("/chat/v4/presences", ct);
        List<Presence> presences;
        if (presencesNode?["presences"] is JsonArray)
        {
            presences = Presence.Parse(presencesNode);
            _lastPresences = presences;
            _lastPresencesAt = DateTime.UtcNow;
        }
        else
        {
            presences = DateTime.UtcNow - _lastPresencesAt < PresenceGrace ? _lastPresences : [];
        }
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
            _myPartyAt = default;
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
        if (loopState is "PREGAME" or "INGAME" && DateTime.UtcNow - _myPartyAt > MyPartyTtl) await RefreshMyPartyAsync(ct);
        if (!isRange && loopState == "PREGAME") match = await BuildPregameAsync(mine!, presences, ct);
        else if (!isRange && loopState == "INGAME") match = await BuildCoregameAsync(mine!, presences, ct);

        // The agent of the current match. In agent select only once it is locked, so hovering doesn't show up.
        AgentDto? currentAgent = null;
        var meInMatch = match?.Allies.FirstOrDefault(p => p.IsMe);
        if (meInMatch?.Agent != null && (match!.State == "INGAME" || meInMatch.Locked)) currentAgent = meInMatch.Agent;
        if (currentAgent != null) _me = _me with { Agent = currentAgent };
        else if ((loopState == "INGAME" || loopState == "PREGAME") && match == null && !isRange)
            Log.Warn($"In {loopState} but the match could not be loaded; agent unknown");

        var friends = await BuildFriendsAsync(presences, ct);

        StatusDto? status = null;
        if (mine != null && loopState != null)
        {
            var mode = isRange ? (lang == "ru" ? "Стрельбище" : "The Range") : _content.QueueName(mine.QueueId, mine.IsCustomGame);
            // Free-for-all matches report 0-0 in presence; there's no round score to show.
            var score = loopState == "INGAME" && mine.ScoreAlly != null && match?.Single != true
                ? new ScoreDto(mine.ScoreAlly ?? 0, mine.ScoreEnemy ?? 0)
                : null;
            status = new StatusDto(loopState, mode, mine.QueueId ?? "", new PartyDto(mine.PartySize, mine.MaxPartySize), score, _stateSince, currentAgent);
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
            if (!_recentCache.TryGetValue(id, out var summary))
            {
                var details = await _stats.GetMatchDetailsAsync(id, ct);
                if (details == null) continue;
                summary = SummarizeMatch(details, puuid);
                if (summary == null) continue;
                _recentCache[id] = summary;
            }

            var agent = _content.Agent(summary.AgentId);
            var agentDto = agent == null ? null : new AgentDto(agent.Name, agent.Icon);
            lastAgent ??= agentDto;
            list.Add(new RecentDto(summary.Result, agentDto, _content.MapByUrl(summary.MapId)?.Name ?? "",
                _content.QueueName(summary.QueueId, summary.Custom), summary.Score, summary.Kda));
        }
        return (list, lastAgent);
    }

    // The few fields of a finished match that the home page shows; the full match JSON is dropped right away.
    private sealed record MatchSummary(string Result, string? AgentId, string? MapId, string? QueueId, bool Custom, string Score, string Kda);

    private static MatchSummary? SummarizeMatch(JsonNode details, string puuid)
    {
        var me = details["players"]?.AsArray().FirstOrDefault(p => p?["subject"]?.GetValue<string>() == puuid);
        if (me == null) return null;
        var teamId = me["teamId"]?.GetValue<string>();
        var teams = details["teams"]?.AsArray() ?? [];
        var myTeam = teams.FirstOrDefault(t => t?["teamId"]?.GetValue<string>() == teamId);
        var other = teams.FirstOrDefault(t => t?["teamId"]?.GetValue<string>() != teamId);
        var won = myTeam?["won"]?.GetValue<bool>() == true;
        // Deathmatch and similar modes have a "team" per player: no round score, no draws.
        var twoTeams = teams.Count == 2;
        var draw = twoTeams && myTeam != null && other != null && !won && other["won"]?.GetValue<bool>() != true;

        var info = details["matchInfo"];
        var stats = me["stats"];
        // Free-for-all modes: placement by kills instead of a round score.
        var score = twoTeams && myTeam != null && other != null
            ? $"{PlayerStats.Int(myTeam, "roundsWon")}–{PlayerStats.Int(other, "roundsWon")}"
            : $"#{Placement(details, puuid)}";

        return new MatchSummary(
            draw ? "D" : won ? "W" : "L",
            me["characterId"]?.GetValue<string>(),
            info?["mapId"]?.GetValue<string>(),
            info?["queueID"]?.GetValue<string>(),
            info?["provisioningFlowID"]?.GetValue<string>() == "CustomGame",
            score,
            $"{PlayerStats.Int(stats, "kills")}/{PlayerStats.Int(stats, "deaths")}/{PlayerStats.Int(stats, "assists")}");
    }

    private static int Placement(JsonNode details, string puuid)
    {
        var myKills = PlayerStats.Int(details["players"]?.AsArray().FirstOrDefault(p => p?["subject"]?.GetValue<string>() == puuid)?["stats"], "kills");
        var better = details["players"]?.AsArray().Count(p => PlayerStats.Int(p?["stats"], "kills") > myKills) ?? 0;
        return better + 1;
    }

    // ---------- Match ----------

    // Members of our own party from the party service; works even when they are not friends.
    private async Task RefreshMyPartyAsync(CancellationToken ct)
    {
        _myPartyAt = DateTime.UtcNow;
        var player = await _riot.GlzGetAsync($"/parties/v1/players/{_riot.Puuid}", ct);
        var partyId = player?["CurrentPartyID"]?.GetValue<string>();
        if (partyId == null)
        {
            _myPartyId = null;
            _myParty = [];
            return;
        }
        var party = await _riot.GlzGetAsync($"/parties/v1/parties/{partyId}", ct);
        var members = party?["Members"]?.AsArray()
            .Select(m => m?["Subject"]?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToHashSet() ?? [];
        _myPartyId = partyId;
        _myParty = members;
        if (members.Count > 1) Log.Info($"Own party: {members.Count} players");
    }

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
            Score = mine.ScoreAlly != null && !single ? new ScoreDto(mine.ScoreAlly ?? 0, mine.ScoreEnemy ?? 0) : null,
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
        // Chat presence only covers friends, so our own party comes from the party service instead.
        var partyOf = presences.Where(p => puuids.Contains(p.Puuid) && p.IsValorant && p.PartyId != null)
            .GroupBy(p => p.Puuid)
            .ToDictionary(g => g.Key, g => g.First().PartyId!);
        if (_myPartyId != null)
            foreach (var member in _myParty.Where(puuids.Contains))
                partyOf[member] = _myPartyId;
        // Guessed parties (players who shared a party in their last match) only for players without a known one.
        var guessed = new HashSet<string>();
        if (_settings().InferParties)
        {
            var known = partyOf.Values.GroupBy(v => v).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
            var hints = puuids.Where(p => !partyOf.TryGetValue(p, out var k) || !known.Contains(k))
                .Select(p => (Puuid: p, Party: _stats.CachedLastParty(p)))
                .Where(x => !string.IsNullOrEmpty(x.Party))
                .GroupBy(x => x.Party!)
                .Where(g => g.Count() > 1);
            foreach (var g in hints)
            foreach (var x in g)
            {
                partyOf[x.Puuid] = "last:" + g.Key;
                guessed.Add(x.Puuid);
            }
        }

        var partyIndex = partyOf.Values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key)
            .Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        var result = new List<PlayerDto>();
        foreach (var p in players)
        {
            var puuid = p["Subject"]?.GetValue<string>() ?? "";
            var identity = p["PlayerIdentity"];
            var isMe = puuid == _riot.Puuid;
            var sameParty = _myParty.Contains(puuid) || (partyOf.TryGetValue(puuid, out var pid) && pid == mine.PartyId);
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
                PartyGuess = guessed.Contains(puuid),
                Card = GameContent.CardWide(identity?["PlayerCardID"]?.GetValue<string>()),
            });
        }
        return result;
    }

    // Loads ranks of the match players in the background, one small request at a time, so the table shows up
    // immediately and fills in without hitting rate limits. K/D and HS% need a full match download each, so
    // they are loaded only for players whose row is opened (RequestPlayerStats).
    private void StartEnrichment(List<string> puuids)
    {
        if (_enrich is { IsCompleted: false }) return;
        var missingRank = puuids.Where(p => _stats.CachedRank(p) == null).ToList();
        var missingCombat = puuids.Where(p => _statsRequested.ContainsKey(p) && _stats.CachedCombat(p) == null).ToList();
        // Party guesses come last and slowly: one full match download per player.
        var missingParty = _settings().InferParties
            ? puuids.Where(p => p != _riot.Puuid && !_myParty.Contains(p) && _stats.CachedLastParty(p) == null).ToList()
            : [];
        if (missingRank.Count == 0 && missingCombat.Count == 0 && missingParty.Count == 0) return;

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
                foreach (var p in missingParty)
                {
                    await _stats.GetLastPartyAsync(p, ct);
                    Wake();
                    await Task.Delay(400, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Enrichment stopped: " + ex.Message);
            }
        }, ct);
    }

    public void RequestPlayerStats(string puuid)
    {
        if (string.IsNullOrEmpty(puuid) || !_statsRequested.TryAdd(puuid, true)) return;
        if (_stats.CachedCombat(puuid) != null) return;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _stats.GetCombatAsync(puuid, ct);
                Wake();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Player stats failed: " + ex.Message);
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
        // A friend missing from one poll is still shown with their last presence for a short while.
        var now = DateTime.UtcNow;
        foreach (var (puuid, p) in byPuuid) _friendSeen[puuid] = (p, now);
        foreach (var (puuid, seen) in _friendSeen.ToList())
        {
            if (byPuuid.ContainsKey(puuid)) continue;
            if (now - seen.At < PresenceGrace) byPuuid[puuid] = seen.Presence;
            else _friendSeen.Remove(puuid);
        }
        var list = new List<FriendDto>();
        var withoutRank = new List<string>();
        foreach (var f in _friendsList)
        {
            var puuid = f?["puuid"]?.GetValue<string>();
            if (puuid == null) continue;
            var name = f!["game_name"]?.GetValue<string>() ?? "";
            var tag = f["game_tag"]?.GetValue<string>() ?? "";
            if (name == "") continue;

            // Presence only exists while a friend is online; otherwise fall back to the rank loaded from MMR
            // and the player card remembered from the last time we saw them online.
            var online = byPuuid.TryGetValue(puuid, out var p);
            if (online && !string.IsNullOrEmpty(p!.PlayerCardId)) _friendCards.Remember(puuid, p.PlayerCardId);
            var cardId = online && !string.IsNullOrEmpty(p!.PlayerCardId) ? p.PlayerCardId : _friendCards.Get(puuid);

            var tier = online && p!.CompetitiveTier > 2 ? p.CompetitiveTier : _stats.CachedRank(puuid)?.Tier;
            if (tier == null) withoutRank.Add(puuid);

            var status = !online ? "offline" : p!.SessionLoopState switch { "INGAME" => "ingame", "PREGAME" => "pregame", _ => "menus" };
            list.Add(new FriendDto(name, tag, status,
                online ? _content.QueueName(p!.QueueId, p.IsCustomGame) : "",
                tier == null ? null : Rank(tier.Value),
                GameContent.CardWide(cardId),
                GameContent.CardSmall(cardId)));
        }
        _friendCards.Save();
        StartFriendRanks(withoutRank);

        var order = new Dictionary<string, int> { ["ingame"] = 0, ["pregame"] = 1, ["menus"] = 2, ["offline"] = 3 };
        return list.OrderBy(f => order[f.Status]).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Ranks of friends without a presence, loaded slowly in the background after the match players.
    private void StartFriendRanks(List<string> puuids)
    {
        if (puuids.Count == 0 || _friendRanks is { IsCompleted: false } || _enrich is { IsCompleted: false }) return;
        var ct = _cts.Token;
        _friendRanks = Task.Run(async () =>
        {
            try
            {
                foreach (var p in puuids.Take(40))
                {
                    await _stats.GetRankAsync(p, ct);
                    await Task.Delay(300, ct);
                }
                Wake();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Friend ranks stopped: " + ex.Message);
            }
        }, ct);
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
