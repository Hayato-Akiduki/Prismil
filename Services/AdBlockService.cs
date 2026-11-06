namespace ZenTube.Services;

public class AdBlockService
{
    public bool IsEnabled { get; set; } = true;
    public int BlockedCount { get; private set; }

    public event Action<int>? BlockedCountChanged;

    private static readonly string[] BlockedDomains =
    [
        // Google / YouTube Ads
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "adservice.google.com",
        "adservice.google.co.jp",
        "pagead2.googlesyndication.com",
        "tpc.googlesyndication.com",
        "imasdk.googleapis.com",
        "s0.2mdn.net",

        // ニコニコ動画 Ads
        "ads.nicovideo.jp",
        "nvad.nicovideo.jp",
        "nicoad.nicovideo.jp",
        "socdm.com",
        "microad.jp",
        "microad.net",
        "im-apps.net",
        "gssprt.jp",
        "yads.c.yimg.jp",
        "nend.net",
        "adingo.jp",
        "fluct.jp",
        "genieesspv.jp",
        "logly.co.jp",
        "ak.kau.li",

        // bilibili Ads & Trackers
        "cm.bilibili.com",
        "pos.baidu.com",
        "cpro.baidu.com",
        "tanx.com",
        "allyes.com",

        // 共通アドネットワーク・トラッカー
        "criteo.com",
        "criteo.net",
        "outbrain.com",
        "taboola.com",
        "amazon-adsystem.com",
        "rubiconproject.com",
        "pubmatic.com",
        "openx.net",
        "adnxs.com",
        "advertising.com",
        "scorecardresearch.com",
        "quantserve.com",
        "moatads.com",
        "teads.tv"
    ];

    private static readonly string[] BlockedUrlKeywords =
    [
        "youtube.com/api/stats/ads",
        "youtube.com/pagead/",
        "youtube.com/ptracking",
        "youtube.com/get_midroll_info",
        "/ youtubei/v1/player/ad_break",
        "youtubei/v1/player/ad_break",
        "&adformat=",
        "?adformat=",
        "&ad_type=",
        "nicovideo.jp/api/nicoad",
        "api.nicoad.nicovideo.jp",
        "data.bilibili.com/log/web",
        "api.bilibili.com/x/web-interface/wbi/index/top/feed/rcmd?ad_"
    ];

    public bool ShouldBlock(string? requestUri)
    {
        if (!IsEnabled || string.IsNullOrEmpty(requestUri)) return false;

        var lower = requestUri.ToLowerInvariant();

        foreach (var kw in BlockedUrlKeywords)
        {
            if (lower.Contains(kw))
            {
                IncrementCount();
                return true;
            }
        }

        if (Uri.TryCreate(lower, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            foreach (var domain in BlockedDomains)
            {
                if (host == domain || host.EndsWith("." + domain))
                {
                    IncrementCount();
                    return true;
                }
            }
        }

        return false;
    }

    public void IncrementCount(int amount = 1)
    {
        BlockedCount += amount;
        BlockedCountChanged?.Invoke(BlockedCount);
    }
}
