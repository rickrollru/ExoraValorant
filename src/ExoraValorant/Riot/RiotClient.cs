using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Exora.Core;

namespace Exora.Riot;

public sealed class RiotNotRunningException(string message) : Exception(message);

// Talks to the local Riot Client API and the remote PD/GLZ/shared endpoints.
// Port of src/requestsV.py from VALORANT rank yoinker.
public sealed class RiotClient : IDisposable
{
    // Base64 of {"platformType":"PC","platformOS":"Windows",...}, same value the game sends.
    private const string ClientPlatform =
        "ew0KCSJwbGF0Zm9ybVR5cGUiOiAiUEMiLA0KCSJwbGF0Zm9ybU9TIjogIldpbmRvd3MiLA0KCSJwbGF0Zm9ybU9TVmVyc2lvbiI6ICIxMC4wLjE5MDQyLjEuMjU2LjY0Yml0IiwNCgkicGxhdGZvcm1DaGlwc2V0IjogIlVua25vd24iDQp9";

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string LockfilePath = Path.Combine(LocalAppData, @"Riot Games\Riot Client\Config\lockfile");
    private static readonly string ShooterLogPath = Path.Combine(LocalAppData, @"VALORANT\Saved\Logs\ShooterGame.log");

    private readonly HttpClient _local;
    private readonly HttpClient _remote;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private Lockfile? _lockfile;
    private string? _accessToken;
    private string? _entitlement;
    private string? _clientVersion;

    public string Puuid { get; private set; } = "";
    // GLZ cluster such as "eu-1"; the shard is the PD host part such as "eu".
    public string Region { get; private set; } = "";
    public string Shard { get; private set; } = "";
    public string PdUrl => $"https://pd.{Shard}.a.pvp.net";
    public string GlzUrl => $"https://glz-{Region}.{Shard}.a.pvp.net";
    public string SharedUrl => $"https://shared.{Shard}.a.pvp.net";
    public bool IsConnected => _lockfile != null && _accessToken != null && Shard != "";

    public RiotClient()
    {
        // The local Riot Client API uses a self-signed certificate on 127.0.0.1.
        var localHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        _local = new HttpClient(localHandler) { Timeout = TimeSpan.FromSeconds(5) };
        _remote = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        _remote.DefaultRequestHeaders.UserAgent.ParseAdd("ShooterGame/13 Windows/10.0.19043.1.256.64bit");
    }

    public static bool IsValorantRunning()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("VALORANT-Win64-Shipping");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public static bool IsRiotClientRunning() => File.Exists(LockfilePath);

    public void Disconnect()
    {
        _lockfile = null;
        _accessToken = null;
        _entitlement = null;
        _clientVersion = null;
        Puuid = "";
        Shard = "";
        Region = "";
    }

