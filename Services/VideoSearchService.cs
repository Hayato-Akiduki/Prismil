using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZenTube.Models;

namespace ZenTube.Services;

public class VideoSearchService
{
    private readonly HttpClient _http;
    private string _soundCloudClientId = "QhVEWrrJKvW4uSU5u5EoI0FTXYUGzy5k";

    public VideoSearchService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
    }

    // =========================================================================
    // 読み込むサイトの選択（OFFのサイトには通信しない）
    // =========================================================================
    private HashSet<SiteType> _disabledSites = [];

    /// <summary>読み込まないサイトを設定する</summary>
    public void SetDisabledSites(IEnumerable<SiteType> sites) => _disabledSites = [.. sites];

    public bool IsSiteEnabled(SiteType site) => !_disabledSites.Contains(site);

    /// <summary>サイトがONなら取得、OFFなら通信せずに空のリストを返す</summary>
    private Task<List<VideoItem>> IfEnabled(SiteType site, Func<Task<List<VideoItem>>> fetch) =>
        IsSiteEnabled(site) ? fetch() : Task.FromResult(new List<VideoItem>());

    /// <summary>9ソースを並列に横断検索してバランスよく統合したリストを返す</summary>
    public async Task<List<VideoItem>> SearchAllAsync(string query, int limitPerSite = 10)
    {
        var ytTask = IfEnabled(SiteType.YouTube, () => SearchYouTubeAsync(query, limitPerSite));
        var nicoTask = IfEnabled(SiteType.Niconico, () => SearchNiconicoAsync(query, limitPerSite, sortByRecentPopular: false));
        var biliTask = IfEnabled(SiteType.Bilibili, () => SearchBilibiliAsync(query, limitPerSite));
        // 補助的なサイト（生配信・ラジオなど）は主要サイトの半分くらいに抑える
        int minor = Math.Max(6, limitPerSite / 2);
        var twitchTask = IfEnabled(SiteType.Twitch, () => SearchTwitchAsync(query, minor));
        var abemaTask = IfEnabled(SiteType.Abema, () => SearchAbemaAsync(query, 5));
        var scTask = IfEnabled(SiteType.SoundCloud, () => SearchSoundCloudAsync(query, limitPerSite));
        var podcastTask = IfEnabled(SiteType.Podcast, () => SearchPodcastsAsync(query, minor));
        var radioTask = IfEnabled(SiteType.Radio, () => SearchRadioStationsAsync(query, minor));
        var dmTask = IfEnabled(SiteType.Dailymotion, () => SearchDailymotionAsync(query, minor));

        await Task.WhenAll(ytTask, nicoTask, biliTask, twitchTask, abemaTask, scTask, podcastTask, radioTask, dmTask);

        return InterleaveMultiResults(
            ytTask.Result, nicoTask.Result, scTask.Result, biliTask.Result,
            podcastTask.Result, twitchTask.Result, radioTask.Result, dmTask.Result, abemaTask.Result);
    }

    // =========================================================================
    // サイトを指定した検索（ページ送りで「もっと読み込む」に対応）
    // =========================================================================

    /// <summary>1サイト分の検索を1ページずつ読むときの目印。次のページの位置と、取得済みの動画を覚えておく。</summary>
    public sealed class SiteSearchCursor
    {
        public SiteSearchCursor(SiteType site, string query, bool shortsOnly = false)
        {
            Site = site;
            Query = query;
            ShortsOnly = shortsOnly;
        }

        public SiteType Site { get; }
        public string Query { get; }
        public bool ShortsOnly { get; }
        public int PagesLoaded { get; internal set; }
        public bool HasMore { get; internal set; } = true;
        internal string? YouTubeToken { get; set; }
        /// <summary>取得済みの動画（Site:Id）。ページをまたいだ重複を除くのに使う。</summary>
        public HashSet<string> SeenKeys { get; } = [];
    }

    /// <summary>サイト別検索で1回に取る件数の目安</summary>
    private const int SitePageSize = 40;

    /// <summary>指定したサイトだけを検索し、次の1ページ分（新しく見つかった動画だけ）を返す。</summary>
    public async Task<List<VideoItem>> SearchSiteNextPageAsync(SiteSearchCursor cursor)
    {
        var fresh = new List<VideoItem>();
        if (!cursor.HasMore || !IsSiteEnabled(cursor.Site)) { cursor.HasMore = false; return fresh; }

        string q = cursor.Query;
        int page = cursor.PagesLoaded; // 0始まり
        List<VideoItem> raw;
        bool more;

        switch (cursor.Site)
        {
            case SiteType.YouTube:
            {
                // 1回の応答は13〜20件なので、SitePageSize 件たまるまで続きのページを読む
                raw = [];
                string ytQuery = cursor.ShortsOnly ? q + " #shorts" : q;
                string? token = cursor.YouTubeToken;
                int newCount = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (page > 0 && i == 0 && string.IsNullOrEmpty(token)) break;
                    var (items, next) = await SearchYouTubePageAsync(ytQuery, token);
                    raw.AddRange(items);
                    newCount += items.Count(x => !cursor.SeenKeys.Contains(x.UniqueKey));
                    token = next;
                    if (string.IsNullOrEmpty(token) || newCount >= SitePageSize) break;
                }
                cursor.YouTubeToken = token;
                more = !string.IsNullOrEmpty(token);
                break;
            }
            case SiteType.Niconico:
            {
                const int size = 50;
                raw = await SearchNiconicoAsync(q, size, sortByRecentPopular: false, offset: page * size);
                more = raw.Count >= size && (page + 1) * size < 1600;
                break;
            }
            case SiteType.Bilibili:
            {
                const int size = 42;
                string biliQuery = cursor.ShortsOnly ? q + " 竖屏 短视频" : q;
                raw = await SearchBilibiliAsync(biliQuery, size, page + 1);
                more = raw.Count >= size / 2 && page + 1 < 30;
                break;
            }
            case SiteType.SoundCloud:
            {
                const int size = 50;
                raw = await SearchSoundCloudAsync(q, size, offset: page * size);
                more = raw.Count >= size / 2;
                break;
            }
            case SiteType.Dailymotion:
            {
                const int size = 50;
                raw = await SearchDailymotionAsync(q, size, page + 1);
                more = raw.Count >= size;
                break;
            }
            case SiteType.Radio:
            {
                const int size = 50;
                raw = await SearchRadioStationsAsync(q, size, offset: page * size);
                more = raw.Count >= size;
                break;
            }
            case SiteType.Podcast:
            {
                // iTunes 検索はページ送りがないので、取得件数を増やして新しい分だけ使う（最大200件）
                int size = Math.Min(200, 50 * (page + 1));
                raw = await SearchPodcastsAsync(q, size);
                more = raw.Count >= size && size < 200;
                break;
            }
            case SiteType.Twitch:
                raw = await SearchTwitchAsync(q, 40);
                more = false;
                break;
            case SiteType.Abema:
                raw = await SearchAbemaAsync(q, 20);
                more = false;
                break;
            default:
                raw = [];
                more = false;
                break;
        }

        foreach (var item in raw)
        {
            if (cursor.SeenKeys.Add(item.UniqueKey))
                fresh.Add(item);
        }

        cursor.PagesLoaded++;
        // 新しい動画が1件も来なかったら、これ以上は無いとみなす
        cursor.HasMore = more && fresh.Count > 0;
        return fresh;
    }

    /// <summary>
    /// 全サイト検索の「もっと読み込む」。ページ送りできる主要サイトから次の分を集め、交互に並べて返す。
    /// cursors が空なら、すでに表示中の動画（existing）を取得済みとして登録した目印を作る。
    /// </summary>
    public async Task<List<VideoItem>> SearchAllNextPageAsync(
        string query, List<SiteSearchCursor> cursors, IEnumerable<VideoItem> existing, bool shortsOnly = false)
    {
        if (cursors.Count == 0)
        {
            var known = existing.Select(x => x.UniqueKey).ToList();
            foreach (var site in new[] { SiteType.YouTube, SiteType.Niconico, SiteType.SoundCloud,
                                         SiteType.Bilibili, SiteType.Dailymotion })
            {
                if (!IsSiteEnabled(site)) continue;
                var c = new SiteSearchCursor(site, query, shortsOnly);
                foreach (var k in known) c.SeenKeys.Add(k);
                cursors.Add(c);
            }
        }

        var active = cursors.Where(c => c.HasMore).ToList();
        var results = await Task.WhenAll(active.Select(SearchSiteNextPageAsync));
        return InterleaveMultiResults(results);
    }

    /// <summary>🎵 音楽＆ラジオ・Podcast専用モード (Music Mode)：楽曲・MV・ラジオ生放送・Podcastを特化取得</summary>
    public async Task<List<VideoItem>> SearchMusicFeedAsync(string? queryOrStation = null, int limitPerSite = 12)
    {
        string q = string.IsNullOrWhiteSpace(queryOrStation)
            ? "YOASOBI 米津玄師 Ado Official Music Video ボカロ"
            : queryOrStation.Trim();

        // 専用モードキーワード判定（ラジオ局一覧 または Podcast一覧 の専用リクエスト）
        if (q.StartsWith("radio:", StringComparison.OrdinalIgnoreCase))
        {
            string radioTag = q["radio:".Length..].Trim();
            bool jpOnly = radioTag.Equals("JP", StringComparison.OrdinalIgnoreCase);
            return await SearchRadioStationsAsync(jpOnly ? "" : radioTag, limit: 36, japanOnly: jpOnly);
        }
        if (q.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
        {
            string podQuery = q["podcast:".Length..].Trim();
            return await SearchPodcastsAsync(podQuery, limit: 36);
        }

        var scTask = IfEnabled(SiteType.SoundCloud, () => SearchSoundCloudAsync(q, limitPerSite));
        var ytTask = IfEnabled(SiteType.YouTube, () => SearchYouTubeAsync(q + " Official Music Video Audio", limitPerSite));
        var nicoTask = IfEnabled(SiteType.Niconico, () => SearchNiconicoMusicAsync(q, limitPerSite));
        var podcastTask = IfEnabled(SiteType.Podcast, () => SearchPodcastsAsync(q, 8));
        var radioTask = IfEnabled(SiteType.Radio, () => SearchRadioStationsAsync(q, 6));
        var biliTask = IfEnabled(SiteType.Bilibili, () => SearchBilibiliAsync(q + " 音楽 MV", 8));
        var dmTask = IfEnabled(SiteType.Dailymotion, () => SearchDailymotionAsync(q + " music", 6));

        await Task.WhenAll(scTask, ytTask, nicoTask, podcastTask, radioTask, biliTask, dmTask);

        // ショート動画（62秒以下）や短すぎる効果音を除外して、楽曲・MV・ラジオ・Podcastだけを厳選
        static List<VideoItem> FilterMusicTracks(List<VideoItem> src) =>
            src.Where(x => !x.IsShortVideo && (x.DurationSeconds == 0 || x.DurationSeconds >= 65)).ToList();

        return InterleaveMultiResults(
            FilterMusicTracks(scTask.Result),
            FilterMusicTracks(ytTask.Result),
            FilterMusicTracks(nicoTask.Result),
            podcastTask.Result,
            radioTask.Result,
            FilterMusicTracks(biliTask.Result),
            FilterMusicTracks(dmTask.Result));
    }

    public async Task<List<VideoItem>> SearchNiconicoMusicAsync(string query, int limit = 14)
    {
        var list = new List<VideoItem>();
        try
        {
            var url = $"https://snapshot.search.nicovideo.jp/api/v2/snapshot/video/contents/search" +
                      $"?q={Uri.EscapeDataString(query)}" +
                      $"&targets=title,tags" +
                      $"&fields=contentId,title,viewCounter,thumbnailUrl,lengthSeconds,startTime,tags" +
                      $"&filters[lengthSeconds][gte]=65" +
                      $"&_sort=-viewCounter" +
                      $"&_limit={limit}" +
                      $"&_context=ZenTube";

            using var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataArr.EnumerateArray())
            {
                string id = item.TryGetProperty("contentId", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                string title = item.TryGetProperty("title", out var tEl) ? WebUtility.HtmlDecode(tEl.GetString() ?? "") : "";
                int lenSec = item.TryGetProperty("lengthSeconds", out var lEl) && lEl.TryGetInt32(out var ls) ? ls : 0;
                long views = item.TryGetProperty("viewCounter", out var vEl) && vEl.TryGetInt64(out var vc) ? vc : 0;
                string thumb = item.TryGetProperty("thumbnailUrl", out var thEl) ? thEl.GetString() ?? "" : "";
                string tags = item.TryGetProperty("tags", out var tgEl) ? tgEl.GetString() ?? "" : "";
                string firstTag = tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "ニコニコ音楽";

                string published = "";
                if (item.TryGetProperty("startTime", out var stEl) && DateTimeOffset.TryParse(stEl.GetString(), out var dto))
                    published = dto.ToString("yyyy/MM/dd");

                list.Add(new VideoItem
                {
                    Id = id,
                    Site = SiteType.Niconico,
                    Title = title,
                    Author = $"#{firstTag}",
                    ThumbnailUrl = NormalizeUrl(thumb),
                    WatchUrl = $"https://www.nicovideo.jp/watch/{id}",
                    DurationSeconds = lenSec,
                    DurationText = VideoItem.FormatDuration(lenSec),
                    ViewCount = views,
                    ViewCountText = VideoItem.FormatViewCount(views),
                    PublishedText = published
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>ショート動画専用：1分以下のショート動画を3サイトから並列取得</summary>
    public async Task<List<VideoItem>> SearchAllShortsAsync(string? query = null, int limitPerSite = 15)
    {
        string baseQuery = string.IsNullOrWhiteSpace(query) ? "人気 話題 音楽 おもしろ" : query.Trim();
        var ytTask = IfEnabled(SiteType.YouTube, () => SearchYouTubeAsync(baseQuery + " #shorts", limitPerSite));
        var nicoTask = IfEnabled(SiteType.Niconico, () => SearchNiconicoShortsAsync(baseQuery, limitPerSite));
        var biliTask = IfEnabled(SiteType.Bilibili, () => SearchBilibiliAsync(baseQuery + " 竖屏 短视频", limitPerSite));

        await Task.WhenAll(ytTask, nicoTask, biliTask);

        foreach (var v in ytTask.Result)
        {
            if (v.DurationSeconds == 0 || v.DurationSeconds > 62)
                v.DurationSeconds = 55;
        }
        var biliShorts = biliTask.Result.Where(x => x.DurationSeconds > 0 && x.DurationSeconds <= 90).ToList();
        foreach (var v in biliShorts)
        {
            if (v.DurationSeconds > 62) v.DurationSeconds = 60;
        }

        return InterleaveMultiResults(ytTask.Result, nicoTask.Result, biliShorts);
    }

    public async Task<List<VideoItem>> SearchNiconicoShortsAsync(string query, int limit = 15)
    {
        var list = new List<VideoItem>();
        try
        {
            var url = $"https://snapshot.search.nicovideo.jp/api/v2/snapshot/video/contents/search" +
                      $"?q={Uri.EscapeDataString(query)}" +
                      $"&targets=title,tags" +
                      $"&fields=contentId,title,viewCounter,thumbnailUrl,lengthSeconds,startTime,tags" +
                      $"&filters[lengthSeconds][lte]=62" +
                      $"&filters[lengthSeconds][gte]=5" +
                      $"&_sort=-viewCounter" +
                      $"&_limit={limit}" +
                      $"&_context=ZenTube";

            using var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataArr.EnumerateArray())
            {
                string id = item.TryGetProperty("contentId", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                string title = item.TryGetProperty("title", out var tEl) ? WebUtility.HtmlDecode(tEl.GetString() ?? "") : "";
                int lenSec = item.TryGetProperty("lengthSeconds", out var lEl) && lEl.TryGetInt32(out var ls) ? ls : 45;
                long views = item.TryGetProperty("viewCounter", out var vEl) && vEl.TryGetInt64(out var vc) ? vc : 0;
                string thumb = item.TryGetProperty("thumbnailUrl", out var thEl) ? thEl.GetString() ?? "" : "";
                string tags = item.TryGetProperty("tags", out var tgEl) ? tgEl.GetString() ?? "" : "";
                string firstTag = tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "ニコニコショート";

                list.Add(new VideoItem
                {
                    Id = id,
                    Site = SiteType.Niconico,
                    Title = title,
                    Author = $"#{firstTag}",
                    ThumbnailUrl = NormalizeUrl(thumb),
                    WatchUrl = $"https://www.nicovideo.jp/watch/{id}",
                    DurationSeconds = lenSec,
                    DurationText = VideoItem.FormatDuration(lenSec),
                    ViewCount = views,
                    ViewCountText = VideoItem.FormatViewCount(views),
                    PublishedText = "ショート"
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>特定の登録チャンネル（投稿者）の動画一覧を取得</summary>
    public async Task<List<VideoItem>> GetChannelVideosAsync(SubscribedChannel channel, int limit = 20)
    {
        var list = channel.Site switch
        {
            SiteType.YouTube => await SearchYouTubeAsync(channel.Name, limit),
            SiteType.Niconico => await SearchNiconicoAsync(channel.Name, limit, sortByRecentPopular: false),
            SiteType.Bilibili => await SearchBilibiliAsync(channel.Name, limit),
            SiteType.Twitch => await SearchTwitchAsync(channel.Name, limit),
            SiteType.Abema => await SearchAbemaAsync(channel.Name, limit),
            SiteType.SoundCloud => await SearchSoundCloudAsync(channel.Name, limit),
            SiteType.Dailymotion => await SearchDailymotionAsync(channel.Name, limit),
            SiteType.Podcast => await SearchPodcastsAsync(channel.Name, limit),
            SiteType.Radio => await SearchRadioStationsAsync(channel.Name, limit),
            _ => []
        };

        var exact = list.Where(x => x.Author.Contains(channel.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var others = list.Where(x => !x.Author.Contains(channel.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return [.. exact, .. others];
    }

    /// <summary>登録チャンネル全体の統合タイムラインを取得</summary>
    public async Task<List<VideoItem>> GetSubscriptionsFeedAsync(IEnumerable<SubscribedChannel> channels, int limitPerChannel = 6)
    {
        var channelList = channels.Where(ch => IsSiteEnabled(ch.Site)).Take(10).ToList();
        if (channelList.Count == 0) return [];

        var tasks = channelList.Select(ch => GetChannelVideosAsync(ch, limitPerChannel)).ToArray();
        var results = await Task.WhenAll(tasks);

        var merged = new List<VideoItem>();
        var seen = new HashSet<string>();
        int maxCount = results.Max(r => r.Count);

        for (int i = 0; i < maxCount; i++)
        {
            foreach (var list in results)
            {
                if (i < list.Count && seen.Add(list[i].UniqueKey))
                {
                    merged.Add(list[i]);
                }
            }
        }

        return merged;
    }

    /// <summary>ホーム画面用：7サイトのトレンド・人気動画・生放送・音楽を並列取得</summary>
    public async Task<List<VideoItem>> GetDiscoverFeedAsync(string? topicKeyword = null, int limitPerSite = 9)
    {
        if (string.IsNullOrWhiteSpace(topicKeyword))
        {
            var ytTask = IfEnabled(SiteType.YouTube, () => SearchYouTubeAsync("話題 人気 MV 公式", limitPerSite));
            var nicoTask = IfEnabled(SiteType.Niconico, () => SearchNiconicoAsync("音楽 OR ゲーム OR アニメ OR ボカロ", limitPerSite, sortByRecentPopular: true));
            var biliTask = IfEnabled(SiteType.Bilibili, () => GetBilibiliPopularAsync(limitPerSite));
            var twitchTask = IfEnabled(SiteType.Twitch, () => GetTwitchPopularStreamsAsync(limitPerSite));
            var abemaTask = IfEnabled(SiteType.Abema, () => GetAbemaLiveChannelsAsync(6));
            var scTask = IfEnabled(SiteType.SoundCloud, () => SearchSoundCloudAsync("J-POP Vocaloid Anime Lofi", 8));
            var dmTask = IfEnabled(SiteType.Dailymotion, () => SearchDailymotionAsync("music anime japan", 6));

            await Task.WhenAll(ytTask, nicoTask, biliTask, twitchTask, abemaTask, scTask, dmTask);
            return InterleaveMultiResults(
                ytTask.Result, nicoTask.Result, scTask.Result, biliTask.Result, twitchTask.Result, abemaTask.Result, dmTask.Result);
        }
        else
        {
            return await SearchAllAsync(topicKeyword, limitPerSite);
        }
    }

    private static List<VideoItem> InterleaveMultiResults(params List<VideoItem>[] lists)
    {
        var result = new List<VideoItem>();
        int max = lists.Length == 0 ? 0 : lists.Max(l => l.Count);
        for (int i = 0; i < max; i++)
        {
            foreach (var list in lists)
            {
                if (i < list.Count)
                    result.Add(list[i]);
            }
        }
        return result;
    }

    // =========================================================================
    // YouTube (InnerTube JSON API)
    // =========================================================================
    /// <summary>
    /// YouTube 検索。1回のAPI応答には動画が13〜20件ほどしか入らないため、
    /// limit に届くまで「続きのページ」（continuation）を最大4回まで読む。
    /// </summary>
    public async Task<List<VideoItem>> SearchYouTubeAsync(string query, int limit = 14)
    {
        var list = new List<VideoItem>();
        var seen = new HashSet<string>();
        string? token = null;
        for (int i = 0; i < 4 && list.Count < limit; i++)
        {
            var (items, next) = await SearchYouTubePageAsync(query, token);
            foreach (var it in items)
            {
                if (list.Count >= limit) break;
                if (seen.Add(it.Id)) list.Add(it);
            }
            if (string.IsNullOrEmpty(next)) break;
            token = next;
        }
        return list;
    }

    /// <summary>YouTube 検索の1ページ分。continuation が null なら最初のページ。</summary>
    private async Task<(List<VideoItem> Items, string? NextToken)> SearchYouTubePageAsync(string query, string? continuation)
    {
        var list = new List<VideoItem>();
        string? nextToken = null;
        try
        {
            var context = new
            {
                client = new
                {
                    clientName = "WEB",
                    clientVersion = "2.20240725.01.00",
                    hl = "ja",
                    gl = "JP"
                }
            };
            object payload = string.IsNullOrEmpty(continuation)
                ? new { context, query }
                : new { context, continuation };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/search?prettyPrint=false")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return (list, null);

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            // 続きのページを読むためのトークン（ページ末尾の continuationItemRenderer の中にある）
            var contRenderers = new List<JsonElement>();
            FindElementsByPropertyName(doc.RootElement, "continuationItemRenderer", contRenderers);
            foreach (var cr in contRenderers)
            {
                if (cr.TryGetProperty("continuationEndpoint", out var ce) &&
                    ce.TryGetProperty("continuationCommand", out var cc) &&
                    cc.TryGetProperty("token", out var tk) &&
                    !string.IsNullOrEmpty(tk.GetString()))
                    nextToken = tk.GetString();
            }

            var renderers = new List<JsonElement>();
            FindElementsByPropertyName(doc.RootElement, "videoRenderer", renderers);

            foreach (var vr in renderers)
            {
                if (!vr.TryGetProperty("videoId", out var idProp)) continue;
                var videoId = idProp.GetString();
                if (string.IsNullOrEmpty(videoId)) continue;

                string title = ExtractYouTubeText(vr, "title");
                if (string.IsNullOrEmpty(title)) continue;

                string author = ExtractYouTubeText(vr, "ownerText");
                if (string.IsNullOrEmpty(author))
                    author = ExtractYouTubeText(vr, "longBylineText");

                string durationText = ExtractYouTubeText(vr, "lengthText");
                int durationSeconds = ParseColonDuration(durationText);

                // YouTube Shorts (reelWatchEndpoint) の判定
                bool isReel = vr.TryGetProperty("navigationEndpoint", out var navEl) &&
                              navEl.ToString().Contains("reelWatchEndpoint", StringComparison.Ordinal);
                if (isReel && durationSeconds == 0)
                    durationSeconds = 59;

                string viewCountRaw = ExtractYouTubeText(vr, "viewCountText");
                long viewCount = ParseNumberFromText(viewCountRaw);
                string viewCountText = viewCount > 0 ? VideoItem.FormatViewCount(viewCount) : viewCountRaw;

                string publishedText = ExtractYouTubeText(vr, "publishedTimeText");

                string thumbUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

                list.Add(new VideoItem
                {
                    Id = videoId,
                    Site = SiteType.YouTube,
                    Title = title,
                    Author = string.IsNullOrEmpty(author) ? "YouTube チャンネル" : author,
                    ThumbnailUrl = thumbUrl,
                    WatchUrl = $"https://www.youtube.com/watch?v={videoId}",
                    DurationText = string.IsNullOrEmpty(durationText) ? "LIVE" : durationText,
                    DurationSeconds = durationSeconds,
                    ViewCount = viewCount,
                    ViewCountText = viewCountText,
                    PublishedText = publishedText
                });
            }
        }
        catch
        {
            // ネットワーク失敗時は空リストを返す
        }
        return (list, nextToken);
    }

    private static string ExtractYouTubeText(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var el)) return "";
        if (el.TryGetProperty("simpleText", out var st)) return st.GetString() ?? "";
        if (el.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var r in runs.EnumerateArray())
            {
                if (r.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
            }
            return sb.ToString();
        }
        return "";
    }

    private static void FindElementsByPropertyName(JsonElement element, string propertyName, List<JsonElement> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == propertyName)
                    results.Add(prop.Value);
                else
                    FindElementsByPropertyName(prop.Value, propertyName, results);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                FindElementsByPropertyName(item, propertyName, results);
        }
    }

    /// <summary>YouTube動画の実際のコメント一覧をInnerTube APIで取得し、弾幕用データ（タイムスタンプ判定付き）に変換する</summary>
    public async Task<List<DanmakuComment>> GetYouTubeDanmakuCommentsAsync(string videoId, int maxComments = 80)
    {
        var result = new List<DanmakuComment>();
        if (string.IsNullOrWhiteSpace(videoId) || videoId.StartsWith("http")) return result;

        try
        {
            var clientCtx = new
            {
                client = new
                {
                    clientName = "WEB",
                    clientVersion = "2.20240725.01.00",
                    hl = "ja",
                    gl = "JP"
                }
            };

            // 1. /youtubei/v1/next からコメントセクションの continuation token を取得
            var initPayload = new { context = clientCtx, videoId };
            using var req1 = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/next?prettyPrint=false")
            {
                Content = new StringContent(JsonSerializer.Serialize(initPayload), Encoding.UTF8, "application/json")
            };
            using var res1 = await _http.SendAsync(req1);
            if (!res1.IsSuccessStatusCode) return result;

            var json1 = await res1.Content.ReadAsStringAsync();
            var tokenMatches = Regex.Matches(json1, @"""token"":""([^""]{40,})""");

            // 後ろの方のトークンほどエンゲージメントパネル（コメント欄）である確率が高いので逆順も含め探索
            var candidateTokens = tokenMatches.Select(m => m.Groups[1].Value).Distinct().Reverse().Take(5).ToList();

            foreach (var token in candidateTokens)
            {
                var contPayload = new { context = clientCtx, continuation = token };
                using var req2 = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/next?prettyPrint=false")
                {
                    Content = new StringContent(JsonSerializer.Serialize(contPayload), Encoding.UTF8, "application/json")
                };
                using var res2 = await _http.SendAsync(req2);
                if (!res2.IsSuccessStatusCode) continue;

                using var stream2 = await res2.Content.ReadAsStreamAsync();
                using var doc2 = await JsonDocument.ParseAsync(stream2);

                var payloads = new List<JsonElement>();
                FindElementsByPropertyName(doc2.RootElement, "commentEntityPayload", payloads);

                if (payloads.Count == 0) continue;

                foreach (var cp in payloads)
                {
                    if (result.Count >= maxComments) break;
                    if (!cp.TryGetProperty("properties", out var propEl) ||
                        !propEl.TryGetProperty("content", out var contEl) ||
                        !contEl.TryGetProperty("content", out var textEl))
                        continue;

                    string raw = textEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    double timeSec = -1;
                    var tsMatch = Regex.Match(raw, @"\b(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\b");
                    if (tsMatch.Success)
                    {
                        int h = string.IsNullOrEmpty(tsMatch.Groups[1].Value) ? 0 : int.Parse(tsMatch.Groups[1].Value);
                        int m = int.Parse(tsMatch.Groups[2].Value);
                        int s = int.Parse(tsMatch.Groups[3].Value);
                        timeSec = h * 3600 + m * 60 + s;
                        raw = raw.Replace(tsMatch.Value, "").Trim();
                    }

                    // 改行を整理して弾幕として読みやすい長さに整形
                    var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string cleanLine = lines.FirstOrDefault(l => l.Length >= 2) ?? raw.Trim();
                    if (cleanLine.Length > 64)
                        cleanLine = cleanLine[..64] + "…";

                    if (!string.IsNullOrWhiteSpace(cleanLine))
                    {
                        result.Add(new DanmakuComment
                        {
                            VideoKey = $"YouTube:{videoId}",
                            TimeSeconds = timeSec,
                            Text = cleanLine,
                            Color = "#FFFFFF"
                        });
                    }
                }

                if (result.Count > 0) break;
            }
        }
        catch { }

        return result;
    }

    // =========================================================================
    // ニコニコ動画 (Snapshot Search API v2)
    // =========================================================================
    public async Task<List<VideoItem>> SearchNiconicoAsync(string query, int limit = 14, bool sortByRecentPopular = false, int offset = 0)
    {
        var list = new List<VideoItem>();
        try
        {
            var url = $"https://snapshot.search.nicovideo.jp/api/v2/snapshot/video/contents/search" +
                      $"?q={Uri.EscapeDataString(query)}" +
                      $"&targets=title,tags" +
                      $"&fields=contentId,title,viewCounter,thumbnailUrl,lengthSeconds,startTime,tags" +
                      $"&_sort=-viewCounter" +
                      $"&_limit={Math.Clamp(limit, 1, 100)}" +
                      (offset > 0 ? $"&_offset={offset}" : "") +
                      $"&_context=ZenTube";

            if (sortByRecentPopular)
            {
                var recentDate = DateTime.UtcNow.AddMonths(-6).ToString("yyyy-MM-ddTHH:mm:ss+00:00");
                url += $"&filters[startTime][gte]={Uri.EscapeDataString(recentDate)}";
            }

            using var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataArr.EnumerateArray())
            {
                string id = item.TryGetProperty("contentId", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                string title = item.TryGetProperty("title", out var tEl) ? WebUtility.HtmlDecode(tEl.GetString() ?? "") : "";
                int lenSec = item.TryGetProperty("lengthSeconds", out var lEl) && lEl.TryGetInt32(out var ls) ? ls : 0;
                long views = item.TryGetProperty("viewCounter", out var vEl) && vEl.TryGetInt64(out var vc) ? vc : 0;
                string thumb = item.TryGetProperty("thumbnailUrl", out var thEl) ? thEl.GetString() ?? "" : "";
                string tags = item.TryGetProperty("tags", out var tgEl) ? tgEl.GetString() ?? "" : "";
                string firstTag = tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "ニコニコ動画";

                string published = "";
                if (item.TryGetProperty("startTime", out var stEl) && DateTimeOffset.TryParse(stEl.GetString(), out var dto))
                {
                    published = dto.ToString("yyyy/MM/dd");
                }

                list.Add(new VideoItem
                {
                    Id = id,
                    Site = SiteType.Niconico,
                    Title = title,
                    Author = $"#{firstTag}",
                    ThumbnailUrl = NormalizeUrl(thumb),
                    WatchUrl = $"https://www.nicovideo.jp/watch/{id}",
                    DurationSeconds = lenSec,
                    DurationText = VideoItem.FormatDuration(lenSec),
                    ViewCount = views,
                    ViewCountText = VideoItem.FormatViewCount(views),
                    PublishedText = published
                });
            }
        }
        catch
        {
            // 失敗時は空リスト
        }
        return list;
    }

    // =========================================================================
    // bilibili (Web Interface Search & Popular API)
    // =========================================================================
    public async Task<List<VideoItem>> SearchBilibiliAsync(string query, int limit = 14, int page = 1)
    {
        var list = new List<VideoItem>();
        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/search/type?search_type=video&keyword={Uri.EscapeDataString(query)}&page={Math.Max(1, page)}&page_size={Math.Clamp(limit, 1, 50)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", "buvid3=12345678-1234-1234-1234-123456789012infoc");
            req.Headers.Add("Referer", "https://search.bilibili.com/");

            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("result", out var resultArr) ||
                resultArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in resultArr.EnumerateArray())
            {
                if (list.Count >= limit) break;
                string bvid = item.TryGetProperty("bvid", out var bvEl) ? bvEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(bvid)) continue;

                string rawTitle = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                string cleanTitle = StripHtmlTags(rawTitle);

                string author = item.TryGetProperty("author", out var aEl) ? aEl.GetString() ?? "bilibili UP主" : "bilibili UP主";
                string pic = item.TryGetProperty("pic", out var pEl) ? pEl.GetString() ?? "" : "";
                string durationStr = item.TryGetProperty("duration", out var dEl) ? dEl.GetString() ?? "" : "";
                int durationSec = ParseColonDuration(durationStr);

                long views = 0;
                if (item.TryGetProperty("play", out var plEl) && plEl.ValueKind == JsonValueKind.Number)
                    plEl.TryGetInt64(out views);

                string published = "";
                if (item.TryGetProperty("pubdate", out var pubEl) && pubEl.TryGetInt64(out var unixSec) && unixSec > 0)
                {
                    published = DateTimeOffset.FromUnixTimeSeconds(unixSec).ToLocalTime().ToString("yyyy/MM/dd");
                }

                list.Add(new VideoItem
                {
                    Id = bvid,
                    Site = SiteType.Bilibili,
                    Title = cleanTitle,
                    Author = author,
                    ThumbnailUrl = NormalizeUrl(pic),
                    WatchUrl = $"https://www.bilibili.com/video/{bvid}",
                    DurationSeconds = durationSec,
                    DurationText = VideoItem.FormatDuration(durationSec),
                    ViewCount = views,
                    ViewCountText = VideoItem.FormatViewCount(views),
                    PublishedText = published
                });
            }
        }
        catch
        {
            // 失敗時は空リスト
        }
        return list;
    }

    public async Task<List<VideoItem>> GetBilibiliPopularAsync(int limit = 12)
    {
        var list = new List<VideoItem>();
        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/popular?ps={limit}&pn=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", "buvid3=12345678-1234-1234-1234-123456789012infoc");
            req.Headers.Add("Referer", "https://www.bilibili.com/");

            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("list", out var listArr) ||
                listArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in listArr.EnumerateArray())
            {
                if (list.Count >= limit) break;
                string bvid = item.TryGetProperty("bvid", out var bvEl) ? bvEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(bvid)) continue;

                string title = item.TryGetProperty("title", out var tEl) ? WebUtility.HtmlDecode(tEl.GetString() ?? "") : "";
                string pic = item.TryGetProperty("pic", out var pEl) ? pEl.GetString() ?? "" : "";
                int durationSec = item.TryGetProperty("duration", out var dEl) && dEl.TryGetInt32(out var ds) ? ds : 0;

                string author = "bilibili";
                if (item.TryGetProperty("owner", out var ownEl) && ownEl.TryGetProperty("name", out var nEl))
                    author = nEl.GetString() ?? "bilibili";

                long views = 0;
                if (item.TryGetProperty("stat", out var stEl) && stEl.TryGetProperty("view", out var vEl))
                    vEl.TryGetInt64(out views);

                string published = "";
                if (item.TryGetProperty("pubdate", out var pubEl) && pubEl.TryGetInt64(out var unixSec) && unixSec > 0)
                    published = DateTimeOffset.FromUnixTimeSeconds(unixSec).ToLocalTime().ToString("yyyy/MM/dd");

                list.Add(new VideoItem
                {
                    Id = bvid,
                    Site = SiteType.Bilibili,
                    Title = title,
                    Author = author,
                    ThumbnailUrl = NormalizeUrl(pic),
                    WatchUrl = $"https://www.bilibili.com/video/{bvid}",
                    DurationSeconds = durationSec,
                    DurationText = VideoItem.FormatDuration(durationSec),
                    ViewCount = views,
                    ViewCountText = VideoItem.FormatViewCount(views),
                    PublishedText = published
                });
            }
        }
        catch
        {
            // 失敗時は空リスト
        }
        return list;
    }

    // =========================================================================
    // Twitch (Public GraphQL API - Live Streams & VODs)
    // =========================================================================
    public async Task<List<VideoItem>> GetTwitchPopularStreamsAsync(int limit = 10)
    {
        var list = new List<VideoItem>();
        try
        {
            var gql = new
            {
                query = $$"""
                query {
                    streams(first: {{limit}}, options: {broadcasterLanguages: ["ja"]}) {
                        edges {
                            node {
                                id
                                title
                                viewersCount
                                broadcaster { login displayName }
                                game { displayName }
                                previewImageURL(width: 440, height: 248)
                            }
                        }
                    }
                }
                """
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = new StringContent(JsonSerializer.Serialize(gql), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");

            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("streams", out var streamsEl) ||
                !streamsEl.TryGetProperty("edges", out var edgesArr) ||
                edgesArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var edge in edgesArr.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object) continue;
                string title = node.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                long viewers = node.TryGetProperty("viewersCount", out var vEl) && vEl.TryGetInt64(out var vc) ? vc : 0;
                string thumb = node.TryGetProperty("previewImageURL", out var pEl) ? pEl.GetString() ?? "" : "";

                string login = "";
                string displayName = "Twitch Streamer";
                if (node.TryGetProperty("broadcaster", out var bEl) && bEl.ValueKind == JsonValueKind.Object)
                {
                    login = bEl.TryGetProperty("login", out var lEl) ? lEl.GetString() ?? "" : "";
                    displayName = bEl.TryGetProperty("displayName", out var dEl) ? dEl.GetString() ?? login : login;
                }
                if (string.IsNullOrEmpty(login)) continue;

                string gameName = "";
                if (node.TryGetProperty("game", out var gEl) && gEl.ValueKind == JsonValueKind.Object)
                {
                    gameName = gEl.TryGetProperty("displayName", out var gdEl) ? gdEl.GetString() ?? "" : "";
                }

                list.Add(new VideoItem
                {
                    Id = login,
                    Site = SiteType.Twitch,
                    Title = string.IsNullOrEmpty(title) ? $"{displayName} のライブ配信" : title,
                    Author = displayName,
                    ThumbnailUrl = thumb,
                    WatchUrl = $"https://www.twitch.tv/{login}",
                    DurationText = "🔴 LIVE",
                    DurationSeconds = 0,
                    ViewCount = viewers,
                    ViewCountText = $"{viewers:N0}人視聴中",
                    PublishedText = gameName
                });
            }
        }
        catch { }
        return list;
    }

    public async Task<List<VideoItem>> SearchTwitchAsync(string query, int limit = 10)
    {
        var list = new List<VideoItem>();
        try
        {
            string escaped = query.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var gql = new
            {
                query = $$"""
                query {
                    searchFor(userQuery: "{{escaped}}", platform: "web") {
                        channels {
                            edges {
                                item {
                                    ... on User {
                                        login
                                        displayName
                                        profileImageURL(width: 300)
                                        broadcastSettings { title }
                                        stream {
                                            id
                                            viewersCount
                                            previewImageURL(width: 440, height: 248)
                                            game { displayName }
                                        }
                                    }
                                }
                            }
                        }
                        videos {
                            edges {
                                item {
                                    ... on Video {
                                        id
                                        title
                                        lengthSeconds
                                        viewCount
                                        previewThumbnailURL(width: 440, height: 248)
                                        owner { login displayName }
                                    }
                                }
                            }
                        }
                    }
                }
                """
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = new StringContent(JsonSerializer.Serialize(gql), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");

            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("searchFor", out var sfEl))
                return list;

            // 1. ライブ配信中のチャンネルを優先追加
            if (sfEl.TryGetProperty("channels", out var chEl) &&
                chEl.TryGetProperty("edges", out var chEdges) &&
                chEdges.ValueKind == JsonValueKind.Array)
            {
                foreach (var edge in chEdges.EnumerateArray())
                {
                    if (list.Count >= limit) break;
                    if (!edge.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;
                    string login = item.TryGetProperty("login", out var lEl) ? lEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(login)) continue;

                    string displayName = item.TryGetProperty("displayName", out var dEl) ? dEl.GetString() ?? login : login;
                    string avatar = item.TryGetProperty("profileImageURL", out var aEl) ? aEl.GetString() ?? "" : "";
                    string lastTitle = "";
                    if (item.TryGetProperty("broadcastSettings", out var bsEl) && bsEl.ValueKind == JsonValueKind.Object)
                        lastTitle = bsEl.TryGetProperty("title", out var btEl) ? btEl.GetString() ?? "" : "";

                    if (item.TryGetProperty("stream", out var stEl) && stEl.ValueKind == JsonValueKind.Object)
                    {
                        long viewers = stEl.TryGetProperty("viewersCount", out var vEl) && vEl.TryGetInt64(out var vc) ? vc : 0;
                        string thumb = stEl.TryGetProperty("previewImageURL", out var pEl) ? pEl.GetString() ?? avatar : avatar;
                        string game = "";
                        if (stEl.TryGetProperty("game", out var gEl) && gEl.ValueKind == JsonValueKind.Object)
                            game = gEl.TryGetProperty("displayName", out var gdEl) ? gdEl.GetString() ?? "" : "";

                        list.Add(new VideoItem
                        {
                            Id = login,
                            Site = SiteType.Twitch,
                            Title = string.IsNullOrEmpty(lastTitle) ? $"🔴 {displayName} ライブ配信中" : lastTitle,
                            Author = displayName,
                            ThumbnailUrl = thumb,
                            WatchUrl = $"https://www.twitch.tv/{login}",
                            DurationText = "🔴 LIVE",
                            ViewCount = viewers,
                            ViewCountText = $"{viewers:N0}人視聴中",
                            PublishedText = game
                        });
                    }
                }
            }

            // 2. アーカイブ動画 (VOD) を追加
            if (sfEl.TryGetProperty("videos", out var vidEl) &&
                vidEl.TryGetProperty("edges", out var vidEdges) &&
                vidEdges.ValueKind == JsonValueKind.Array)
            {
                foreach (var edge in vidEdges.EnumerateArray())
                {
                    if (list.Count >= limit) break;
                    if (!edge.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;
                    string vidId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(vidId)) continue;

                    string title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                    int lenSec = item.TryGetProperty("lengthSeconds", out var lEl) && lEl.TryGetInt32(out var ls) ? ls : 0;
                    long views = item.TryGetProperty("viewCount", out var vEl) && vEl.TryGetInt64(out var vc) ? vc : 0;
                    string thumb = item.TryGetProperty("previewThumbnailURL", out var pEl) ? pEl.GetString() ?? "" : "";

                    string author = "Twitch";
                    if (item.TryGetProperty("owner", out var ownEl) && ownEl.ValueKind == JsonValueKind.Object)
                        author = ownEl.TryGetProperty("displayName", out var dnEl) ? dnEl.GetString() ?? "Twitch" : "Twitch";

                    list.Add(new VideoItem
                    {
                        Id = vidId,
                        Site = SiteType.Twitch,
                        Title = title,
                        Author = author,
                        ThumbnailUrl = thumb,
                        WatchUrl = $"https://www.twitch.tv/videos/{vidId}",
                        DurationSeconds = lenSec,
                        DurationText = VideoItem.FormatDuration(lenSec),
                        ViewCount = views,
                        ViewCountText = VideoItem.FormatViewCount(views),
                        PublishedText = "アーカイブ"
                    });
                }
            }
        }
        catch { }
        return list;
    }

    // =========================================================================
    // ABEMA (24時間放送中チャンネル API ＆ 番組検索連携)
    // =========================================================================
    public async Task<List<VideoItem>> GetAbemaLiveChannelsAsync(int limit = 10)
    {
        var list = new List<VideoItem>();
        try
        {
            using var res = await _http.GetAsync("https://api.p-c3-e.abema-tv.com/v1/channels");
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("channels", out var chArr) || chArr.ValueKind != JsonValueKind.Array)
                return list;

            const string defaultSplash = "https://image.p-c2-x.abema-tv.com/image/assets/splash.png?height=360&quality=75&version=20210720&width=640";

            foreach (var ch in chArr.EnumerateArray())
            {
                if (list.Count >= limit) break;
                string id = ch.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                string name = ch.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || id == "commercial") continue;

                list.Add(new VideoItem
                {
                    Id = id,
                    Site = SiteType.Abema,
                    Title = $"【生放送】{name}",
                    Author = name,
                    ThumbnailUrl = defaultSplash,
                    WatchUrl = $"https://abema.tv/now-on-air/{id}",
                    DurationText = "📺 放送中",
                    ViewCountText = "ABEMA LIVE",
                    PublishedText = "24h チャンネル"
                });
            }
        }
        catch { }
        return list;
    }

    public async Task<List<VideoItem>> SearchAbemaAsync(string query, int limit = 6)
    {
        var list = new List<VideoItem>();
        const string defaultSplash = "https://image.p-c2-x.abema-tv.com/image/assets/splash.png?height=360&quality=75&version=20210720&width=640";

        // 1. まずABEMA内の番組・見逃し配信を直接開ける検索カードを追加
        list.Add(new VideoItem
        {
            Id = "search:" + query,
            Site = SiteType.Abema,
            Title = $"ABEMAで「{query}」の番組・見逃し配信を開く",
            Author = "ABEMA 番組検索",
            ThumbnailUrl = defaultSplash,
            WatchUrl = $"https://abema.tv/search?q={Uri.EscapeDataString(query)}",
            DurationText = "🔍 番組一覧",
            ViewCountText = "見逃し・アニメ・特番",
            PublishedText = "ABEMA"
        });

        // 2. キーワードに関連するABEMAライブチャンネルがあれば追加
        var allChannels = await GetAbemaLiveChannelsAsync(30);
        foreach (var ch in allChannels)
        {
            if (list.Count >= limit) break;
            if (ch.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ch.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (query.Contains("アニメ") && ch.Id.Contains("anime")) ||
                (query.Contains("ニュース") && ch.Id.Contains("news")) ||
                (query.Contains("ゲーム") && ch.Id.Contains("special")))
            {
                list.Add(ch);
            }
        }

        return list;
    }

    // =========================================================================
    // SoundCloud (API v2 Track Search + 公式ビジュアルプレイヤー連携)
    // =========================================================================
    public async Task<List<VideoItem>> SearchSoundCloudAsync(string query, int limit = 12, int offset = 0)
    {
        var list = new List<VideoItem>();
        try
        {
            string url = $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={_soundCloudClientId}&limit={limit}&offset={offset}";
            using var res = await _http.GetAsync(url);

            if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
            {
                await RefreshSoundCloudClientIdAsync();
                url = $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={_soundCloudClientId}&limit={limit}&offset={offset}";
                using var retryRes = await _http.GetAsync(url);
                if (!retryRes.IsSuccessStatusCode) return list;
                await ParseSoundCloudStreamAsync(retryRes, list, limit);
                return list;
            }

            if (!res.IsSuccessStatusCode) return list;
            await ParseSoundCloudStreamAsync(res, list, limit);
        }
        catch { }
        return list;
    }

    private async Task ParseSoundCloudStreamAsync(HttpResponseMessage res, List<VideoItem> list, int limit)
    {
        using var stream = await res.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("collection", out var colArr) || colArr.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in colArr.EnumerateArray())
        {
            if (list.Count >= limit) break;
            if (!item.TryGetProperty("id", out var idEl)) continue;
            string id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : (idEl.GetString() ?? "");
            if (string.IsNullOrEmpty(id)) continue;

            string title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(title)) continue;

            int durationMs = item.TryGetProperty("duration", out var dEl) && dEl.TryGetInt32(out var dm) ? dm : 0;
            int durationSec = durationMs / 1000;

            long plays = item.TryGetProperty("playback_count", out var pEl) && pEl.TryGetInt64(out var pc) ? pc : 0;
            string thumb = item.TryGetProperty("artwork_url", out var artEl) ? artEl.GetString() ?? "" : "";

            string author = "SoundCloud Artist";
            if (item.TryGetProperty("user", out var uEl) && uEl.ValueKind == JsonValueKind.Object)
            {
                author = uEl.TryGetProperty("username", out var unEl) ? unEl.GetString() ?? author : author;
                if (string.IsNullOrEmpty(thumb) && uEl.TryGetProperty("avatar_url", out var avEl))
                    thumb = avEl.GetString() ?? "";
            }

            if (!string.IsNullOrEmpty(thumb))
                thumb = thumb.Replace("-large.jpg", "-t500x500.jpg");

            string published = "";
            if (item.TryGetProperty("created_at", out var caEl) && DateTimeOffset.TryParse(caEl.GetString(), out var dto))
                published = dto.ToString("yyyy/MM/dd");

            string watchUrl = $"https://w.soundcloud.com/player/?url=https%3A//api.soundcloud.com/tracks/{id}&color=%23ff5500&auto_play=true&hide_related=true&show_comments=false&show_user=true&show_reposts=false&show_teaser=false&visual=true";

            list.Add(new VideoItem
            {
                Id = id,
                Site = SiteType.SoundCloud,
                Title = title,
                Author = author,
                ThumbnailUrl = NormalizeUrl(thumb),
                WatchUrl = watchUrl,
                DurationSeconds = durationSec,
                DurationText = VideoItem.FormatDuration(durationSec),
                ViewCount = plays,
                ViewCountText = VideoItem.FormatViewCount(plays),
                PublishedText = published
            });
        }
    }

    private async Task RefreshSoundCloudClientIdAsync()
    {
        try
        {
            var html = await _http.GetStringAsync("https://soundcloud.com/search/sounds?q=music");
            var m = Regex.Match(html, @"""hydratable"":""apiClient"",""data"":\{""id"":""([^""]+)""");
            if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value))
            {
                _soundCloudClientId = m.Groups[1].Value;
            }
        }
        catch { }
    }

    // =========================================================================
    // Dailymotion (公式 REST JSON API + クリーン埋め込みプレイヤー)
    // =========================================================================
    public async Task<List<VideoItem>> SearchDailymotionAsync(string query, int limit = 10, int page = 1)
    {
        var list = new List<VideoItem>();
        try
        {
            string url = $"https://api.dailymotion.com/videos?search={Uri.EscapeDataString(query)}&fields=id,title,duration,views_total,thumbnail_480_url,owner.screenname,created_time&limit={Math.Clamp(limit, 1, 100)}&page={Math.Max(1, page)}";
            using var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("list", out var listArr) || listArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in listArr.EnumerateArray())
            {
                if (list.Count >= limit) break;
                string id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                string title = item.TryGetProperty("title", out var tEl) ? WebUtility.HtmlDecode(tEl.GetString() ?? "") : "";
                int durationSec = item.TryGetProperty("duration", out var dEl) && dEl.TryGetInt32(out var ds) ? ds : 0;
                long views = item.TryGetProperty("views_total", out var vEl) && vEl.TryGetInt64(out var vt) ? vt : 0;
                string thumb = item.TryGetProperty("thumbnail_480_url", out var thEl) ? thEl.GetString() ?? "" : "";
                string author = item.TryGetProperty("owner.screenname", out var owEl) ? owEl.GetString() ?? "Dailymotion" : "Dailymotion";

                string published = "";
                if (item.TryGetProperty("created_time", out var ctEl) && ctEl.TryGetInt64(out var unixSec) && unixSec > 0)
                    published = DateTimeOffset.FromUnixTimeSeconds(unixSec).ToLocalTime().ToString("yyyy/MM/dd");

                list.Add(new VideoItem
                {
                    Id = id,
                    Site = SiteType.Dailymotion,
                    Title = title,
                    Author = author,
                    ThumbnailUrl = NormalizeUrl(thumb),
                    WatchUrl = $"https://www.dailymotion.com/embed/video/{id}?autoplay=1",
                    DurationSeconds = durationSec,
                    DurationText = VideoItem.FormatDuration(durationSec),
                    ViewCount = views,
                    ViewCountText = VideoItem.FormatViewCount(views),
                    PublishedText = published
                });
            }
        }
        catch { }
        return list;
    }

    // =========================================================================
    // 🎙 Apple Podcasts / iTunes Podcast Episode API (国内・海外ラジオ番組＆Podcast)
    // =========================================================================
    public async Task<List<VideoItem>> SearchPodcastsAsync(string query, int limit = 16)
    {
        var list = new List<VideoItem>();
        try
        {
            string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&country=JP&media=podcast&entity=podcastEpisode&limit={limit}";
            using var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("results", out var resArr) || resArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in resArr.EnumerateArray())
            {
                if (list.Count >= limit) break;
                string epUrl = item.TryGetProperty("episodeUrl", out var euEl) ? euEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(epUrl))
                    epUrl = item.TryGetProperty("previewUrl", out var puEl) ? puEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(epUrl)) continue;

                string id = item.TryGetProperty("trackId", out var idEl) && idEl.TryGetInt64(out var tid)
                    ? tid.ToString()
                    : epUrl;

                string title = item.TryGetProperty("trackName", out var tnEl) ? tnEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(title)) continue;

                string showName = item.TryGetProperty("collectionName", out var cnEl) ? cnEl.GetString() ?? "Podcast番組" : "Podcast番組";
                string thumb = item.TryGetProperty("artworkUrl600", out var art600) ? art600.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(thumb) && item.TryGetProperty("artworkUrl160", out var art160))
                    thumb = art160.GetString() ?? "";

                int durationSec = 0;
                if (item.TryGetProperty("trackTimeMillis", out var tmEl) && tmEl.TryGetInt64(out var ms) && ms > 0)
                    durationSec = (int)(ms / 1000);

                string published = "";
                if (item.TryGetProperty("releaseDate", out var rdEl) && DateTimeOffset.TryParse(rdEl.GetString(), out var dto))
                    published = dto.ToLocalTime().ToString("yyyy/MM/dd");

                string genre = "Podcast";
                if (item.TryGetProperty("genres", out var gArr) && gArr.ValueKind == JsonValueKind.Array)
                {
                    var firstG = gArr.EnumerateArray().FirstOrDefault();
                    if (firstG.ValueKind == JsonValueKind.Object && firstG.TryGetProperty("name", out var gnEl))
                        genre = gnEl.GetString() ?? "Podcast";
                }

                list.Add(new VideoItem
                {
                    Id = id,
                    Site = SiteType.Podcast,
                    Title = title,
                    Author = showName,
                    ThumbnailUrl = NormalizeUrl(thumb),
                    WatchUrl = epUrl,
                    DurationSeconds = durationSec,
                    DurationText = durationSec > 0 ? VideoItem.FormatDuration(durationSec) : "🎙 番組",
                    ViewCount = 1000,
                    ViewCountText = $"🎙 {genre}",
                    PublishedText = published
                });
            }
        }
        catch { }
        return list;
    }

    // =========================================================================
    // 📻 インターネットラジオ (Radio Browser API - 世界＆日本の24時間ライブ音楽・FM局)
    // =========================================================================
    public async Task<List<VideoItem>> SearchRadioStationsAsync(string queryOrTag, int limit = 16, bool japanOnly = false, int offset = 0)
    {
        var list = new List<VideoItem>();
        try
        {
            string url;
            if (japanOnly)
            {
                url = $"https://de1.api.radio-browser.info/json/stations/search?countrycode=JP&hidebroken=true&order=clickcount&reverse=true&limit={limit}";
                if (!string.IsNullOrWhiteSpace(queryOrTag))
                    url += $"&name={Uri.EscapeDataString(queryOrTag)}";
            }
            else if (string.IsNullOrWhiteSpace(queryOrTag))
            {
                url = $"https://de1.api.radio-browser.info/json/stations/search?tag=lofi&hidebroken=true&order=clickcount&reverse=true&limit={limit}";
            }
            else
            {
                url = $"https://de1.api.radio-browser.info/json/stations/search?name={Uri.EscapeDataString(queryOrTag)}&hidebroken=true&order=clickcount&reverse=true&limit={limit}";
            }

            if (offset > 0) url += $"&offset={offset}";

            using var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            const string defaultRadioArt = "https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=600&q=80";

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= limit) break;
                string streamUrl = item.TryGetProperty("url_resolved", out var urEl) ? urEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(streamUrl))
                    streamUrl = item.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(streamUrl)) continue;

                string uuid = item.TryGetProperty("stationuuid", out var idEl) ? idEl.GetString() ?? streamUrl : streamUrl;
                string name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "").Trim() : "";
                if (string.IsNullOrEmpty(name)) continue;

                string favicon = item.TryGetProperty("favicon", out var favEl) ? favEl.GetString() ?? "" : "";
                string country = item.TryGetProperty("countrycode", out var ccEl) ? ccEl.GetString() ?? "" : "";
                string tags = item.TryGetProperty("tags", out var tgEl) ? tgEl.GetString() ?? "" : "";
                int bitrate = item.TryGetProperty("bitrate", out var brEl) && brEl.TryGetInt32(out var br) ? br : 0;
                long votes = item.TryGetProperty("votes", out var vEl) && vEl.TryGetInt64(out var vt) ? vt : 0;

                string firstTags = string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(2));
                string author = string.IsNullOrEmpty(firstTags)
                    ? $"📻 Radio ({country})"
                    : $"📻 {firstTags} ({country})";

                list.Add(new VideoItem
                {
                    Id = uuid,
                    Site = SiteType.Radio,
                    Title = $"📻 {name}",
                    Author = author,
                    ThumbnailUrl = string.IsNullOrWhiteSpace(favicon) ? defaultRadioArt : NormalizeUrl(favicon),
                    WatchUrl = streamUrl,
                    DurationSeconds = 0,
                    DurationText = "📻 ON AIR",
                    ViewCount = votes,
                    ViewCountText = bitrate > 0 ? $"{bitrate}kbps LIVE" : "24h LIVE Radio",
                    PublishedText = $"♥ {votes:N0}"
                });
            }
        }
        catch { }
        return list;
    }

    // =========================================================================
    // URL / 動画ID 直接入力の解析
    // =========================================================================
    public static bool TryParseDirectVideo(string input, out VideoItem item)
    {
        item = null!;
        var text = input.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        // ニコニコ動画ID (sm12345 / so12345 / nm12345)
        if (Regex.IsMatch(text, @"^(sm|so|nm)\d+$", RegexOptions.IgnoreCase))
        {
            item = new VideoItem
            {
                Id = text,
                Site = SiteType.Niconico,
                Title = $"ニコニコ動画 ({text})",
                Author = "ニコニコ動画",
                WatchUrl = $"https://www.nicovideo.jp/watch/{text}"
            };
            return true;
        }

        // bilibili BV番号
        if (Regex.IsMatch(text, @"^BV[0-9A-Za-z]{10}$"))
        {
            item = new VideoItem
            {
                Id = text,
                Site = SiteType.Bilibili,
                Title = $"bilibili ({text})",
                Author = "bilibili",
                WatchUrl = $"https://www.bilibili.com/video/{text}"
            };
            return true;
        }

        // URL判定
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();

            if (host.Contains("youtube.com") || host.Contains("youtu.be"))
            {
                string vid = ExtractYouTubeId(uri);
                item = new VideoItem
                {
                    Id = string.IsNullOrEmpty(vid) ? uri.ToString() : vid,
                    Site = SiteType.YouTube,
                    Title = string.IsNullOrEmpty(vid) ? "YouTube ページ" : $"YouTube ({vid})",
                    Author = "YouTube",
                    ThumbnailUrl = string.IsNullOrEmpty(vid) ? "" : $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg",
                    WatchUrl = uri.ToString()
                };
                return true;
            }
            if (host.Contains("nicovideo.jp") || host.Contains("nico.ms"))
            {
                var match = Regex.Match(uri.AbsolutePath, @"(sm|so|nm)\d+");
                var id = match.Success ? match.Value : uri.ToString();
                item = new VideoItem
                {
                    Id = id,
                    Site = SiteType.Niconico,
                    Title = $"ニコニコ動画 ({id})",
                    Author = "ニコニコ動画",
                    WatchUrl = uri.ToString()
                };
                return true;
            }
            if (host.Contains("bilibili.com") || host.Contains("b23.tv"))
            {
                var match = Regex.Match(uri.AbsolutePath, @"BV[0-9A-Za-z]{10}");
                var id = match.Success ? match.Value : uri.ToString();
                item = new VideoItem
                {
                    Id = id,
                    Site = SiteType.Bilibili,
                    Title = $"bilibili ({id})",
                    Author = "bilibili",
                    WatchUrl = uri.ToString()
                };
                return true;
            }
            if (host.Contains("twitch.tv"))
            {
                var id = uri.AbsolutePath.Trim('/');
                item = new VideoItem
                {
                    Id = string.IsNullOrEmpty(id) ? uri.ToString() : id,
                    Site = SiteType.Twitch,
                    Title = $"Twitch ({id})",
                    Author = id,
                    WatchUrl = uri.ToString(),
                    DurationText = "🔴 Twitch"
                };
                return true;
            }
            if (host.Contains("abema.tv"))
            {
                var id = uri.AbsolutePath.Trim('/');
                item = new VideoItem
                {
                    Id = string.IsNullOrEmpty(id) ? uri.ToString() : id,
                    Site = SiteType.Abema,
                    Title = $"ABEMA ({id})",
                    Author = "ABEMA",
                    WatchUrl = uri.ToString(),
                    DurationText = "📺 ABEMA"
                };
                return true;
            }
            if (host.Contains("soundcloud.com"))
            {
                var id = uri.AbsolutePath.Trim('/');
                string watchUrl = host.Contains("w.soundcloud.com")
                    ? uri.ToString()
                    : $"https://w.soundcloud.com/player/?url={Uri.EscapeDataString(uri.ToString())}&color=%23ff5500&auto_play=true&hide_related=true&show_comments=false&show_user=true&visual=true";
                item = new VideoItem
                {
                    Id = string.IsNullOrEmpty(id) ? uri.ToString() : id,
                    Site = SiteType.SoundCloud,
                    Title = $"SoundCloud ({id})",
                    Author = "SoundCloud",
                    WatchUrl = watchUrl,
                    DurationText = "☁ SoundCloud"
                };
                return true;
            }
            if (host.Contains("dailymotion.com") || host.Contains("dai.ly"))
            {
                var id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? uri.ToString();
                item = new VideoItem
                {
                    Id = id,
                    Site = SiteType.Dailymotion,
                    Title = $"Dailymotion ({id})",
                    Author = "Dailymotion",
                    WatchUrl = $"https://www.dailymotion.com/embed/video/{id}?autoplay=1",
                    DurationText = "🎬 Dailymotion"
                };
                return true;
            }
        }

        return false;
    }

    private static string ExtractYouTubeId(Uri uri)
    {
        if (uri.Host.Contains("youtu.be"))
            return uri.AbsolutePath.Trim('/');

        var m = Regex.Match(uri.Query, @"[?&]v=([^&]+)");
        if (m.Success) return m.Groups[1].Value;

        var shorts = Regex.Match(uri.AbsolutePath, @"/(shorts|live|embed)/([^/?]+)");
        return shorts.Success ? shorts.Groups[2].Value : "";
    }

    private static string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var stripped = Regex.Replace(html, "<.*?>", string.Empty);
        return WebUtility.HtmlDecode(stripped);
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("//")) url = "https:" + url;
        else if (url.StartsWith("http://")) url = "https://" + url[7..];

        if (url.Contains("hdslb.com") && !url.Contains('@') && (url.EndsWith(".jpg") || url.EndsWith(".png")))
            url += "@480w_270h_1c.jpg";

        return url;
    }

    private static int ParseColonDuration(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Trim().Split(':');
        int total = 0;
        foreach (var p in parts)
        {
            if (int.TryParse(p, out int val))
                total = total * 60 + val;
        }
        return total;
    }

    private static long ParseNumberFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var n) ? n : 0;
    }
}
