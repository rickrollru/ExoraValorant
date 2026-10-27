using DiscordRPC;
using DiscordRPC.Message;
using Exora.Core;
using Exora.Riot;
using Exora.Services;

namespace Exora.Discord;

// Discord Rich Presence. Text and images follow rpcLines()/discordCard() in wwwroot/js/pages.js,
// so the in-app preview matches what Discord shows.
public sealed class DiscordPresence : IDisposable
{
    private DiscordRpcClient? _client;
    private string _clientId = "";
    private bool _connected;

    public event Action<bool>? ConnectionChanged;

    public void Update(UiState state, AppSettings settings, GameContent content)
    {
        var rpc = settings.Rpc;
        if (!rpc.Enabled || string.IsNullOrWhiteSpace(rpc.ClientId) || state.Status == null)
        {
            if (!rpc.Enabled || string.IsNullOrWhiteSpace(rpc.ClientId)) Stop();
            else _client?.ClearPresence();
            return;
        }

        EnsureClient(rpc.ClientId.Trim());
        try
        {
            _client!.SetPresence(Build(state, settings, content));
        }
        catch (Exception ex)
        {
            Log.Warn("Discord presence rejected: " + ex.Message);
        }
    }

    private static RichPresence Build(UiState state, AppSettings settings, GameContent content)
    {
        var rpc = settings.Rpc;
        var ru = settings.Lang == "ru";
        var st = state.Status!;
        var me = state.Me;
        var lobby = st.State == "MENUS";
        var images = lobby ? rpc.Lobby : rpc.Game;

        var party = rpc.ShowParty && st.Party.Size > 1
            ? (ru ? $"Пати {st.Party.Size} из {st.Party.Max}" : $"Party {st.Party.Size} of {st.Party.Max}")
            : null;
        var name = rpc.ShowName && me != null && me.Name != "" ? $"{me.Name}#{me.Tag}" : null;

        string details;
        string? stateLine;
        if (lobby)
        {
            details = $"{(ru ? "Лобби" : "Lobby")} — {st.Mode}";
            stateLine = Join(name, party);
        }
        else
        {
            var score = rpc.ShowScore && st.Score != null ? $"{st.Score.Ally} – {st.Score.Enemy}" : null;
            details = st.State == "PREGAME"
                ? $"{(ru ? "Выбор агента" : "Agent select")} — {st.Mode}"
                : Join(st.Mode, score) ?? st.Mode;
            stateLine = Join(name, me?.Agent?.Name, party);
        }

        var large = Image(images.Large, state, content);
        var small = Image(images.Small, state, content);

        var presence = new RichPresence
        {
            Details = Fit(details),
            State = Fit(stateLine),
            Assets = new Assets
            {
                LargeImageKey = large?.Url,
                LargeImageText = Fit(large?.Text),
                SmallImageKey = small?.Url,
                SmallImageText = Fit(small?.Text),
            },
        };

        if (rpc.ShowTimer && !lobby)
            presence.Timestamps = new Timestamps(DateTimeOffset.FromUnixTimeMilliseconds(st.Since).UtcDateTime);

        if (rpc.Button.Enabled && rpc.Button.Label.Length > 0 && Uri.TryCreate(rpc.Button.Url, UriKind.Absolute, out var url)
            && (url.Scheme == "https" || url.Scheme == "http"))
            presence.Buttons = [new DiscordRPC.Button { Label = Fit(rpc.Button.Label, 32), Url = url.ToString() }];

        return presence;
    }

    private static (string Url, string? Text)? Image(string kind, UiState state, GameContent content)
    {
        var me = state.Me;
        var map = state.Match?.Map;
        (string? Url, string? Text) img = kind switch
        {
            "map" => (map?.Splash, map?.Name),
            "agent" => (me?.Agent?.Icon, me?.Agent?.Name),
            "rank" => (me?.Rank?.Icon, me?.Rank?.Name),
            "card" => (me?.CardSmall, me == null ? null : $"{me.Name}#{me.Tag}"),
            "logo" => (content.ValorantLogo, "VALORANT"),
            _ => (null, null),
        };
        return string.IsNullOrEmpty(img.Url) ? null : (img.Url!, img.Text);
    }

    private static string? Join(params string?[] parts)
    {
        var s = string.Join(" • ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return s == "" ? null : s;
    }

    // Discord limits text fields to 128 bytes and needs at least 2 characters.
    private static string? Fit(string? s, int max = 128)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length < 2) s += " ";
        while (System.Text.Encoding.UTF8.GetByteCount(s) > max) s = s[..^2] + "…";
        return s;
    }

    private void EnsureClient(string clientId)
    {
        if (_client != null && _clientId == clientId) return;
        Stop();
        _clientId = clientId;
        _client = new DiscordRpcClient(clientId) { SkipIdenticalPresence = true };
        _client.OnReady += (_, _) => SetConnected(true);
        _client.OnConnectionEstablished += (_, _) => SetConnected(true);
        _client.OnConnectionFailed += (_, _) => SetConnected(false);
        _client.OnClose += (_, _) => SetConnected(false);
        _client.OnError += (_, e) => Log.Warn($"Discord error {e.Code}: {e.Message}");
        _client.Initialize();
        Log.Info("Discord RPC started");
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke(value);
    }

    public void Stop()
    {
        if (_client == null) return;
        try
        {
            _client.ClearPresence();
            _client.Dispose();
        }
        catch
        {
        }
        _client = null;
        _clientId = "";
        SetConnected(false);
    }

    public void Dispose() => Stop();
}
