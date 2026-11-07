using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZenTube.Models;

namespace ZenTube.Services;

/// <summary>コメント一覧に表示する1件分</summary>
public class CommentEntry
{
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>投稿日時・いいね数などの補足（右下に小さく表示）</summary>
    public string Meta { get; set; } = "";
    /// <summary>動画の中の時間（ニコニコのコメント）。ない時は -1</summary>
    public double TimeSeconds { get; set; } = -1;
    public long Likes { get; set; }

    public bool HasTime => TimeSeconds >= 0;
    public string TimeText => HasTime ? VideoItem.FormatDuration((int)TimeSeconds) : "";
    public bool HasAuthor => !string.IsNullOrEmpty(Author);
}

public class CommentPage
{
    public List<CommentEntry> Items { get; } = [];
    /// <summary>続きを読み込む時に使う値。続きがない時は null</summary>
    public string? Next { get; set; }
    /// <summary>取得できなかった時に画面に出す理由</summary>
    public string? Error { get; set; }
}

/// <summary>
/// 動画のコメントを取得して、普通のコメント一覧として表示できる形にする。
/// 対応：YouTube（コメント欄）、ニコニコ動画（動画上のコメント）
/// </summary>
public class CommentService
{
    private readonly HttpClient _http;

    public CommentService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
    }

    public static bool Supports(VideoItem item) =>
        (item.Site == SiteType.YouTube && Regex.IsMatch(item.Id ?? "", @"^[A-Za-z0-9_-]{11}$")) ||
        (item.Site == SiteType.Niconico && Regex.IsMatch(item.Id ?? "", @"^[a-z]{2}\d+$"));

    /// <summary>最初のページ（next が null）または続き（next に前回の値）を取得する</summary>
    public Task<CommentPage> GetAsync(VideoItem item, string? next = null) => item.Site switch
    {
        SiteType.YouTube => GetYouTubeAsync(item.Id, next),
        SiteType.Niconico => GetNiconicoAsync(item.Id),
        _ => Task.FromResult(new CommentPage { Error = "このサイトのコメントは一覧表示に対応していません" })
    };

    // =========================================================================
    // 弾幕用：コメントをまとめて取り、流す時間つきの形にする
    // =========================================================================

    /// <summary>
    /// YouTube のコメントを動画の長さに見合う数だけ（60〜400件）取得して弾幕用にする。
    /// 本文に「1:23」のような時間があるコメントはその時間に、ないものは時間なし（-1）で返す
    /// （時間なしのものは、プレイヤー側で動画全体に均等に割り振って1回ずつ流す）。
    /// </summary>
    public async Task<List<DanmakuComment>> GetYouTubeDanmakuAsync(string videoId, int durationSeconds, Action<int>? progress = null)
    {
        int target = durationSeconds > 0 ? Math.Clamp(durationSeconds / 4, 60, 400) : 150;
        var result = new List<DanmakuComment>();
        var seenText = new HashSet<string>();
        string? next = null;

        for (int pageNo = 0; pageNo < 25 && result.Count < target; pageNo++)
        {
            var page = await GetYouTubeAsync(videoId, next);
            foreach (var c in page.Items)
            {
                var d = ToDanmaku(c.Text, durationSeconds);
                if (d == null || !seenText.Add(d.Text)) continue; // 同じ文面は1回だけ
                d.VideoKey = $"YouTube:{videoId}";
                result.Add(d);
            }
            progress?.Invoke(result.Count);
            if (page.Next == null || page.Items.Count == 0) break;
            next = page.Next;
        }
        return result;
    }

    private static readonly Regex TimeStampRegex = new(@"(?<![\d:])(?:(\d{1,2}):)?(\d{1,2}):(\d{2})(?![\d:])", RegexOptions.Compiled);

    /// <summary>コメント本文を弾幕1件にする（時間の取り出し・1行に整形）。流せない時は null</summary>
    private static DanmakuComment? ToDanmaku(string raw, int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        double time = -1;
        var m = TimeStampRegex.Match(raw);
        if (m.Success)
        {
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            int min = int.Parse(m.Groups[2].Value);
            int s = int.Parse(m.Groups[3].Value);
            if (s < 60 && (h == 0 || min < 60))
            {
                double t = h * 3600 + min * 60 + s;
                // 動画の長さを超える時間（時刻など）は時間として扱わない
                if (durationSeconds <= 0 || t <= durationSeconds)
                {
                    time = t;
                    raw = raw.Remove(m.Index, m.Length);
                }
            }
        }

        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.FirstOrDefault(l => l.Length >= 2) ?? raw.Trim();
        line = Regex.Replace(line, @"\s+", " ").Trim(' ', '　', '-', '~', '〜', ':');
        if (line.Length < 1) return null;
        if (line.Length > 60) line = line[..60] + "…";

        return new DanmakuComment { Text = line, TimeSeconds = time, Color = "#FFFFFF" };
    }

    // =========================================================================
    // YouTube（InnerTube API のコメント欄）
    // =========================================================================
    private static readonly object YouTubeContext = new
    {
        client = new { clientName = "WEB", clientVersion = "2.20240725.01.00", hl = "ja", gl = "JP" }
    };

    private async Task<JsonDocument?> PostYouTubeNextAsync(object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/next?prettyPrint=false")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        await using var stream = await res.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private async Task<CommentPage> GetYouTubeAsync(string videoId, string? next)
    {
        var page = new CommentPage();
        try
        {
            if (next != null)
            {
                using var doc = await PostYouTubeNextAsync(new { context = YouTubeContext, continuation = next });
                if (doc != null) ReadYouTubeComments(doc.RootElement, page);
                return page;
            }

            // 動画ページの情報からコメント欄の入口（continuation）を探す
            using var first = await PostYouTubeNextAsync(new { context = YouTubeContext, videoId });
            if (first == null) { page.Error = "コメントを取得できませんでした"; return page; }

            var token = FindCommentSectionToken(first.RootElement);
            if (token == null)
            {
                page.Error = "この動画はコメントがオフになっているか、取得できませんでした";
                return page;
            }

            using var doc2 = await PostYouTubeNextAsync(new { context = YouTubeContext, continuation = token });
            if (doc2 != null) ReadYouTubeComments(doc2.RootElement, page);
            if (page.Items.Count == 0 && page.Next == null) page.Error = "コメントはまだありません";
        }
        catch
        {
            page.Error = "コメントを取得できませんでした";
        }
        return page;
    }

    /// <summary>コメント欄（itemSectionRenderer の comment-item-section）の最初の continuation を探す</summary>
    private static string? FindCommentSectionToken(JsonElement root)
    {
        var sections = new List<JsonElement>();
        FindByName(root, "itemSectionRenderer", sections);
        foreach (var s in sections)
        {
            if (s.TryGetProperty("sectionIdentifier", out var id) && id.GetString() == "comment-item-section")
            {
                var tok = FindFirstContinuationToken(s);
                if (tok != null) return tok;
            }
        }
        return null;
    }

    private static string? FindFirstContinuationToken(JsonElement el)
    {
        var cmds = new List<JsonElement>();
        FindByName(el, "continuationCommand", cmds);
        foreach (var c in cmds)
            if (c.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 } s) return s;
        return null;
    }

    private static void ReadYouTubeComments(JsonElement root, CommentPage page)
    {
        // 本文などは frameworkUpdates の commentEntityPayload に入っている（返信は除く）
        var payloads = new List<JsonElement>();
        FindByName(root, "commentEntityPayload", payloads);
        foreach (var p in payloads)
        {
            if (!p.TryGetProperty("properties", out var props)) continue;
            if (props.TryGetProperty("replyLevel", out var lvl) && lvl.ValueKind == JsonValueKind.Number && lvl.GetInt32() > 0) continue;
            if (!props.TryGetProperty("content", out var c1) || !c1.TryGetProperty("content", out var c2)) continue;
            var text = (c2.GetString() ?? "").Trim();
            if (text.Length == 0) continue;

            string author = p.TryGetProperty("author", out var a) && a.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
            string published = props.TryGetProperty("publishedTime", out var pt) ? pt.GetString() ?? "" : "";

            string likeText = "";
            string replyText = "";
            if (p.TryGetProperty("toolbar", out var tb))
            {
                likeText = tb.TryGetProperty("likeCountNotliked", out var lk) ? (lk.GetString() ?? "").Trim() : "";
                replyText = tb.TryGetProperty("replyCount", out var rc) ? (rc.GetString() ?? "").Trim() : "";
            }

            var meta = new List<string>();
            if (published.Length > 0) meta.Add(published);
            if (likeText.Length > 0 && likeText != "0") meta.Add($"👍 {likeText}");
            if (replyText.Length > 0 && replyText != "0") meta.Add($"💬 返信 {replyText}");

            page.Items.Add(new CommentEntry { Author = author, Text = text, Meta = string.Join("  ·  ", meta) });
        }

        // 続きのページ：一覧の最後にある continuationItemRenderer
        var lists = new List<JsonElement>();
        FindByName(root, "continuationItems", lists);
        foreach (var list in lists)
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) continue;
            var last = list[list.GetArrayLength() - 1];
            if (last.TryGetProperty("continuationItemRenderer", out var cir))
            {
                var tok = FindFirstContinuationToken(cir);
                if (tok != null) page.Next = tok;
            }
        }
    }

    // =========================================================================
    // ニコニコ動画（視聴ページの nvComment 情報 → コメントサーバー）
    // =========================================================================
    private async Task<CommentPage> GetNiconicoAsync(string videoId)
    {
        var page = new CommentPage();
        try
        {
            var html = await _http.GetStringAsync($"https://www.nicovideo.jp/watch/{videoId}");
            var m = Regex.Match(html, @"<meta name=""server-response"" content=""([^""]+)""");
            if (!m.Success) { page.Error = "コメントを取得できませんでした"; return page; }

            using var resDoc = JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
            var nvList = new List<JsonElement>();
            FindByName(resDoc.RootElement, "nvComment", nvList);
            var nv = nvList.FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("threadKey", out _));
            if (nv.ValueKind != JsonValueKind.Object)
            {
                page.Error = "この動画はログインが必要なため、コメントを取得できませんでした";
                return page;
            }

            var server = nv.GetProperty("server").GetString();
            var body = $"{{\"params\":{nv.GetProperty("params").GetRawText()},\"threadKey\":{nv.GetProperty("threadKey").GetRawText()},\"additionals\":{{}}}}";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/v1/threads")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Frontend-Id", "6");
            req.Headers.Add("X-Frontend-Version", "0");
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) { page.Error = "コメントを取得できませんでした"; return page; }

            await using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("threads", out var threads))
            {
                page.Error = "コメントを取得できませんでした";
                return page;
            }

            var seen = new HashSet<string>();
            foreach (var th in threads.EnumerateArray())
            {
                bool isOwner = th.TryGetProperty("fork", out var f) && f.GetString() == "owner";
                if (!th.TryGetProperty("comments", out var comments)) continue;
                foreach (var c in comments.EnumerateArray())
                {
                    var id = c.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (id.Length > 0 && !seen.Add(id)) continue;
                    var text = c.TryGetProperty("body", out var b) ? (b.GetString() ?? "").Trim() : "";
                    if (text.Length == 0) continue;

                    double vpos = c.TryGetProperty("vposMs", out var v) && v.TryGetInt64(out var ms) ? ms / 1000.0 : -1;
                    long nicoru = c.TryGetProperty("nicoruCount", out var n) && n.TryGetInt64(out var nc) ? nc : 0;
                    string posted = c.TryGetProperty("postedAt", out var pa) && DateTimeOffset.TryParse(pa.GetString(), out var dto)
                        ? dto.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "";

                    var meta = new List<string>();
                    if (posted.Length > 0) meta.Add(posted);
                    if (nicoru > 0) meta.Add($"ニコる {nicoru}");

                    page.Items.Add(new CommentEntry
                    {
                        Author = isOwner ? "投稿者コメント" : "",
                        Text = text,
                        TimeSeconds = vpos,
                        Likes = nicoru,
                        Meta = string.Join("  ·  ", meta)
                    });
                }
            }

            page.Items.Sort((x, y) => x.TimeSeconds.CompareTo(y.TimeSeconds));
            if (page.Items.Count == 0) page.Error = "コメントはまだありません";
        }
        catch
        {
            page.Error = "コメントを取得できませんでした";
        }
        return page;
    }

    // =========================================================================
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
