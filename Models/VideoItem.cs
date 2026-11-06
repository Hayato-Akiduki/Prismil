using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace ZenTube.Models;

public enum SiteType
{
    YouTube,
    Niconico,
    Bilibili,
    Twitch,
    Abema,
    SoundCloud,
    Dailymotion,
    Podcast,
    Radio
}

public class VideoItem : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public SiteType Site { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string WatchUrl { get; set; } = "";
    public string DurationText { get; set; } = "";
    public int DurationSeconds { get; set; }
    public long ViewCount { get; set; }
    public string ViewCountText { get; set; } = "";
    public string PublishedText { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;

    private bool _isFavorite;
    [JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FavoriteIcon));
                OnPropertyChanged(nameof(FavoriteForeground));
            }
        }
    }

    private bool _isWatchLater;
    [JsonIgnore]
    public bool IsWatchLater
    {
        get => _isWatchLater;
        set
        {
            if (_isWatchLater != value)
            {
                _isWatchLater = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WatchLaterIcon));
                OnPropertyChanged(nameof(WatchLaterForeground));
            }
        }
    }

    private bool _isChannelSubscribed;
    [JsonIgnore]
    public bool IsChannelSubscribed
    {
        get => _isChannelSubscribed;
        set
        {
            if (_isChannelSubscribed != value)
            {
                _isChannelSubscribed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubscribeIcon));
                OnPropertyChanged(nameof(SubscribeForeground));
            }
        }
    }

    [JsonIgnore]
    public string UniqueKey => $"{Site}:{Id}";

    [JsonIgnore]
    public bool IsShortVideo =>
        (DurationSeconds > 0 && DurationSeconds <= 62) ||
        Title.Contains("#shorts", StringComparison.OrdinalIgnoreCase) ||
        Title.Contains("#short", StringComparison.OrdinalIgnoreCase) ||
        Title.Contains("ショート動画", StringComparison.OrdinalIgnoreCase) ||
        WatchUrl.Contains("/shorts/", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string ChannelKey => $"{Site}:{Author}";

    [JsonIgnore]
    public string SiteDisplayName => Site switch
    {
        SiteType.YouTube => "YouTube",
        SiteType.Niconico => "ニコニコ動画",
        SiteType.Bilibili => "bilibili",
        SiteType.Twitch => "Twitch",
        SiteType.Abema => "ABEMA",
        SiteType.SoundCloud => "SoundCloud",
        SiteType.Dailymotion => "Dailymotion",
        SiteType.Podcast => "🎙 Podcast",
        SiteType.Radio => "📻 LIVEラジオ",
        _ => Site.ToString()
    };

    [JsonIgnore]
    public Brush SiteBadgeBrush => Site switch
    {
        SiteType.YouTube => new SolidColorBrush(Color.FromRgb(230, 33, 23)),
        SiteType.Niconico => new SolidColorBrush(Color.FromRgb(45, 45, 50)),
        SiteType.Bilibili => new SolidColorBrush(Color.FromRgb(0, 161, 214)),
        SiteType.Twitch => new SolidColorBrush(Color.FromRgb(145, 70, 255)),
        SiteType.Abema => new SolidColorBrush(Color.FromRgb(0, 175, 75)),
        SiteType.SoundCloud => new SolidColorBrush(Color.FromRgb(255, 85, 0)),
        SiteType.Dailymotion => new SolidColorBrush(Color.FromRgb(0, 102, 255)),
        SiteType.Podcast => new SolidColorBrush(Color.FromRgb(139, 92, 246)),
        SiteType.Radio => new SolidColorBrush(Color.FromRgb(14, 165, 233)),
        _ => Brushes.Gray
    };

    [JsonIgnore]
    public Brush SiteAccentBrush => Site switch
    {
        SiteType.YouTube => new SolidColorBrush(Color.FromRgb(255, 50, 70)),
        SiteType.Niconico => new SolidColorBrush(Color.FromRgb(220, 220, 230)),
        SiteType.Bilibili => new SolidColorBrush(Color.FromRgb(0, 185, 240)),
        SiteType.Twitch => new SolidColorBrush(Color.FromRgb(169, 112, 255)),
        SiteType.Abema => new SolidColorBrush(Color.FromRgb(16, 210, 100)),
        SiteType.SoundCloud => new SolidColorBrush(Color.FromRgb(255, 115, 36)),
        SiteType.Dailymotion => new SolidColorBrush(Color.FromRgb(56, 139, 255)),
        SiteType.Podcast => new SolidColorBrush(Color.FromRgb(167, 139, 250)),
        SiteType.Radio => new SolidColorBrush(Color.FromRgb(56, 189, 248)),
        _ => Brushes.Gray
    };

    [JsonIgnore]
    public string FavoriteIcon => IsFavorite ? "♥" : "♡";

    [JsonIgnore]
    public Brush FavoriteForeground => IsFavorite
        ? new SolidColorBrush(Color.FromRgb(255, 75, 114))
        : new SolidColorBrush(Color.FromRgb(170, 170, 180));

    [JsonIgnore]
    public string WatchLaterIcon => IsWatchLater ? "✓ 後で見る" : "+ 後で見る";

    [JsonIgnore]
    public Brush WatchLaterForeground => IsWatchLater
        ? new SolidColorBrush(Color.FromRgb(56, 189, 248))
        : new SolidColorBrush(Color.FromRgb(170, 170, 180));

    [JsonIgnore]
    public string SubscribeIcon => IsChannelSubscribed ? "★ 登録済" : "☆ 登録";

    [JsonIgnore]
    public Brush SubscribeForeground => IsChannelSubscribed
        ? new SolidColorBrush(Color.FromRgb(250, 204, 21))
        : new SolidColorBrush(Color.FromRgb(148, 163, 184));

    [JsonIgnore]
    public string MetaSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ViewCountText)) parts.Add(ViewCountText);
            if (!string.IsNullOrWhiteSpace(PublishedText)) parts.Add(PublishedText);
            return string.Join(" • ", parts);
        }
    }

    public static string FormatViewCount(long count)
    {
        if (count <= 0) return "";
        if (count >= 100_000_000) return $"{count / 100_000_000.0:F1}億回視聴";
        if (count >= 10_000) return $"{count / 10_000.0:F1}万回視聴";
        return $"{count:N0}回視聴";
    }

    public static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0) return "";
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class DanmakuComment
{
    public string VideoKey { get; set; } = "";
    public double TimeSeconds { get; set; } = -1;
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#FFFFFF";
    public bool IsUser { get; set; }
}
