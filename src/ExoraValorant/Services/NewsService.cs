using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Exora.Core;

namespace Exora.Services;

// Latest patch notes from playvalorant.com. The site is a Next.js app; the article list lives in the
// __NEXT_DATA__ JSON, so we look for any object that has a title, a publish date and a link.
public static partial class NewsService
{
    private static readonly HttpClient Http = CreateClient();
    private static string CacheFile(string lang) => Path.Combine(AppInfo.CacheDir, $"news_{lang}.json");

    public static async Task<List<NewsDto>> GetAsync(string lang, CancellationToken ct)
    {
        var locale = lang == "ru" ? "ru-ru" : "en-us";
        try
        {
            var html = await Http.GetStringAsync($"https://playvalorant.com/{locale}/news/tags/patch-notes/", ct);
            var news = Parse(html, locale);
            if (news.Count > 0)
            {
                await File.WriteAllTextAsync(CacheFile(lang), JsonSerializer.Serialize(news, AppInfo.Json), ct);
                return news;
            }
            Log.Warn("Patch notes page parsed, but no articles were found");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Failed to load patch notes: " + ex.Message);
        }

        try
        {
            if (File.Exists(CacheFile(lang)))
                return JsonSerializer.Deserialize<List<NewsDto>>(await File.ReadAllTextAsync(CacheFile(lang), ct), AppInfo.Json) ?? [];
        }
        catch
        {
        }
        return [];
    }

    private static List<NewsDto> Parse(string html, string locale)
    {
        var m = NextData().Match(html);
        if (!m.Success) return [];
        var root = JsonNode.Parse(m.Groups[1].Value);
        var found = new Dictionary<string, NewsDto>();
        Walk(root, found, locale);
        return found.Values.OrderByDescending(n => n.Date).Take(8).ToList();
    }

    private static void Walk(JsonNode? node, Dictionary<string, NewsDto> found, string locale)
    {
        switch (node)
        {
            case JsonObject o:
                var title = Str(o["title"]);
                var date = Str(o["publishedAt"]) ?? Str(o["date"]);
                var url = Str(o["action"]?["payload"]?["url"]) ?? Str(o["url"]);
                if (title != null && date != null && url != null && DateTime.TryParse(date, out _))
                {
                    if (url.StartsWith('/')) url = "https://playvalorant.com" + url;
                    var text = Str(o["description"]?["body"]) ?? Str(o["description"]) ?? "";
                    found.TryAdd(url, new NewsDto(date, title, StripHtml(text), url));
                }
                foreach (var (_, v) in o) Walk(v, found, locale);
                break;
            case JsonArray a:
                foreach (var v in a) Walk(v, found, locale);
                break;
        }
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string StripHtml(string s) => WebUtility.HtmlDecode(Tags().Replace(s, "")).Trim();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Exora");
        return c;
    }

    [GeneratedRegex("<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextData();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
