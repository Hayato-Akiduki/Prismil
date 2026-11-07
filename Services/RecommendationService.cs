using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZenTube.Models;

namespace ZenTube.Services;

/// <summary>
/// 「次に再生」のおすすめ用に、動画の関連動画を取得する。
/// 対応：YouTube（視聴ページの関連動画）、ニコニコ動画（視聴ページのおすすめ）
/// </summary>
public class RecommendationService
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, List<VideoItem>> _cache = [];

    public RecommendationService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
    }

    public static bool Supports(VideoItem item) =>
        (item.Site == SiteType.YouTube && Regex.IsMatch(item.Id ?? "", @"^[A-Za-z0-9_-]{11}$")) ||
        (item.Site == SiteType.Niconico && Regex.IsMatch(item.Id ?? "", @"^[a-z]{2}\d+$"));

    /// <summary>関連動画（取得できない時は空）。同じ動画は一度取ったら覚えておく</summary>
    public async Task<List<VideoItem>> GetRelatedAsync(VideoItem item)
    {
        if (!Supports(item)) return [];
        if (_cache.TryGetValue(item.UniqueKey, out var cached)) return cached;

        List<VideoItem> list;
        try
        {
            list = item.Site == SiteType.YouTube
                ? await GetYouTubeRelatedAsync(item.Id)
                : await GetNiconicoRelatedAsync(item.Id);
        }
        catch
        {
            list = [];
        }
        if (list.Count > 0) _cache[item.UniqueKey] = list;
        return list;
    }

    // =========================================================================
    // YouTube：/youtubei/v1/next の secondaryResults（lockupViewModel）
    // =========================================================================
    private async Task<List<VideoItem>> GetYouTubeRelatedAsync(string videoId)
    {
        var body = new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20240725.01.00", hl = "ja", gl = "JP" } },
            videoId
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/next?prettyPrint=false")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return [];
        await using var stream = await res.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var secondary = new List<JsonElement>();
        FindByName(doc.RootElement, "secondaryResults", secondary);
        if (secondary.Count == 0) return [];

        var lockups = new List<JsonElement>();
        FindByName(secondary[0], "lockupViewModel", lockups);

        var list = new List<VideoItem>();
        foreach (var l in lockups)
        {
            if (!l.TryGetProperty("contentType", out var ct) || ct.GetString() != "LOCKUP_CONTENT_TYPE_VIDEO") continue;
            var id = l.TryGetProperty("contentId", out var idEl) ? idEl.GetString() ?? "" : "";
            if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$") || id == videoId) continue;

            string title = "", author = "", views = "", published = "", duration = "";
            if (l.TryGetProperty("metadata", out var md) && md.TryGetProperty("lockupMetadataViewModel", out var lm))
            {
                if (lm.TryGetProperty("title", out var t) && t.TryGetProperty("content", out var tc)) title = tc.GetString() ?? "";

                // 1行目：チャンネル名、2行目：「再生回数 | 投稿時期」
                var rows = new List<JsonElement>();
                FindByName(lm, "metadataRows", rows);
                if (rows.Count > 0 && rows[0].ValueKind == JsonValueKind.Array)
                {
                    var texts = rows[0].EnumerateArray()
                        .Select(r => r.TryGetProperty("metadataParts", out var parts) && parts.ValueKind == JsonValueKind.Array
                            ? parts.EnumerateArray()
                                .Select(p => p.TryGetProperty("text", out var pt) && pt.TryGetProperty("content", out var pc) ? pc.GetString() ?? "" : "")
                                .Where(s => s.Length > 0).ToList()
                            : [])
                        .ToList();
                    if (texts.Count > 0 && texts[0].Count > 0) author = texts[0][0];
                    if (texts.Count > 1)
                    {
                        if (texts[1].Count > 0) views = texts[1][0];
                        if (texts[1].Count > 1) published = texts[1][1];
                    }
                }
            }
            if (title.Length == 0) continue;

            if (l.TryGetProperty("contentImage", out var ci))
            {
                var badges = new List<JsonElement>();
                FindByName(ci, "thumbnailBadgeViewModel", badges);
                foreach (var b in badges)
                    if (b.TryGetProperty("text", out var bt) && bt.GetString() is { } s && Regex.IsMatch(s, @"^\d+(:\d{2}){1,2}$")) { duration = s; break; }
            }

            list.Add(new VideoItem
            {
                Id = id,
                Site = SiteType.YouTube,
                Title = title,
                Author = author,
                ThumbnailUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                WatchUrl = $"https://www.youtube.com/watch?v={id}",
                DurationSeconds = ParseDuration(duration),
                DurationText = duration,
                ViewCountText = views.Length > 0 ? $"{views}回視聴" : "",
                PublishedText = published
            });
        }
        return list;
    }

    // =========================================================================
    // ニコニコ動画：nvapi の視聴ページ用おすすめ
    // =========================================================================
    private async Task<List<VideoItem>> GetNiconicoRelatedAsync(string videoId)
    {
        var url = $"https://nvapi.nicovideo.jp/v1/recommend?recipeId=video_watch_recommendation&videoId={videoId}&limit=25&site=nicovideo&_frontendId=6&_frontendVersion=0";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Frontend-Id", "6");
        req.Headers.Add("X-Frontend-Version", "0");
        using var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return [];
        await using var stream = await res.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("items", out var items)) return [];

        var list = new List<VideoItem>();
        foreach (var it in items.EnumerateArray())
        {
            if (!it.TryGetProperty("contentType", out var ct) || ct.GetString() != "video") continue;
            if (!it.TryGetProperty("content", out var c)) continue;
            if (c.TryGetProperty("isPaymentRequired", out var pay) && pay.ValueKind == JsonValueKind.True) continue;

            var id = c.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var title = c.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (id.Length == 0 || title.Length == 0 || id == videoId) continue;

            int dur = c.TryGetProperty("duration", out var d) && d.TryGetInt32(out var ds) ? ds : 0;
            long views = c.TryGetProperty("count", out var cnt) && cnt.TryGetProperty("view", out var v) && v.TryGetInt64(out var vv) ? vv : 0;
            string thumb = "";
            if (c.TryGetProperty("thumbnail", out var th))
            {
                foreach (var key in new[] { "listingUrl", "middleUrl", "url" })
                    if (th.TryGetProperty(key, out var tu) && tu.GetString() is { Length: > 0 } s) { thumb = s; break; }
            }
            string author = c.TryGetProperty("owner", out var ow) && ow.TryGetProperty("name", out var on) ? on.GetString() ?? "" : "";
            string published = c.TryGetProperty("registeredAt", out var ra) && DateTimeOffset.TryParse(ra.GetString(), out var dto)
                ? dto.ToLocalTime().ToString("yyyy/MM/dd") : "";

            list.Add(new VideoItem
            {
                Id = id,
                Site = SiteType.Niconico,
                Title = title,
                Author = author,
                ThumbnailUrl = thumb,
                WatchUrl = $"https://www.nicovideo.jp/watch/{id}",
                DurationSeconds = dur,
                DurationText = VideoItem.FormatDuration(dur),
                ViewCount = views,
                ViewCountText = VideoItem.FormatViewCount(views),
                PublishedText = published
            });
        }
        return list;
    }

    // =========================================================================
    private static int ParseDuration(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int total = 0;
        foreach (var part in s.Split(':'))
        {
            if (!int.TryParse(part, out var n)) return 0;
            total = total * 60 + n;
        }
        return total;
    }

    private static void FindByName(JsonElement el, string name, List<JsonElement> results)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.NameEquals(name)) results.Add(prop.Value);
                else FindByName(prop.Value, name, results);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray()) FindByName(item, name, results);
        }
    }
}
