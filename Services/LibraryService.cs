using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ZenTube.Models;

namespace ZenTube.Services;

public class LibraryData
{
    public List<VideoItem> Favorites { get; set; } = [];
    public List<VideoItem> WatchLater { get; set; } = [];
    public List<VideoItem> History { get; set; } = [];
    public List<CustomPlaylist> Playlists { get; set; } = [];
    public List<SubscribedChannel> Subscriptions { get; set; } = [];
    public List<string> RecentSearches { get; set; } = [];
    public List<DanmakuComment> UserDanmaku { get; set; } = [];
    /// <summary>読み込まない（OFFにした）サイト。空ならすべて読み込む</summary>
    public List<SiteType> DisabledSites { get; set; } = [];
    /// <summary>左のメニュー（サイドバー）を隠しているか</summary>
    public bool SidebarCollapsed { get; set; }
}

public class LibraryService
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ObservableCollection<VideoItem> Favorites { get; } = [];
    public ObservableCollection<VideoItem> WatchLater { get; } = [];
    public ObservableCollection<VideoItem> History { get; } = [];
    public ObservableCollection<CustomPlaylist> Playlists { get; } = [];
    public ObservableCollection<SubscribedChannel> Subscriptions { get; } = [];
    public ObservableCollection<string> RecentSearches { get; } = [];
    public List<DanmakuComment> UserDanmaku { get; } = [];
    public HashSet<SiteType> DisabledSites { get; } = [];
    public bool SidebarCollapsed { get; set; }

    public string DataFilePath => _filePath;

    public event Action? CountsChanged;

    public LibraryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZenTube");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "library.json");
        Load();

        if (Playlists.Count == 0)
        {
            Playlists.Add(new CustomPlaylist { Name = "🎵 作業用BGM" });
            Playlists.Add(new CustomPlaylist { Name = "✨ 神曲・おすすめ" });
            Save();
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<LibraryData>(json, _jsonOptions);
            if (data == null) return;

            Subscriptions.Clear();
            if (data.Subscriptions != null)
            {
                foreach (var sub in data.Subscriptions)
                    Subscriptions.Add(sub);
            }

            Favorites.Clear();
            foreach (var item in data.Favorites)
            {
                item.IsFavorite = true;
                Favorites.Add(item);
            }

            WatchLater.Clear();
            foreach (var item in data.WatchLater)
            {
                item.IsWatchLater = true;
                WatchLater.Add(item);
            }

            History.Clear();
            foreach (var item in data.History)
            {
                SyncItemFlags(item);
                History.Add(item);
            }

            Playlists.Clear();
            if (data.Playlists != null)
            {
                foreach (var pl in data.Playlists)
                {
                    foreach (var v in pl.Items)
                        SyncItemFlags(v);
                    Playlists.Add(pl);
                }
            }

            RecentSearches.Clear();
            if (data.RecentSearches != null)
            {
                foreach (var kw in data.RecentSearches)
                    RecentSearches.Add(kw);
            }

            UserDanmaku.Clear();
            if (data.UserDanmaku != null)
            {
                UserDanmaku.AddRange(data.UserDanmaku);
            }

            DisabledSites.Clear();
            if (data.DisabledSites != null)
                DisabledSites.UnionWith(data.DisabledSites);
            SidebarCollapsed = data.SidebarCollapsed;

            foreach (var item in Favorites) SyncItemFlags(item);
            foreach (var item in WatchLater) SyncItemFlags(item);
        }
        catch
        {
            // 読み込み失敗時は空の状態で開始
        }
    }

    public void Save()
    {
        try
        {
            var data = new LibraryData
            {
                Favorites = [.. Favorites],
                WatchLater = [.. WatchLater],
                History = [.. History],
                Playlists = [.. Playlists],
                Subscriptions = [.. Subscriptions],
                RecentSearches = [.. RecentSearches],
                UserDanmaku = [.. UserDanmaku],
                DisabledSites = [.. DisabledSites],
                SidebarCollapsed = SidebarCollapsed
            };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // 保存失敗時は無視
        }
    }

    // =========================================================================
    // ローカル・チャンネル登録（フォロー）管理
    // =========================================================================
    public bool ToggleSubscription(VideoItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Author)) return false;

        var key = item.ChannelKey;
        var existing = Subscriptions.FirstOrDefault(x => x.UniqueKey == key);
        if (existing != null)
        {
            Subscriptions.Remove(existing);
            item.IsChannelSubscribed = false;
        }
        else
        {
            Subscriptions.Insert(0, new SubscribedChannel
            {
                Name = item.Author.TrimStart('#'),
                Site = item.Site,
                AvatarUrl = item.ThumbnailUrl,
                SubscribedAt = DateTime.Now
            });
            item.IsChannelSubscribed = true;
        }

        UpdateChannelFlagsAcrossAll(key, item.IsChannelSubscribed);
        Save();
        CountsChanged?.Invoke();
        return item.IsChannelSubscribed;
    }

    public void UnsubscribeChannel(SubscribedChannel channel)
    {
        if (Subscriptions.Remove(channel))
        {
            UpdateChannelFlagsAcrossAll(channel.UniqueKey, false);
            Save();
            CountsChanged?.Invoke();
        }
    }

    private void UpdateChannelFlagsAcrossAll(string channelKey, bool subscribed)
    {
        foreach (var x in Favorites.Where(x => x.ChannelKey == channelKey || $"{x.Site}:{x.Author.TrimStart('#')}" == channelKey))
            x.IsChannelSubscribed = subscribed;
        foreach (var x in WatchLater.Where(x => x.ChannelKey == channelKey || $"{x.Site}:{x.Author.TrimStart('#')}" == channelKey))
            x.IsChannelSubscribed = subscribed;
        foreach (var x in History.Where(x => x.ChannelKey == channelKey || $"{x.Site}:{x.Author.TrimStart('#')}" == channelKey))
            x.IsChannelSubscribed = subscribed;
        foreach (var pl in Playlists)
        {
            foreach (var x in pl.Items.Where(x => x.ChannelKey == channelKey || $"{x.Site}:{x.Author.TrimStart('#')}" == channelKey))
                x.IsChannelSubscribed = subscribed;
        }
    }

    // =========================================================================
    // カスタムプレイリスト管理
    // =========================================================================
    public CustomPlaylist CreatePlaylist(string name)
    {
        var pl = new CustomPlaylist { Name = name.Trim() };
        Playlists.Add(pl);
        Save();
        CountsChanged?.Invoke();
        return pl;
    }

    public void DeletePlaylist(CustomPlaylist playlist)
    {
        if (Playlists.Remove(playlist))
        {
            Save();
            CountsChanged?.Invoke();
        }
    }

    public bool ContainsInPlaylist(CustomPlaylist playlist, VideoItem item)
    {
        return playlist.Items.Any(x => x.UniqueKey == item.UniqueKey);
    }

    public bool ToggleItemInPlaylist(CustomPlaylist playlist, VideoItem item)
    {
        var existing = playlist.Items.FirstOrDefault(x => x.UniqueKey == item.UniqueKey);
        bool added;
        if (existing != null)
        {
            playlist.Items.Remove(existing);
            added = false;
        }
        else
        {
            var copy = CloneItem(item);
            copy.AddedAt = DateTime.Now;
            SyncItemFlags(copy);
            playlist.Items.Insert(0, copy);
            added = true;
        }

        playlist.NotifyCountChanged();
        Save();
        CountsChanged?.Invoke();
        return added;
    }

    public void SyncItemFlags(VideoItem item)
    {
        item.IsFavorite = Favorites.Any(x => x.UniqueKey == item.UniqueKey);
        item.IsWatchLater = WatchLater.Any(x => x.UniqueKey == item.UniqueKey);
        var cleanAuthorKey = $"{item.Site}:{item.Author.TrimStart('#')}";
        item.IsChannelSubscribed = Subscriptions.Any(x => x.UniqueKey == item.ChannelKey || x.UniqueKey == cleanAuthorKey);
    }

    public void SyncCollectionFlags(IEnumerable<VideoItem> items)
    {
        foreach (var item in items)
            SyncItemFlags(item);
    }

    public bool ToggleFavorite(VideoItem item)
    {
        var existing = Favorites.FirstOrDefault(x => x.UniqueKey == item.UniqueKey);
        if (existing != null)
        {
            Favorites.Remove(existing);
            item.IsFavorite = false;
        }
        else
        {
            var copy = CloneItem(item);
            copy.AddedAt = DateTime.Now;
            copy.IsFavorite = true;
            copy.IsWatchLater = item.IsWatchLater;
            copy.IsChannelSubscribed = item.IsChannelSubscribed;
            Favorites.Insert(0, copy);
            item.IsFavorite = true;
        }

        UpdateFlagsAcrossAll(item.UniqueKey);
        Save();
        CountsChanged?.Invoke();
        return item.IsFavorite;
    }

    public bool ToggleWatchLater(VideoItem item)
    {
        var existing = WatchLater.FirstOrDefault(x => x.UniqueKey == item.UniqueKey);
        if (existing != null)
        {
            WatchLater.Remove(existing);
            item.IsWatchLater = false;
        }
        else
        {
            var copy = CloneItem(item);
            copy.AddedAt = DateTime.Now;
            copy.IsWatchLater = true;
            copy.IsFavorite = item.IsFavorite;
            copy.IsChannelSubscribed = item.IsChannelSubscribed;
            WatchLater.Insert(0, copy);
            item.IsWatchLater = true;
        }

        UpdateFlagsAcrossAll(item.UniqueKey);
        Save();
        CountsChanged?.Invoke();
        return item.IsWatchLater;
    }

    public void AddToHistory(VideoItem item)
    {
        var existing = History.FirstOrDefault(x => x.UniqueKey == item.UniqueKey);
        if (existing != null)
            History.Remove(existing);

        var copy = CloneItem(item);
        copy.AddedAt = DateTime.Now;
        SyncItemFlags(copy);
        History.Insert(0, copy);

        while (History.Count > 150)
            History.RemoveAt(History.Count - 1);

        Save();
        CountsChanged?.Invoke();
    }

    public void ClearHistory()
    {
        History.Clear();
        Save();
        CountsChanged?.Invoke();
    }

    public void AddRecentSearch(string query)
    {
        var q = query.Trim();
        if (string.IsNullOrEmpty(q)) return;
        var existing = RecentSearches.FirstOrDefault(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            RecentSearches.Remove(existing);

        RecentSearches.Insert(0, q);
        while (RecentSearches.Count > 10)
            RecentSearches.RemoveAt(RecentSearches.Count - 1);

        Save();
    }

    public void AddUserDanmaku(DanmakuComment comment)
    {
        UserDanmaku.Add(comment);
        while (UserDanmaku.Count > 1000)
            UserDanmaku.RemoveAt(0);
        Save();
    }

    public List<DanmakuComment> GetUserDanmakuForVideo(string videoKey)
    {
        return UserDanmaku.Where(x => x.VideoKey == videoKey).ToList();
    }

    private void UpdateFlagsAcrossAll(string uniqueKey)
    {
        bool isFav = Favorites.Any(x => x.UniqueKey == uniqueKey);
        bool isWl = WatchLater.Any(x => x.UniqueKey == uniqueKey);

        foreach (var x in Favorites.Where(x => x.UniqueKey == uniqueKey))
        {
            x.IsFavorite = isFav;
            x.IsWatchLater = isWl;
        }
        foreach (var x in WatchLater.Where(x => x.UniqueKey == uniqueKey))
        {
            x.IsFavorite = isFav;
            x.IsWatchLater = isWl;
        }
        foreach (var x in History.Where(x => x.UniqueKey == uniqueKey))
        {
            x.IsFavorite = isFav;
            x.IsWatchLater = isWl;
        }
        foreach (var pl in Playlists)
        {
            foreach (var x in pl.Items.Where(x => x.UniqueKey == uniqueKey))
            {
                x.IsFavorite = isFav;
                x.IsWatchLater = isWl;
            }
        }
    }

    private static VideoItem CloneItem(VideoItem src) => new()
    {
        Id = src.Id,
        Site = src.Site,
        Title = src.Title,
        Author = src.Author,
        ThumbnailUrl = src.ThumbnailUrl,
        WatchUrl = src.WatchUrl,
        DurationText = src.DurationText,
        DurationSeconds = src.DurationSeconds,
        ViewCount = src.ViewCount,
        ViewCountText = src.ViewCountText,
        PublishedText = src.PublishedText,
        AddedAt = src.AddedAt,
        IsFavorite = src.IsFavorite,
        IsWatchLater = src.IsWatchLater,
        IsChannelSubscribed = src.IsChannelSubscribed
    };
}
