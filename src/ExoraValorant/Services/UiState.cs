namespace Exora.Services;

// Shapes sent to the web UI as JSON (camelCase). Keep in sync with wwwroot/js/pages.js.

public sealed record UiState
{
    public string Connection { get; init; } = "none"; // none | connecting | ok
    public bool ValorantRunning { get; init; }
    public StatusDto? Status { get; init; }
    public MeDto? Me { get; init; }
    public MatchDto? Match { get; init; }
    public List<FriendDto> Friends { get; init; } = [];
    public DiscordDto Discord { get; init; } = new(false);
    public string Version { get; init; } = Core.AppInfo.Version;
}

public sealed record DiscordDto(bool Connected);

public sealed record RankDto(int Tier, string Name, string? Icon);

public sealed record PeakDto(int Tier, string Name, string? Icon, string Act);

public sealed record AgentDto(string Name, string? Icon);

public sealed record MapDto(string Name, string? Splash, string? Icon);

public sealed record ScoreDto(int Ally, int Enemy);

public sealed record PartyDto(int Size, int Max);

public sealed record StatusDto(string State, string Mode, string Queue, PartyDto Party, ScoreDto? Score, long Since);

public sealed record RecentDto(string Result, AgentDto? Agent, string Map, string Mode, string Score, string Kda);

public sealed record MeDto
{
    public string Name { get; init; } = "";
    public string Tag { get; init; } = "";
    public string? Title { get; init; }
    public int Level { get; init; }
    public string? Card { get; init; }
    public string? CardSmall { get; init; }
    public RankDto? Rank { get; init; }
    public int Rr { get; init; }
    public PeakDto? Peak { get; init; }
    public int? Wr { get; init; }
    public int Games { get; init; }
    public AgentDto? Agent { get; init; }
    public List<RecentDto> Recent { get; init; } = [];
}

public sealed record PlayerDto
{
    public string Puuid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Tag { get; init; } = "";
    public bool Hidden { get; init; }
    public bool IsMe { get; init; }
    public AgentDto? Agent { get; init; }
    public bool Locked { get; init; }
    public string? Title { get; init; }
    public int Level { get; init; }
    public bool LevelHidden { get; init; }
    public RankDto? Rank { get; init; }
    public int Rr { get; init; }
    public int Leaderboard { get; init; }
    public PeakDto? Peak { get; init; }
    public RankDto? Prev { get; init; }
    public int? Wr { get; init; }
    public int Games { get; init; }
    public double? Kd { get; init; }
    public int? Hs { get; init; }
    public int? Party { get; init; }
    public string? Card { get; init; }
}

public sealed record MatchDto
{
    public string Id { get; init; } = "";
    public MapDto? Map { get; init; }
    public string Mode { get; init; } = "";
    public string State { get; init; } = "";
    public ScoreDto? Score { get; init; }
    public List<PlayerDto> Allies { get; init; } = [];
    public List<PlayerDto> Enemies { get; init; } = [];
    public bool Single { get; init; }
}

public sealed record FriendDto(string Name, string Tag, string Status, string Mode, RankDto? Rank, string? Card);

public sealed record NewsDto(string Date, string Title, string Text, string Url);
