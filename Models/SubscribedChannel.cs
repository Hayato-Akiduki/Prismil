using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace ZenTube.Models;

public class SubscribedChannel : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public SiteType Site { get; set; }
    public string ChannelId { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public DateTime SubscribedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string UniqueKey => $"{Site}:{Name}";

    [JsonIgnore]
    public string SiteBadgeText => Site switch
    {
        SiteType.YouTube => "YT",
        SiteType.Niconico => "ニコ",
        SiteType.Bilibili => "bili",
        SiteType.Twitch => "Twitch",
        SiteType.Abema => "ABEMA",
        SiteType.SoundCloud => "SC",
        SiteType.Dailymotion => "DM",
        SiteType.Podcast => "Podcast",
        SiteType.Radio => "Radio",
        _ => ""
    };

    [JsonIgnore]
    public Brush SiteDotBrush => Site switch
    {
        SiteType.YouTube => new SolidColorBrush(Color.FromRgb(255, 0, 51)),
        SiteType.Niconico => new SolidColorBrush(Color.FromRgb(226, 232, 240)),
        SiteType.Bilibili => new SolidColorBrush(Color.FromRgb(0, 161, 214)),
        SiteType.Twitch => new SolidColorBrush(Color.FromRgb(145, 70, 255)),
        SiteType.Abema => new SolidColorBrush(Color.FromRgb(0, 185, 80)),
        SiteType.SoundCloud => new SolidColorBrush(Color.FromRgb(255, 85, 0)),
        SiteType.Dailymotion => new SolidColorBrush(Color.FromRgb(0, 102, 255)),
        SiteType.Podcast => new SolidColorBrush(Color.FromRgb(139, 92, 246)),
        SiteType.Radio => new SolidColorBrush(Color.FromRgb(14, 165, 233)),
        _ => Brushes.Gray
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