    // Reads the lockfile, gets entitlement tokens and resolves region/shard.
    public async Task ConnectAsync(CancellationToken ct)
    {
        _lockfile = Lockfile.Read(LockfilePath) ?? throw new RiotNotRunningException("Riot Client lockfile not found");
        _local.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + _lockfile.Password)));

        await RefreshTokensAsync(ct);
        await ResolveRegionAsync(ct);
        _clientVersion = await ResolveClientVersionAsync(ct);
        Log.Info($"Connected: shard={Shard} region={Region} version={_clientVersion}");
    }

    private async Task RefreshTokensAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var ent = await LocalGetAsync("/entitlements/v1/token", ct)
                      ?? throw new RiotNotRunningException("Entitlements are not available yet");
            var access = ent["accessToken"]?.GetValue<string>();
            var token = ent["token"]?.GetValue<string>();
            var subject = ent["subject"]?.GetValue<string>();
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(subject))
                throw new RiotNotRunningException("Entitlements token is not ready yet");
            _accessToken = access;
            _entitlement = token;
            Puuid = subject;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task ResolveRegionAsync(CancellationToken ct)
    {
        // Same approach as vRY: the game log contains the PD and GLZ hosts it talks to.
        foreach (var line in ReadShooterLog())
        {
            var pd = Between(line, "https://pd.", ".a.pvp.net/account-xp/v1/");
            if (pd != null) Shard = pd;
            var glz = Between(line, "https://glz-", ".a.pvp.net");
            if (glz != null)
            {
                var parts = glz.Split('.');
                if (parts.Length == 2)
                {
                    Region = parts[0];
                    if (Shard == "") Shard = parts[1];
                }
            }
            if (Shard != "" && Region != "") break;
        }

        if (Shard == "pbe")
        {
            Shard = "na";
            Region = "na-1";
        }

        if (Shard == "" || Region == "")
        {
            // Fallback when the log is missing: ask the Riot Client for the account region.
            var rl = await LocalGetAsync("/riotclient/region-locale", ct);
            var region = rl?["region"]?.GetValue<string>()?.ToLowerInvariant() ?? "eu";
            Region = region + "-1";
            Shard = region switch { "latam" or "br" or "na" or "pbe" => "na", "kr" => "kr", "ap" => "ap", _ => "eu" };
        }
    }

    private async Task<string> ResolveClientVersionAsync(CancellationToken ct)
    {
        foreach (var line in ReadShooterLog())
        {
            var i = line.IndexOf("CI server version: ", StringComparison.Ordinal);
            if (i >= 0) return line[(i + "CI server version: ".Length)..].Trim();
        }

        try
        {
            var v = await _remote.GetFromJsonAsync<JsonNode>("https://valorant-api.com/v1/version", ct);
            var riotVersion = v?["data"]?["riotClientVersion"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(riotVersion)) return riotVersion;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not get client version from valorant-api: " + ex.Message);
        }
        return "release-00.00-shipping-0-0";
    }

    private static IEnumerable<string> ReadShooterLog()
    {
        if (!File.Exists(ShooterLogPath)) yield break;
        FileStream fs;
        try
        {
            fs = new FileStream(ShooterLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)
        {
            yield break;
        }
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string? Between(string line, string start, string end)
    {
        var i = line.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = line.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? null : line[i..j];
    }

    // ---------- Local API ----------

    // Returns null when the client answers with an error or the connection drops for a moment;
    // throws RiotNotRunningException only once the Riot client is really gone (its lockfile is removed).
    public async Task<JsonNode?> LocalGetAsync(string path, CancellationToken ct)
    {
        if (_lockfile == null) throw new RiotNotRunningException("Not connected");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await _local.GetAsync($"https://127.0.0.1:{_lockfile.Port}{path}", ct);
                if (!resp.IsSuccessStatusCode) return null;
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (node is JsonObject o && o["errorCode"]?.GetValue<string>() == "RPC_ERROR") return null;
                return node;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (!File.Exists(LockfilePath)) throw new RiotNotRunningException("Riot client closed: " + ex.Message);
                if (attempt == 0)
                {
                    await Task.Delay(300, ct);
                    continue;
                }
                Log.Warn($"Local API {path} failed twice: {ex.Message}");
                return null;
            }
        }
    }

    public async Task LocalPostAsync(string path, object? body, CancellationToken ct)
    {
        if (_lockfile == null) throw new RiotNotRunningException("Not connected");
        using var resp = await _local.PostAsJsonAsync($"https://127.0.0.1:{_lockfile.Port}{path}", body ?? new { }, ct);
    }

    // ---------- Remote API ----------

    public Task<JsonNode?> PdGetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, PdUrl + path, null, ct);

    public Task<JsonNode?> PdPutAsync(string path, object body, CancellationToken ct) => SendAsync(HttpMethod.Put, PdUrl + path, body, ct);

    public Task<JsonNode?> GlzGetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, GlzUrl + path, null, ct);

    public Task<JsonNode?> SharedGetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, SharedUrl + path, null, ct);

    // Returns null for 404 / RESOURCE_NOT_FOUND. Refreshes tokens once on auth errors and backs off on 429.
    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct, int attempt = 0)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Headers.Add("X-Riot-Entitlements-JWT", _entitlement);
        req.Headers.Add("X-Riot-ClientPlatform", ClientPlatform);
        req.Headers.Add("X-Riot-ClientVersion", _clientVersion);
        if (body != null) req.Content = JsonContent.Create(body);

        using var resp = await _remote.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.NotFound) return null;

        if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
        {
            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 + attempt * 5);
            Log.Warn($"429 on {Strip(url)}, waiting {wait.TotalSeconds:0}s");
            await Task.Delay(wait, ct);
            return await SendAsync(method, url, body, ct, attempt + 1);
        }

        var badClaims = text.Contains("BAD_CLAIMS", StringComparison.Ordinal) || resp.StatusCode == HttpStatusCode.Unauthorized;
        if (badClaims && attempt < 1)
        {
            Log.Info("Token expired, refreshing");
            await RefreshTokensAsync(ct);
            return await SendAsync(method, url, body, ct, attempt + 1);
        }

        if (!resp.IsSuccessStatusCode)
        {
            if (text.Contains("RESOURCE_NOT_FOUND", StringComparison.Ordinal)) return null;
            Log.Warn($"{method} {Strip(url)} -> {(int)resp.StatusCode}: {Truncate(text, 300)}");
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string Strip(string url) => url.Length > 120 ? url[..120] : url;

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] : s;

    public void Dispose()
    {
        _local.Dispose();
        _remote.Dispose();
        _tokenLock.Dispose();
    }
}

public sealed record Lockfile(string Name, int Pid, int Port, string Password, string Protocol)
{
    public static Lockfile? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var parts = reader.ReadToEnd().Split(':');
            if (parts.Length < 5 || !int.TryParse(parts[2], out var port)) return null;
            int.TryParse(parts[1], out var pid);
            return new Lockfile(parts[0], pid, port, parts[3], parts[4]);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
