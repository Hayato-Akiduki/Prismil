using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ZenTube.Models;
using ZenTube.Services;

namespace ZenTube;

public partial class MainWindow : Window
{
    private readonly VideoSearchService _searchService = new();
    private readonly LibraryService _libraryService = new();
    private readonly AdBlockService _adBlockService = new();
    private readonly DispatcherTimer _sleepTimer;

    private List<VideoItem> _discoverItems = [];
    private List<VideoItem> _musicItems = [];
    private List<VideoItem> _searchItems = [];
    private List<VideoItem> _subscriptionFeedItems = [];
    private List<VideoItem> _channelItems = [];
    private VideoItem? _currentPlayingItem;
    private CustomPlaylist? _activeCustomPlaylist;
    private SubscribedChannel? _activeSubscribedChannel;

    private bool _isWebViewReady;
    private bool _isLoading;
    private CoreWebView2Environment? _webViewEnv;
    private bool _isMusicBgListen = true;
    private int _shortsFilterMode; // 0: ショート除外, 1: ショートのみ, 2: すべて表示
    private bool _isLoopMode;
    private bool _isAutoNext = true;
    private bool _isShuffleMode;
    private bool _isMirrorMode;
    private bool _isDimMode;
    private bool _isAudioOnlyMode;
    private bool _isMiniPlayerMode;
    private bool _isFullScreen;
    private bool _isSideQueueOpen = true;
    private double _playbackRate = 1.0;
    private double _audioGain = 1.0;
    private string _audioPreset = "normal";
    private string _ambilightMode = "vivid";
    private string _danmakuStyle = "normal";
    private string _currentDanmakuPoolJson = "[]";

    private int _sleepRemainingSeconds;
    private string _activeQueueTab = "Current";
    private string _lastTopicKeyword = "";
    private string _lastMusicStation = "";
    private string _lastSearchQuery = "";

    // 検索の状態
    /// <summary>検索ボックスで検索したキーワード（ガチャ結果などのときは空）</summary>
    private string _searchKeyword = "";
    /// <summary>今の検索結果がどのサイトのものか（null = 全サイト）</summary>
    private SiteType? _searchSite;
    /// <summary>サイト別検索の続きを読むための目印</summary>
    private VideoSearchService.SiteSearchCursor? _siteSearchCursor;
    /// <summary>全サイト検索の「もっと読み込む」用の目印（サイトごと）</summary>
    private readonly List<VideoSearchService.SiteSearchCursor> _allSearchCursors = [];
    /// <summary>新しい検索を始めるたびに増やす。古い検索の結果が後から届いても捨てるため</summary>
    private int _searchGeneration;
    private bool _isSearching;
    private bool _isLoadingMore;
    private bool _syncingSiteSelection;

    // カード一覧（行単位で表示して、見えている行だけを作る）
    /// <summary>今の一覧の動画すべて（絞り込み・並び替え後。ページに分ける前）</summary>
    private List<VideoItem> _displayedCards = [];
    /// <summary>今のページに出している動画</summary>
    private List<VideoItem> _pageCards = [];
    /// <summary>1ページに出す件数</summary>
    private const int CardsPerPage = 40;
    /// <summary>今のページ（0始まり）</summary>
    private int _cardPage;
    /// <summary>どの一覧を見ているか。変わったら1ページ目に戻す</summary>
    private string _cardListKey = "";
    private ObservableCollection<CardRow> _cardRows = [];
    private int _cardColumns;
    /// <summary>次にカード一覧を入れ替えるとき、一番上へ戻すか（新しい検索・読み込みのとき）</summary>
    private bool _scrollCardsToTopOnNextSet;

    // ホーム（おすすめ）の状態
    /// <summary>今のホーム一覧がどのサイトのものか（null = 全サイト）</summary>
    private SiteType? _homeSite;
    /// <summary>サイト別おすすめの続きを読むための目印</summary>
    private VideoSearchService.SiteSearchCursor? _homeCursor;
    private int _homeGeneration;
    private bool _isHomeLoading;
    /// <summary>ボタン側でホームを読み込み直すとき、サイト切り替えによる自動の読み込み直しを止める</summary>
    private bool _suppressHomeAutoReload;

    private WindowState _prevWindowState;
    private WindowStyle _prevWindowStyle;
    private double _prevLeft;
    private double _prevTop;
    private double _prevWidth;
    private double _prevHeight;
    private bool _prevTopmost;

    public MainWindow()
    {
        InitializeComponent();

        // 前回のサイドバーの開閉状態を反映
        _isSidebarOpen = !_libraryService.SidebarCollapsed;
        SidebarColumn.Width = new GridLength(CurrentSidebarWidth);
        UpdateSidebarToggleButton();

        // 読み込むサイトの設定を反映（起動時の読み込みより先に）
        _searchService.SetDisabledSites(_libraryService.DisabledSites);
        BuildSiteToggles();

        PlaylistsNavControl.ItemsSource = _libraryService.Playlists;
        SubscriptionsNavControl.ItemsSource = _libraryService.Subscriptions;
        RecentSearchesControl.ItemsSource = _libraryService.RecentSearches;

        _libraryService.CountsChanged += UpdateLibraryBadges;
        _adBlockService.BlockedCountChanged += _ =>
        {
            Dispatcher.InvokeAsync(UpdateAdBlockButtonUI);
        };
        UpdateLibraryBadges();

        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sleepTimer.Tick += SleepTimer_Tick;

        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        // Windows 11 のすりガラス効果（非対応の環境では元の不透明な背景のまま）
        SourceInitialized += (_, _) => GlassBackdrop.Apply(this, GlassBackdrop.Kind.Acrylic);
    }

    /// <summary>トグルボタンがOFFのときの控えめな枠線</summary>
    private static readonly SolidColorBrush NeutralBorderBrush = MakeFrozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    /// <summary>ボタンの通常時の半透明な背景</summary>
    private static readonly SolidColorBrush NeutralFillBrush = MakeFrozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));

    // =========================================================================
    // 左サイドバーの表示 / 非表示（☰ ボタン・Ctrl+B）
    // =========================================================================
    private const double SidebarFullWidth = 236;
    private bool _isSidebarOpen = true;

    /// <summary>今の開閉状態でのサイドバーの幅（全画面やミニ窓から戻る時にも使う）</summary>
    private double CurrentSidebarWidth => _isSidebarOpen ? SidebarFullWidth : 0;

    // 列の幅（GridLength）は直接アニメーションできないので、数値のプロパティを経由して動かす
    private static readonly DependencyProperty SidebarAnimWidthProperty =
        DependencyProperty.Register(nameof(SidebarAnimWidth), typeof(double), typeof(MainWindow),
            new PropertyMetadata(SidebarFullWidth, (d, e) =>
                ((MainWindow)d).SidebarColumn.Width = new GridLength(Math.Max(0, (double)e.NewValue))));

    private double SidebarAnimWidth
    {
        get => (double)GetValue(SidebarAnimWidthProperty);
        set => SetValue(SidebarAnimWidthProperty, value);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarOpen(!_isSidebarOpen);

    /// <summary>開閉ボタンの見た目を今の状態に合わせる（押すとどうなるかが分かるように）</summary>
    private void UpdateSidebarToggleButton()
    {
        // 開いている時は左向き矢印（押すと左へ隠れる）、閉じている時は右向き矢印（押すと右へ出てくる）
        SidebarToggleIcon.Text = _isSidebarOpen ? "" : "";
        SidebarToggleLabel.Visibility = _isSidebarOpen ? Visibility.Collapsed : Visibility.Visible;
        BtnToggleSidebar.ToolTip = _isSidebarOpen ? "メニューを隠す (Ctrl+B)" : "メニューを表示 (Ctrl+B)";
    }

    private void SetSidebarOpen(bool open)
    {
        _isSidebarOpen = open;
        _libraryService.SidebarCollapsed = !open;
        _libraryService.Save();
        UpdateSidebarToggleButton();

        // 全画面・ミニ窓・シアターの間は幅を動かさない（戻った時に今の状態が反映される）
        if (_isFullScreen || _isMiniPlayerMode || _isTheaterMode) return;

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = CurrentSidebarWidth,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            // 終わったら元の値（＝最終の幅）に戻るだけにして、全画面などが幅を自由に変えられるようにする
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        double from = SidebarColumn.ActualWidth;
        SidebarAnimWidth = CurrentSidebarWidth; // 最終の幅を先に設定（アニメーション終了後の値）
        anim.From = from;
        BeginAnimation(SidebarAnimWidthProperty, anim);
    }

    // 「⋯」メニュー：開いている時にボタンを押すと、閉じた直後にまた開いてしまうのを防ぐ
    private DateTime _moreMenuClosedAt;

    private void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _moreMenuClosedAt).TotalMilliseconds < 250) return;
        MoreMenuPopup.IsOpen = true;
    }

    private async void MoreMenuPopup_Closed(object? sender, EventArgs e)
    {
        _moreMenuClosedAt = DateTime.Now;

        // 読み込むサイトを変えていたら、メニューを閉じたタイミングで1回だけ読み込み直す
        if (_siteSelectionChanged)
        {
            _siteSelectionChanged = false;
            await ReloadAfterSiteChangeAsync();
        }
    }

    // =========================================================================
    // 読み込むサイトの選択
    // =========================================================================
    private static readonly (SiteType Site, string Label, string Color)[] SiteToggleDefs =
    [
        (SiteType.YouTube, "YouTube", "#FF0033"),
        (SiteType.Niconico, "ニコニコ", "#E2E8F0"),
        (SiteType.Bilibili, "bilibili", "#00A1D6"),
        (SiteType.SoundCloud, "SoundCloud", "#FF5500"),
        (SiteType.Twitch, "Twitch", "#9146FF"),
        (SiteType.Abema, "ABEMA", "#00B94B"),
        (SiteType.Podcast, "Podcast", "#8B5CF6"),
        (SiteType.Radio, "ネットラジオ", "#0EA5E9"),
    ];

    private bool _siteSelectionChanged;

    private void BuildSiteToggles()
    {
        SiteToggleGrid.Children.Clear();
        foreach (var (site, label, color) in SiteToggleDefs)
        {
            var cb = new CheckBox
            {
                Content = label,
                Tag = new BrushConverter().ConvertFromString(color),
                IsChecked = !_libraryService.DisabledSites.Contains(site),
                Style = (Style)FindResource("SiteToggle"),
                ToolTip = $"{label} を読み込む / 読み込まない",
            };
            cb.Click += (_, _) => OnSiteToggleClicked(cb, site);
            SiteToggleGrid.Children.Add(cb);
        }
        UpdateSiteFilterVisibility();
    }

    private void OnSiteToggleClicked(CheckBox cb, SiteType site)
    {
        bool enable = cb.IsChecked == true;

        // 全部OFFにはできないようにする（何も表示されなくなるため）
        int enabledCount = SiteToggleDefs.Count(d => !_libraryService.DisabledSites.Contains(d.Site));
        if (!enable && enabledCount <= 1)
        {
            cb.IsChecked = true;
            return;
        }

        if (enable) _libraryService.DisabledSites.Remove(site);
        else _libraryService.DisabledSites.Add(site);

        _libraryService.Save();
        _searchService.SetDisabledSites(_libraryService.DisabledSites);
        UpdateSiteFilterVisibility();
        _siteSelectionChanged = true;
    }

    private (SiteType Site, RadioButton Radio)[] SiteFilterRadios() =>
    [
        (SiteType.YouTube, FilterYouTube), (SiteType.Niconico, FilterNiconico),
        (SiteType.Bilibili, FilterBilibili), (SiteType.SoundCloud, FilterSoundCloud),
        (SiteType.Twitch, FilterTwitch), (SiteType.Abema, FilterAbema),
        (SiteType.Podcast, FilterPodcast),
        (SiteType.Radio, FilterRadio),
    ];

    /// <summary>左の「サイト別に見る」で選ばれているサイト（「すべてのサイト」なら null）</summary>
    private SiteType? SelectedSiteFilter()
    {
        foreach (var (site, radio) in SiteFilterRadios())
            if (radio.IsChecked == true) return site;
        return null;
    }

    private static string SiteLabel(SiteType site) =>
        SiteToggleDefs.FirstOrDefault(d => d.Site == site).Label ?? site.ToString();

    /// <summary>左の「サイト別に見る」と検索ボックス横のサイト選択から、読み込まないサイトを隠す</summary>
    private void UpdateSiteFilterVisibility()
    {
        foreach (var (site, radio) in SiteFilterRadios())
        {
            bool on = _searchService.IsSiteEnabled(site);
            radio.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on && radio.IsChecked == true) FilterAll.IsChecked = true;
        }
        RebuildSearchSiteCombo();
    }

    /// <summary>検索ボックス横の「検索するサイト」の選択肢を作り直す</summary>
    private void RebuildSearchSiteCombo()
    {
        _syncingSiteSelection = true;
        try
        {
            SearchSiteCombo.Items.Clear();
            SearchSiteCombo.Items.Add(new ComboBoxItem { Content = "すべてのサイト", Tag = FilterAll });
            foreach (var (site, radio) in SiteFilterRadios())
            {
                if (!_searchService.IsSiteEnabled(site)) continue;
                SearchSiteCombo.Items.Add(new ComboBoxItem { Content = SiteLabel(site), Tag = radio });
            }
        }
        finally { _syncingSiteSelection = false; }
        SyncSearchSiteComboFromFilter();
    }

    /// <summary>左の「サイト別に見る」の選択を、検索ボックス横のサイト選択に反映する</summary>
    private void SyncSearchSiteComboFromFilter()
    {
        _syncingSiteSelection = true;
        try
        {
            var item = SearchSiteCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is RadioButton r && r.IsChecked == true);
            SearchSiteCombo.SelectedItem = item ?? SearchSiteCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally { _syncingSiteSelection = false; }
    }

    /// <summary>検索ボックス横でサイトを選んだら、左の「サイト別に見る」も同じサイトにする（検索し直しはそちらで行う）</summary>
    private void SearchSiteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSiteSelection) return;
        if (SearchSiteCombo.SelectedItem is ComboBoxItem { Tag: RadioButton radio } && radio.IsChecked != true)
            radio.IsChecked = true;
    }

    /// <summary>今見ている一覧が通信で取ってきたものなら読み込み直す（お気に入り等の保存済み一覧はそのまま）</summary>
    private async Task ReloadAfterSiteChangeAsync()
    {
        if (_activeSubscribedChannel != null)
            await OpenChannelViewAsync(_activeSubscribedChannel);
        else if (NavMusicMode.IsChecked == true)
            await LoadMusicFeedAsync(_lastMusicStation);
        else if (NavSubscriptionsFeed.IsChecked == true)
            await LoadSubscriptionsFeedAsync();
        else if (NavSearch.IsChecked == true && !string.IsNullOrEmpty(_lastSearchQuery))
            await ExecuteSearchAsync(_lastSearchQuery);
        else if (NavHome.IsChecked == true)
            await LoadDiscoverFeedAsync(_lastTopicKeyword);
    }

    // 画面が切り替わる項目を押したらメニューを閉じる（ON/OFF切替の項目は開いたまま状態を見せる）
    private void MoreMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == BtnMusicMode || e.OriginalSource == BtnGacha)
            MoreMenuPopup.IsOpen = false;
    }

    private static SolidColorBrush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySavedSettings(); // 前回の設定に戻す（最初の読み込みより先に）
        NavHome.IsChecked = true;
        AppVersionText.Text = $"Prismil v{UpdateService.CurrentVersionText}";
        var initWebTask = InitPlayerWebViewAsync();
        var loadFeedTask = LoadDiscoverFeedAsync();
        await Task.WhenAll(initWebTask, loadFeedTask);

        // 起動が落ち着いてから、裏で新しい版がないか確かめる
        _ = CheckForAppUpdateAsync();
    }

    // =========================================================================
    // 自動更新（GitHub Releases に新しい版があれば裏でダウンロードして、右下でお知らせ）
    // =========================================================================
    private readonly UpdateService _updateService = new();
    private Velopack.VelopackAsset? _pendingUpdate;

    private async Task CheckForAppUpdateAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var release = await Task.Run(_updateService.CheckAndDownloadAsync);
            if (release == null) return;

            _pendingUpdate = release;
            UpdateBannerTitle.Text = $"新しいバージョン v{release.Version} の準備ができました";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // ネットにつながっていない時なども、ここで止めて普段通り使えるようにする
            ErrorLog.Write("UpdateCheck", ex);
        }
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void UpdateRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        try
        {
            _libraryService.Save();
            _updateService.RestartToApply(_pendingUpdate);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("UpdateApply", ex);
            MessageBox.Show("更新を始められませんでした。次に起動した時に自動で更新されます。",
                "Prismil", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // =========================================================================
    // WebView2 初期化 ＆ Zen Player / 広告ブロッカー注入
    // =========================================================================
    private async Task InitPlayerWebViewAsync()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZenTube", "WebView2");

        try
        {
            // 独自プレイヤーの中の埋め込み動画が、クリックしなくても音つきで再生されるようにする
            var envOptions = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, envOptions);
            _webViewEnv = env; // コメント欄ウィンドウでも同じログイン状態を使う
            await PlayerWebView.EnsureCoreWebView2Async(env);
            var core = PlayerWebView.CoreWebView2;

            // 独自プレイヤー画面（https://player.prismil.local/player.html）を使えるようにする
            PrismilPlayer.Install(core, env);

            await core.AddScriptToExecuteOnDocumentCreatedAsync(ZenPlayerScript.BootstrapScript);
            await UpdateYouTubeAdStripScriptAsync();

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (_adBlockService.ShouldBlock(e.Request.Uri))
                {
                    e.Response = env.CreateWebResourceResponse(
                        Stream.Null, 204, "No Content", "Access-Control-Allow-Origin: *");
                }
            };

            core.WebMessageReceived += (_, e) =>
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg == "ad_skipped")
                {
                    _adBlockService.IncrementCount();
                }
                else if (msg == "need_login")
                {
                    SkipLoginRequiredVideo();
                }
                else if (msg == "embed_blocked")
                {
                    // 埋め込みが禁止されたニコニコ動画は、普通の視聴ページで開き直す
                    var cur = _currentPlayingItem;
                    if (cur != null && cur.Site == SiteType.Niconico && PrismilPlayer.IsPlayerUrl(core.Source))
                        core.Navigate(cur.WatchUrl);
                }
                else if (msg == "theater_toggle")
                {
                    SetTheaterMode(!_isTheaterMode); // 動画の上で T キー
                }
                else if (msg == "theater_exit")
                {
                    SetTheaterMode(false); // 動画の上で Esc キー
                }
                else if (msg == "video_ended")
                {
                    if (_isAutoNext && !_isLoopMode)
                    {
                        PlayNextInQueue(step: 1);
                    }
                }
                else if (msg != null && msg.StartsWith("ab_state:"))
                {
                    var state = msg["ab_state:".Length..];
                    if (state == "OFF")
                    {
                        BtnABRepeat.Content = "📍 A-B区間ループ: OFF";
                        BtnABRepeat.BorderBrush = NeutralBorderBrush;
                    }
                    else
                    {
                        BtnABRepeat.Content = $"📍 {state}";
                        BtnABRepeat.BorderBrush = new SolidColorBrush(Color.FromRgb(236, 72, 153));
                    }
                }
                else if (msg != null && msg.StartsWith("doc_title:"))
                {
                    // 独自プレイヤーでは、埋め込み側のページタイトルから動画名を補う
                    TryFillPlaceholderTitle(msg["doc_title:".Length..]);
                }
                else if (msg != null && msg.StartsWith("ambient_rgb3:"))
                {
                    if (_ambilightMode != "off")
                        OnAmbientColors(msg["ambient_rgb3:".Length..]);
                }
                else if (msg != null && msg.StartsWith("ambient_rgb:"))
                {
                    if (_ambilightMode != "off")
                    {
                        var parts = msg["ambient_rgb:".Length..].Split(',');
                        if (parts.Length == 3 &&
                            byte.TryParse(parts[0], out byte r) &&
                            byte.TryParse(parts[1], out byte g) &&
                            byte.TryParse(parts[2], out byte b))
                        {
                            ApplyNativeAmbilightTint(r, g, b);
                        }
                    }
                }
            };

            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (VideoSearchService.TryParseDirectVideo(e.Uri, out var item))
                {
                    PlayVideo(item);
                }
                else
                {
                    core.Navigate(e.Uri);
                }
            };

            core.ContainsFullScreenElementChanged += (_, _) =>
                SetFullScreen(core.ContainsFullScreenElement);

            core.NavigationCompleted += async (_, _) =>
            {
                await ApplyZenModeToPlayerAsync();
                await InjectCurrentDanmakuPoolAsync();
                if (!PrismilPlayer.IsPlayerUrl(core.Source))
                {
                    TryFillPlaceholderTitle(core.DocumentTitle);
                }
            };

            _isWebViewReady = true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Microsoft Edge WebView2 ランタイムが見つかりません。\nhttps://developer.microsoft.com/microsoft-edge/webview2/ からインストールしてください。",
                "Prismil", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 初期化エラー時は無視
        }
    }

    /// <summary>URLから開いた動画など、仮のタイトル（「YouTube (xxxx)」等）のままなら本当のタイトルに置き換える</summary>
    private void TryFillPlaceholderTitle(string? docTitle)
    {
        if (_currentPlayingItem == null || string.IsNullOrWhiteSpace(docTitle)) return;
        var t = _currentPlayingItem.Title;
        bool isPlaceholder =
            t.StartsWith("YouTube (") || t.StartsWith("ニコニコ動画 (") || t.StartsWith("bilibili (") ||
            t.StartsWith("Twitch (") || t.StartsWith("ABEMA (") || t.StartsWith("SoundCloud (") ||
            t.StartsWith("Dailymotion (");
        if (!isPlaceholder) return;

        var cleaned = CleanDocumentTitle(docTitle);
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "YouTube" or "ニコニコ動画" or "bilibili") return;
        _currentPlayingItem.Title = cleaned;
        UpdatePlayingTitles(_currentPlayingItem);
        _libraryService.Save();
    }

    private static string CleanDocumentTitle(string docTitle)
    {
        return docTitle
            .Replace(" - YouTube", "")
            .Replace(" - ニコニコ動画", "")
            .Replace("_哔哩哔哩_bilibili", "")
            .Replace(" - bilibili", "")
            .Replace(" - Twitch", "")
            .Replace(" | ABEMA", "")
            .Replace(" - Dailymotion", "")
            .Replace(" | Listen online for free on SoundCloud", "")
            .Trim();
    }

    private async Task ApplyZenModeToPlayerAsync()
    {
        if (!_isWebViewReady || PlayerWebView.CoreWebView2 == null) return;
        try
        {
            // サイトの画面は常にプレイヤー部分だけを表示する（コメントは「💬 コメント」ボタンの別ウィンドウで見る）
            const string zenArg = "true";
            string adArg = _adBlockService.IsEnabled ? "true" : "false";
            string shortsArg = _shortsFilterMode == 0 ? "true" : "false";
            string loopArg = _isLoopMode ? "true" : "false";
            string mirrorArg = _isMirrorMode ? "true" : "false";
            string dimArg = _isDimMode ? "true" : "false";
            string rateArg = _playbackRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string gainArg = _audioGain.ToString(System.Globalization.CultureInfo.InvariantCulture);

            await PlayerWebView.CoreWebView2.ExecuteScriptAsync(
                $"if (window.setZenTubeMode) window.setZenTubeMode({zenArg});" +
                $"if (window.setZenTubeAmbilight) window.setZenTubeAmbilight('{_ambilightMode}');" +
                $"if (window.setZenTubeDanmaku) window.setZenTubeDanmaku('{_danmakuStyle}');" +
                $"if (window.setZenTubeAdBlock) window.setZenTubeAdBlock({adArg});" +
                $"if (window.setZenTubeBlockShorts) window.setZenTubeBlockShorts({shortsArg});" +
                $"if (window.setZenTubeLoop) window.setZenTubeLoop({loopArg});" +
                $"if (window.setZenTubePlaybackRate) window.setZenTubePlaybackRate({rateArg});" +
                $"if (window.setZenTubeVideoFx) window.setZenTubeVideoFx({mirrorArg}, {dimArg});" +
                $"if (window.setZenTubeAudioProfile) window.setZenTubeAudioProfile({gainArg}, '{_audioPreset}');");
        }
        catch
        {
            // スクリプト実行失敗時は無視
        }
    }

    // =========================================================================
    // トレンド取得・Musicモード取得 ＆ 全サイト横断検索
    // =========================================================================
    private async Task LoadDiscoverFeedAsync(string? topicKeyword = null)
    {
        _lastTopicKeyword = topicKeyword ?? "";

        // 左の「サイト別に見る」で1サイトを選んでいれば、そのサイトのおすすめだけを多めに取る
        var site = SelectedSiteFilter();
        int generation = ++_homeGeneration;
        _homeSite = site;
        _homeCursor = null;
        if (NavHome.IsChecked == true) _scrollCardsToTopOnNextSet = true;

        _isHomeLoading = true;
        List<VideoItem> items;
        try
        {
            if (site is SiteType s)
            {
                ShowLoadingOverlay(
                    string.IsNullOrEmpty(_lastTopicKeyword)
                        ? $"{SiteLabel(s)} のおすすめを取得中..."
                        : $"「{_lastTopicKeyword}」の動画を{SiteLabel(s)}から取得中...",
                    $"{SiteLabel(s)} の人気・話題の動画を集めています");
                _homeCursor = new VideoSearchService.SiteSearchCursor(s, _lastTopicKeyword, shortsOnly: _shortsFilterMode == 1);
                items = await _searchService.SearchSiteNextPageAsync(_homeCursor);
            }
            else
            {
                ShowLoadingOverlay(
                    string.IsNullOrEmpty(_lastTopicKeyword)
                        ? "5サイトのトレンド・音楽・配信を取得中..."
                        : $"「{_lastTopicKeyword}」の動画を全サイトから取得中...",
                    "YouTube・ニコニコ・SoundCloud・bilibili・Twitch から並列取得しています");
                // 届いたサイトの結果から先に表示する
                void OnPartial(List<VideoItem> partialItems)
                {
                    if (generation != _homeGeneration || NavHome.IsChecked != true) return;
                    _libraryService.SyncCollectionFlags(partialItems);
                    _discoverItems = partialItems;
                    RefreshDisplayedCards(partial: true);
                }
                items = _shortsFilterMode == 1
                    ? await _searchService.SearchAllShortsAsync(_lastTopicKeyword, limitPerSite: 15)
                    : await _searchService.GetDiscoverFeedAsync(_lastTopicKeyword, limitPerSite: 9, onPartial: OnPartial);
            }
        }
        finally
        {
            _isHomeLoading = false;
        }

        // 待っている間に別の読み込みが始まっていたら、この結果は使わない
        if (generation != _homeGeneration) return;

        _libraryService.SyncCollectionFlags(items);
        _discoverItems = items;

        HideLoadingOverlay();
        if (NavHome.IsChecked == true)
            RefreshDisplayedCards();
    }

    private static string FormatMusicStationTitle(string station)
    {
        if (string.IsNullOrEmpty(station))
            return "🎧 Music & Radio Hub — 音楽・ラジオ・Podcast";
        if (station.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
            return $"🎙 ポッドキャスト番組: {station["podcast:".Length..]}";
        if (station.StartsWith("radio:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = station["radio:".Length..];
            return tag.Equals("JP", StringComparison.OrdinalIgnoreCase)
                ? "📻 日本のネットラジオ局 (24h LIVE)"
                : $"📻 24時間ネットラジオ局: {tag.ToUpperInvariant()}";
        }
        return $"🎧 ステーション: {station}";
    }

    private async Task LoadMusicFeedAsync(string? stationKeyword = null)
    {
        _lastMusicStation = stationKeyword ?? "";
        ShowLoadingOverlay(
            FormatMusicStationTitle(_lastMusicStation) + " を取得中...",
            "SoundCloud・Podcast・ネットラジオ・YouTube・ニコニコ音楽から厳選しています");

        var items = await _searchService.SearchMusicFeedAsync(_lastMusicStation, limitPerSite: 12);
        _libraryService.SyncCollectionFlags(items);
        _musicItems = items;
        NavMusicMode.Tag = items.Count > 0 ? items.Count.ToString() : null;

        HideLoadingOverlay();
        if (NavMusicMode.IsChecked == true)
            RefreshDisplayedCards();
    }

    private async Task ExecuteSearchAsync(string query, bool rememberQuery = true)
    {
        query = query.Trim();
        if (string.IsNullOrEmpty(query)) return;

        if (VideoSearchService.TryParseDirectVideo(query, out var directItem))
        {
            _libraryService.SyncItemFlags(directItem);
            PlayVideo(directItem);
            return;
        }

        _lastSearchQuery = query;
        if (rememberQuery) _libraryService.AddRecentSearch(query);
        _activeCustomPlaylist = null;
        _activeSubscribedChannel = null;

        // Musicモード選択中に検索した場合は音楽特化検索をそのまま実行
        if (NavMusicMode.IsChecked == true)
        {
            SwitchToCardBrowserView();
            await LoadMusicFeedAsync(query);
            return;
        }

        // 左の「サイト別に見る」（または検索ボックス横の選択）で1サイトを選んでいれば、そのサイトだけを検索する
        var site = SelectedSiteFilter();
        int generation = ++_searchGeneration;
        _searchKeyword = query;
        _searchSite = site;
        _siteSearchCursor = null;
        _allSearchCursors.Clear();
        _scrollCardsToTopOnNextSet = true;

        _isSearching = true;
        List<VideoItem> results;
        try
        {
            NavSearch.IsChecked = true;
            SwitchToCardBrowserView();

            if (site is SiteType s)
            {
                ShowLoadingOverlay(
                    $"「{query}」を{SiteLabel(s)}で検索中...",
                    $"{SiteLabel(s)} の検索結果だけを表示します");
                _siteSearchCursor = new VideoSearchService.SiteSearchCursor(s, query, shortsOnly: _shortsFilterMode == 1);
                results = await _searchService.SearchSiteNextPageAsync(_siteSearchCursor);
            }
            else
            {
                ShowLoadingOverlay(
                    $"「{query}」を全サイトで検索中...",
                    "YouTube・ニコニコ・SoundCloud・bilibili・Twitch などを同時に検索しています");
                // 届いたサイトの結果から先に表示する
                void OnPartial(List<VideoItem> partialItems)
                {
                    if (generation != _searchGeneration || NavSearch.IsChecked != true) return;
                    _libraryService.SyncCollectionFlags(partialItems);
                    _searchItems = partialItems;
                    RefreshDisplayedCards(partial: true);
                }
                results = _shortsFilterMode == 1
                    ? await _searchService.SearchAllShortsAsync(query, limitPerSite: 20)
                    : await _searchService.SearchAllAsync(query, limitPerSite: 20, onPartial: OnPartial);
            }
        }
        finally
        {
            _isSearching = false;
        }

        // 待っている間に別の検索が始まっていたら、この結果は使わない
        if (generation != _searchGeneration) return;

        _libraryService.SyncCollectionFlags(results);
        _searchItems = results;
        NavSearch.Tag = results.Count > 0 ? results.Count.ToString() : null;

        HideLoadingOverlay();
        RefreshDisplayedCards();
    }

    /// <summary>検索結果の続きを読めるか</summary>
    private bool CanLoadMoreSearch()
    {
        if (string.IsNullOrEmpty(_searchKeyword) || _lastSearchQuery != _searchKeyword) return false;
        if (_siteSearchCursor != null) return _siteSearchCursor.HasMore;
        return _allSearchCursors.Count == 0 || _allSearchCursors.Any(c => c.HasMore);
    }

    /// <summary>検索結果の続き（次のページ）を読み込んで、一覧の後ろに足す</summary>
    /// <summary>今の一覧（検索結果・ホームのおすすめ）の続きを読み込んで、後ろに足す</summary>
    private async Task LoadMoreCurrentAsync()
    {
        if (NavHome.IsChecked == true)
        {
            await LoadMoreHomeAsync();
            return;
        }
        if (_isLoadingMore || _isSearching || !CanLoadMoreSearch()) return;
        _isLoadingMore = true;
        int generation = _searchGeneration;
        UpdatePager();
        try
        {
            var more = _siteSearchCursor != null
                ? await _searchService.SearchSiteNextPageAsync(_siteSearchCursor)
                : await _searchService.SearchAllNextPageAsync(
                    _searchKeyword, _allSearchCursors, _searchItems, shortsOnly: _shortsFilterMode == 1);
            if (generation != _searchGeneration) return;

            _libraryService.SyncCollectionFlags(more);
            _searchItems = [.. _searchItems, .. more];
            NavSearch.Tag = _searchItems.Count > 0 ? _searchItems.Count.ToString() : null;
            RefreshDisplayedCards();
        }
        finally
        {
            _isLoadingMore = false;
            UpdatePager();
        }
    }

    /// <summary>サイト別おすすめの続きを読み込んで、一覧の後ろに足す</summary>
    private async Task LoadMoreHomeAsync()
    {
        var cursor = _homeCursor;
        if (_isLoadingMore || _isHomeLoading || cursor == null || !cursor.HasMore) return;
        _isLoadingMore = true;
        int generation = _homeGeneration;
        UpdatePager();
        try
        {
            var more = await _searchService.SearchSiteNextPageAsync(cursor);
            if (generation != _homeGeneration) return;

            _libraryService.SyncCollectionFlags(more);
            _discoverItems = [.. _discoverItems, .. more];
            RefreshDisplayedCards();
        }
        finally
        {
            _isLoadingMore = false;
            UpdatePager();
        }
    }

    /// <summary>今の一覧に、まだ読み込める続きがあるか</summary>
    private bool CanLoadMoreCurrent()
    {
        if (_activeCustomPlaylist != null || _activeSubscribedChannel != null) return false;
        if (NavSearch.IsChecked == true) return _searchItems.Count > 0 && CanLoadMoreSearch();
        if (NavHome.IsChecked == true) return _discoverItems.Count > 0 && _homeCursor?.HasMore == true;
        return false;
    }

    private int CardPageCount => Math.Max(1, (_displayedCards.Count + CardsPerPage - 1) / CardsPerPage);

    /// <summary>指定したページへ移る。まだ読み込んでいないページなら、続きを読み込んでから移る</summary>
    private async Task GoToPageAsync(int page)
    {
        if (page < 0 || _isLoadingMore) return;

        // 絞り込みで減ることがあるので、足りるまで数回まで読み込む
        for (int tries = 0; tries < 4 && _displayedCards.Count <= page * CardsPerPage && CanLoadMoreCurrent(); tries++)
        {
            int before = _displayedCards.Count;
            await LoadMoreCurrentAsync();
            if (_displayedCards.Count == before && !CanLoadMoreCurrent()) break;
        }

        _cardPage = Math.Clamp(page, 0, CardPageCount - 1);
        _scrollCardsToTopOnNextSet = true;
        RefreshDisplayedCards();
    }

    private async void PagerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int page }) await GoToPageAsync(page);
    }

    /// <summary>一覧の下の「前へ・ページ番号・次へ」を作り直す</summary>
    private void UpdatePager()
    {
        PagerPanel.Children.Clear();
        int pageCount = CardPageCount;
        bool canMore = CanLoadMoreCurrent();
        if (_displayedCards.Count == 0 || (pageCount <= 1 && !canMore))
        {
            PagerBar.Visibility = Visibility.Collapsed;
            return;
        }
        PagerBar.Visibility = Visibility.Visible;

        AddPagerButton("‹ 前へ", _cardPage - 1, _cardPage > 0 && !_isLoadingMore);

        // 最初・最後のページと、今のページの前後2ページを出す（間は「…」）
        var pages = new SortedSet<int> { 0, pageCount - 1 };
        for (int i = _cardPage - 2; i <= _cardPage + 2; i++)
            if (i >= 0 && i < pageCount) pages.Add(i);
        int prev = -1;
        foreach (int pg in pages)
        {
            if (prev >= 0 && pg > prev + 1) AddPagerEllipsis();
            AddPagerButton((pg + 1).ToString(), pg, !_isLoadingMore, current: pg == _cardPage);
            prev = pg;
        }
        if (canMore) AddPagerEllipsis(); // まだ先のページがある

        AddPagerButton(_isLoadingMore ? "読み込み中…" : "次へ ›", _cardPage + 1,
            !_isLoadingMore && (_cardPage < pageCount - 1 || canMore));
    }

    private void AddPagerButton(string text, int page, bool enabled, bool current = false)
    {
        var btn = new Button
        {
            Content = text,
            Tag = page,
            Style = (Style)FindResource(current ? "PrimaryBtn" : "ActionBtn"),
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(12, 6, 12, 6),
            MinWidth = 38,
            IsEnabled = enabled || current,
            IsHitTestVisible = !current,
        };
        btn.Click += PagerButton_Click;
        PagerPanel.Children.Add(btn);
    }

    private void AddPagerEllipsis() =>
        PagerPanel.Children.Add(new TextBlock
        {
            Text = "…",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        });

    /// <summary>
    /// 検索画面で「サイト別に見る」を切り替えたら、そのサイト（または全サイト）で検索し直す。
    /// 表示だけ絞り込むと、全サイト検索で取れた少しの件数しか出ないため。
    /// </summary>
    /// <summary>ホーム画面で「サイト別に見る」を切り替えたら、そのサイト（または全サイト）のおすすめを読み込み直す</summary>
    private async Task<bool> ReloadHomeIfSiteChangedAsync()
    {
        if (_suppressHomeAutoReload || _isHomeLoading || NavHome.IsChecked != true) return false;
        if (_activeCustomPlaylist != null || _activeSubscribedChannel != null) return false;
        if (SelectedSiteFilter() == _homeSite) return false;
        await LoadDiscoverFeedAsync(_lastTopicKeyword);
        return true;
    }

    private async Task<bool> ResearchIfSiteChangedAsync()
    {
        if (_isSearching || NavSearch.IsChecked != true) return false;
        if (string.IsNullOrEmpty(_searchKeyword) || _lastSearchQuery != _searchKeyword) return false;
        if (SelectedSiteFilter() == _searchSite) return false;
        await ExecuteSearchAsync(_searchKeyword, rememberQuery: false);
        return true;
    }

    // =========================================================================
    // カード一覧のフィルタリング・ソート・表示更新
    // =========================================================================
    /// <param name="partial">まだ読み込み中のサイトがある途中経過の表示か</param>
    private void RefreshDisplayedCards(bool partial = false)
    {
        if (!IsLoaded) return;

        IEnumerable<VideoItem> source;
        string headerTitle;
        bool showClearHistory = false;
        bool isCustomPlaylist = _activeCustomPlaylist != null;
        bool isSingleChannel = _activeSubscribedChannel != null;
        bool isMusicMode = !isCustomPlaylist && !isSingleChannel && NavMusicMode.IsChecked == true;

        MusicStationBar.Visibility = isMusicMode ? Visibility.Visible : Visibility.Collapsed;
        BtnMusicMode.Background = isMusicMode
            ? new SolidColorBrush(Color.FromRgb(190, 24, 93))
            : NeutralFillBrush;
        BtnMusicMode.Foreground = isMusicMode
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(244, 114, 182));

        if (isSingleChannel && _activeSubscribedChannel != null)
        {
            source = _channelItems;
            headerTitle = $"⭐ {_activeSubscribedChannel.Name} ({_activeSubscribedChannel.SiteBadgeText})";
        }
        else if (isCustomPlaylist && _activeCustomPlaylist != null)
        {
            source = _activeCustomPlaylist.Items;
            headerTitle = $"📁 {_activeCustomPlaylist.Name}";
        }
        else if (isMusicMode)
        {
            source = _musicItems;
            headerTitle = FormatMusicStationTitle(_lastMusicStation);
        }
        else if (NavSubscriptionsFeed.IsChecked == true)
        {
            source = _subscriptionFeedItems;
            headerTitle = "📡 登録チャンネル新着タイムライン";
        }
        else if (NavSearch.IsChecked == true)
        {
            source = _searchItems;
            headerTitle = string.IsNullOrEmpty(_lastSearchQuery)
                ? "🔍 横断検索結果"
                : _searchSite is SiteType ss && _lastSearchQuery == _searchKeyword
                    ? $"🔍 「{_lastSearchQuery}」の{SiteLabel(ss)}検索結果"
                    : $"🔍 「{_lastSearchQuery}」の横断検索結果";
        }
        else if (NavFavorites.IsChecked == true)
        {
            source = _libraryService.Favorites;
            headerTitle = "♥ お気に入りコレクション";
        }
        else if (NavWatchLater.IsChecked == true)
        {
            source = _libraryService.WatchLater;
            headerTitle = "🕒 後で見るリスト";
        }
        else if (NavHistory.IsChecked == true)
        {
            source = _libraryService.History;
            headerTitle = "📜 視聴履歴";
            showClearHistory = _libraryService.History.Count > 0;
        }
        else
        {
            source = _discoverItems;
            headerTitle = _homeSite is SiteType hs
                ? string.IsNullOrEmpty(_lastTopicKeyword)
                    ? $"🔥 {SiteLabel(hs)} のおすすめ"
                    : $"🔥 トピック: {_lastTopicKeyword}（{SiteLabel(hs)}）"
                : string.IsNullOrEmpty(_lastTopicKeyword)
                    ? "🔥 5サイト横断トレンド"
                    : $"🔥 トピック: {_lastTopicKeyword}";
        }

        // サイト別に見る（選んだサイトだけを表示）
        if (FilterYouTube.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.YouTube);
        else if (FilterNiconico.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.Niconico);
        else if (FilterSoundCloud.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.SoundCloud);
        else if (FilterPodcast.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.Podcast);
        else if (FilterRadio.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.Radio);
        else if (FilterBilibili.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.Bilibili);
        else if (FilterTwitch.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.Twitch);
        else if (FilterAbema.IsChecked == true)
            source = source.Where(x => x.Site == SiteType.Abema);

        // ショート動画 3段階フィルター（お気に入り・後で見る・カスタムプレイリスト以外で有効）
        if (!isCustomPlaylist && NavFavorites.IsChecked != true && NavWatchLater.IsChecked != true)
        {
            if (_shortsFilterMode == 0)
                source = source.Where(x => !x.IsShortVideo);
            else if (_shortsFilterMode == 1)
                source = source.Where(x => x.IsShortVideo);
        }

        // 並び替え
        source = SortCombo.SelectedIndex switch
        {
            1 => source.OrderByDescending(x => x.ViewCount),
            2 => source.OrderByDescending(x => x.DurationSeconds),
            3 => source.Where(x => x.DurationSeconds > 0).OrderBy(x => x.DurationSeconds)
                       .Concat(source.Where(x => x.DurationSeconds <= 0)),
            _ => source
        };

        var finalList = source.ToList();

        // 途中経過（まだ読み込み中のサイトがある）で1件も無ければ、読み込み中の表示のまま待つ
        if (partial)
        {
            if (finalList.Count == 0) return;
            HideLoadingOverlay();
        }

        // 見ている一覧が変わったら（別の検索・画面・サイト・並び順など）1ページ目に戻す
        string listKey = string.Join("|", headerTitle, SelectedSiteFilter(), SortCombo.SelectedIndex, _shortsFilterMode,
            _searchGeneration, _homeGeneration, _activeCustomPlaylist?.Name, _activeSubscribedChannel?.UniqueKey);
        if (listKey != _cardListKey)
        {
            _cardListKey = listKey;
            _cardPage = 0;
            _scrollCardsToTopOnNextSet = true;
        }

        _displayedCards = finalList;
        int pageCount = CardPageCount;
        _cardPage = Math.Clamp(_cardPage, 0, pageCount - 1);
        SetCardItems(finalList.Skip(_cardPage * CardsPerPage).Take(CardsPerPage).ToList());

        ViewHeaderTitle.Text = headerTitle;
        bool canMore = CanLoadMoreCurrent();
        string countText = $"{finalList.Count} 件";
        if (pageCount > 1 || canMore)
            countText += $"・{_cardPage + 1} / {pageCount}{(canMore ? "+" : "")} ページ";
        if (partial) countText += "（ほかのサイトを読み込み中…）";
        ViewHeaderCount.Text = countText;

        BtnClearHistory.Visibility = showClearHistory ? Visibility.Visible : Visibility.Collapsed;
        UpdatePager();
        BtnPlayAllPlaylist.Visibility = isCustomPlaylist && finalList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnDeletePlaylist.Visibility = isCustomPlaylist ? Visibility.Visible : Visibility.Collapsed;
        BtnUnsubscribeChannel.Visibility = isSingleChannel && _activeSubscribedChannel != null &&
            _libraryService.Subscriptions.Any(x => x.UniqueKey == _activeSubscribedChannel.UniqueKey)
            ? Visibility.Visible : Visibility.Collapsed;

        // 空状態の案内表示
        if (!_isLoading)
        {
            if (finalList.Count == 0)
            {
                if (NavSubscriptionsFeed.IsChecked == true && _libraryService.Subscriptions.Count == 0)
                    ShowEmptyState("登録チャンネルがまだありません", "動画カードの投稿者名横にある「☆ 登録」ボタンを押すと、ログイン不要でこのPC内にチャンネル登録できます");
                else if (isCustomPlaylist)
                    ShowEmptyState("このプレイリストはまだ空です", "動画カードの「📁+」ボタンから好きな動画を自由に追加できます");
                else if (NavFavorites.IsChecked == true)
                    ShowEmptyState("お気に入りがまだありません", "動画カードの「♡」ボタンを押すと、サイトをまたいでここへ保存されます");
                else if (NavWatchLater.IsChecked == true)
                    ShowEmptyState("「後で見る」リストは空です", "気になる動画の「+ 後で見る」ボタンを押してストックしましょう");
                else if (NavHistory.IsChecked == true)
                    ShowEmptyState("視聴履歴はまだありません", "動画を再生すると自動的に履歴が記録されます");
                else if (NavSearch.IsChecked == true && string.IsNullOrEmpty(_lastSearchQuery))
                    ShowEmptyState("キーワードを入力して横断検索", "上の検索バーから YouTube・ニコニコ動画・bilibili をまとめて検索できます");
                else
                    ShowEmptyState("該当する動画が見つかりませんでした", "別のキーワードを試すか、左の「サイト別に見る」で別のサイトを選んでください");
            }
            else
            {
                StatusOverlay.Visibility = Visibility.Collapsed;
            }
        }

        RefreshSideQueue();
    }

    /// <summary>今の一覧の幅に、カードが横に何枚並ぶか</summary>
    private int ComputeCardColumns()
    {
        const double cardWidth = 278 + 12; // カード幅 + 左右の余白
        double width = VideoCardsControl.ActualWidth - 40 - SystemParameters.VerticalScrollBarWidth;
        return width <= 0 ? Math.Max(_cardColumns, 1) : Math.Max(1, (int)(width / cardWidth));
    }

    /// <summary>
    /// カード一覧を入れ替える。前の一覧の後ろに足しただけ（もっと読み込む）なら、増えた行だけを足す。
    /// </summary>
    private void SetCardItems(List<VideoItem> list)
    {
        int cols = ComputeCardColumns();
        int oldCount = _pageCards.Count;
        bool isAppend = cols == _cardColumns && oldCount > 0 && list.Count >= oldCount &&
                        ReferenceEquals(VideoCardsControl.ItemsSource, _cardRows) &&
                        list.Take(oldCount).SequenceEqual(_pageCards, ReferenceEqualityComparer.Instance);

        if (isAppend)
        {
            if (list.Count > oldCount)
            {
                // 最後の行が埋まっていなければ、その行から作り直す
                int start = oldCount;
                if (_cardRows.Count > 0 && _cardRows[^1].Items.Count < cols)
                {
                    start -= _cardRows[^1].Items.Count;
                    _cardRows.RemoveAt(_cardRows.Count - 1);
                }
                foreach (var row in BuildCardRows(list, start, cols))
                    _cardRows.Add(row);
            }
        }
        else
        {
            _cardRows = new ObservableCollection<CardRow>(BuildCardRows(list, 0, cols));
            VideoCardsControl.ItemsSource = _cardRows;
        }

        _pageCards = list;
        _cardColumns = cols;

        if (_scrollCardsToTopOnNextSet)
        {
            _scrollCardsToTopOnNextSet = false;
            (VideoCardsControl.Template?.FindName("CardsScrollViewer", VideoCardsControl) as ScrollViewer)?.ScrollToTop();
        }
    }

    private static IEnumerable<CardRow> BuildCardRows(List<VideoItem> list, int start, int cols)
    {
        for (int i = start; i < list.Count; i += cols)
            yield return new CardRow(list.GetRange(i, Math.Min(cols, list.Count - i)));
    }

    /// <summary>ウィンドウの幅が変わって横に並ぶ枚数が変わったら、行を組み直す</summary>
    private void VideoCardsControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || ComputeCardColumns() == _cardColumns) return;
        var list = _pageCards;
        _pageCards = [];
        SetCardItems(list);
    }

    private void RefreshSideQueue()
    {
        if (SideQueueItemsControl == null) return;

        IEnumerable<VideoItem> queueSource = _activeQueueTab switch
        {
            "WatchLater" => _libraryService.WatchLater,
            "Favorites" => _libraryService.Favorites,
            // 次に再生：検索・一覧の続き ＋ 視聴履歴をもとにしたおすすめ（まだ作れていない間は今の一覧）
            _ => _upNext.Count > 0 && _upNextKey == _currentPlayingItem?.UniqueKey
                ? _upNext
                : (_displayedCards.Count > 0 ? _displayedCards : _discoverItems)
        };

        bool showComments = _activeQueueTab == "Comments";
        CommentsPanel.Visibility = showComments ? Visibility.Visible : Visibility.Collapsed;
        SideQueueItemsControl.Visibility = showComments ? Visibility.Collapsed : Visibility.Visible;
        BtnQueueComments.BorderBrush = showComments
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        if (showComments) EnsureCommentsLoaded();
        else SideQueueItemsControl.ItemsSource = queueSource.ToList();

        BtnQueueCurrent.BorderBrush = _activeQueueTab == "Current"
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        BtnQueueWatchLater.BorderBrush = _activeQueueTab == "WatchLater"
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        BtnQueueFavorites.BorderBrush = _activeQueueTab == "Favorites"
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
    }

    // =========================================================================
    // ⏭ 次に再生（連続再生キュー）
    // 自分で検索・表示していた一覧の続きと、再生中の動画や最近の視聴履歴の関連動画（YouTube・ニコニコ）を
    // 交互に並べる。見たことのある動画のおすすめは出さない。動画が変わるたびに作り直す
    // =========================================================================
    private readonly RecommendationService _recommendationService = new();
    private List<VideoItem> _upNext = [];
    private string? _upNextKey;               // どの動画のために作ったキューか
    private List<VideoItem> _upNextSourceList = [];  // 「検索・一覧」の元になる一覧
    private int _upNextSourceIndex = -1;      // その一覧の中で最後に再生した位置

    private const string ReasonSearch = "🔍 検索・一覧";
    private const string ReasonRelated = "✨ おすすめ";
    private const string ReasonHistory = "🕘 履歴から";

    private async Task BuildUpNextAsync(VideoItem current)
    {
        // 「検索・一覧」の元：今表示している一覧。再生した動画がその中にあれば、その次から並べる
        var shown = _displayedCards.Count > 0 ? _displayedCards : _discoverItems;
        int idxInShown = shown.FindIndex(x => x.UniqueKey == current.UniqueKey);
        if (idxInShown >= 0)
        {
            _upNextSourceList = [.. shown];
            _upNextSourceIndex = idxInShown;
        }
        else if (_upNextSourceList.Count == 0)
        {
            _upNextSourceList = [.. shown];
            _upNextSourceIndex = -1;
        }
        var searchPart = _upNextSourceList.Skip(_upNextSourceIndex + 1)
            .Where(x => x.UniqueKey != current.UniqueKey)
            .Take(30).ToList();

        // おすすめ：再生中の動画の関連動画 ＋ 最近見た動画（2本）の関連動画
        var watched = new HashSet<string>(_libraryService.History.Select(h => h.UniqueKey)) { current.UniqueKey };
        var recentSeeds = _libraryService.History
            .Where(h => h.UniqueKey != current.UniqueKey && RecommendationService.Supports(h))
            .Take(2).ToList();

        var currentTask = _recommendationService.GetRelatedAsync(current);
        var seedTasks = recentSeeds.Select(s => _recommendationService.GetRelatedAsync(s)).ToList();
        List<VideoItem> related;
        List<List<VideoItem>> fromHistory;
        try
        {
            related = await currentTask;
            fromHistory = [.. await Task.WhenAll(seedTasks)];
        }
        catch
        {
            related = [];
            fromHistory = [];
        }
        if (_currentPlayingItem?.UniqueKey != current.UniqueKey) return; // 待っている間に別の動画になった

        var recPart = new List<VideoItem>();
        var used = new HashSet<string>(searchPart.Select(x => x.UniqueKey));
        void AddRec(VideoItem v, string reason)
        {
            if (watched.Contains(v.UniqueKey) || !used.Add(v.UniqueKey)) return;
            if (_shortsFilterMode == 0 && v.IsShortVideo) return; // ショート除外中は出さない
            v.QueueReason = reason;
            recPart.Add(v);
        }
        // 再生中の動画の関連を中心に、履歴の関連を3本に1本くらい混ぜる
        int ri = 0, hi = 0;
        var historyFlat = fromHistory.SelectMany(l => l).ToList();
        while (ri < related.Count || hi < historyFlat.Count)
        {
            for (int k = 0; k < 2 && ri < related.Count; k++) AddRec(related[ri++], ReasonRelated);
            if (hi < historyFlat.Count) AddRec(historyFlat[hi++], ReasonHistory);
            if (recPart.Count >= 40) break;
        }

        foreach (var s in searchPart) s.QueueReason = ReasonSearch;

        // 交互に並べる（おすすめ → 検索・一覧 → …）
        var queue = new List<VideoItem>();
        for (int i = 0; i < Math.Max(recPart.Count, searchPart.Count); i++)
        {
            if (i < recPart.Count) queue.Add(recPart[i]);
            if (i < searchPart.Count) queue.Add(searchPart[i]);
        }

        _libraryService.SyncCollectionFlags(queue);
        _upNext = queue;
        _upNextKey = current.UniqueKey;
        if (_activeQueueTab == "Current") RefreshSideQueue();
    }

    private void UpdateLibraryBadges()
    {
        if (NavFavorites == null) return;
        NavSubscriptionsFeed.Tag = _libraryService.Subscriptions.Count > 0 ? _libraryService.Subscriptions.Count.ToString() : null;
        NavFavorites.Tag = _libraryService.Favorites.Count > 0 ? _libraryService.Favorites.Count.ToString() : null;
        NavWatchLater.Tag = _libraryService.WatchLater.Count > 0 ? _libraryService.WatchLater.Count.ToString() : null;
        NavHistory.Tag = _libraryService.History.Count > 0 ? _libraryService.History.Count.ToString() : null;
    }

    // =========================================================================
    // 動画再生 ＆ クリーンプレイヤー制御
    // =========================================================================
    // ログインが必要な動画を続けて飛ばした回数（全部ログインが必要な時に延々と飛ばし続けないため）
    private int _loginSkipStreak;
    private bool _isSkippingForLogin;
    private System.Windows.Threading.DispatcherTimer? _noticeTimer;

    /// <summary>ログインしないと見られないニコニコ動画は再生せず、次の動画へ飛ばす</summary>
    private void SkipLoginRequiredVideo()
    {
        var item = _currentPlayingItem;
        if (item == null || item.Site != SiteType.Niconico) return;

        bool hasNext = SideQueueItemsControl.ItemsSource is IList<VideoItem> list && list.Count > 1;
        if (!hasNext || _loginSkipStreak >= 5)
        {
            ShowNotice("この動画はログインが必要なため再生できません");
            return;
        }

        _loginSkipStreak++;
        ShowNotice($"「{item.Title}」はログインが必要なため飛ばしました");
        _isSkippingForLogin = true;
        try { PlayNextInQueue(step: 1); }
        finally { _isSkippingForLogin = false; }
    }

    private void ShowNotice(string text)
    {
        NoticeToastText.Text = text;
        NoticeToast.Visibility = Visibility.Visible;
        _noticeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Stop();
        _noticeTimer.Tick -= NoticeTimer_Tick;
        _noticeTimer.Tick += NoticeTimer_Tick;
        _noticeTimer.Start();
    }

    private void NoticeTimer_Tick(object? sender, EventArgs e)
    {
        _noticeTimer?.Stop();
        NoticeToast.Visibility = Visibility.Collapsed;
    }

    private void PlayVideo(VideoItem item, bool forceZenMode = true)
    {
        if (!_isSkippingForLogin) _loginSkipStreak = 0;
        _libraryService.SyncItemFlags(item);
        _currentPlayingItem = item;
        _libraryService.AddToHistory(item);

        PlayerSiteBadge.Background = item.SiteBadgeBrush;
        PlayerSiteText.Text = item.SiteDisplayName;
        DockSiteBadge.Background = item.SiteBadgeBrush;
        DockSiteText.Text = item.SiteDisplayName;
        UpdatePlayingTitles(item);

        // BGMモード＆ボトムドック用アートワーク更新
        if (!string.IsNullOrEmpty(item.ThumbnailUrl))
        {
            try
            {
                var bmp = new BitmapImage(new Uri(item.ThumbnailUrl));
                AudioOnlyThumb.Source = bmp;
                DockThumbImage.Source = bmp;
            }
            catch
            {
                AudioOnlyThumb.Source = null;
                DockThumbImage.Source = null;
            }
        }
        else
        {
            AudioOnlyThumb.Source = null;
            DockThumbImage.Source = null;
        }

        UpdatePlayerLibraryButtons();
        RefreshSideQueue();

        // 新しい動画を開いた時はA-B区間をリセット
        BtnABRepeat.Content = "📍 A-B区間ループ: OFF";
        BtnABRepeat.BorderBrush = NeutralBorderBrush;
        if (_isWebViewReady && PlayerWebView.CoreWebView2 != null)
        {
            _ = PlayerWebView.CoreWebView2.ExecuteScriptAsync("if(window.clearZenTubeABRepeat) window.clearZenTubeABRepeat();");
        }

        // Podcast や ネットラジオなど音声専用ソースの場合はプレイヤー画面で美しいジャケットデッキを自動表示
        if (item.Site == SiteType.Podcast || item.Site == SiteType.Radio)
        {
            AudioOnlyOverlay.Visibility = Visibility.Visible;
            PlayerWebView.Visibility = Visibility.Hidden;
            BtnAudioOnlyToggle.Content = "🎧 BGM: ON";
            BtnAudioOnlyToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(99, 102, 241));
        }
        else if (!_isAudioOnlyMode)
        {
            AudioOnlyOverlay.Visibility = Visibility.Collapsed;
            PlayerWebView.Visibility = Visibility.Visible;
            BtnAudioOnlyToggle.Content = "🎧 BGMモード";
            BtnAudioOnlyToggle.BorderBrush = NeutralBorderBrush;
        }

        // Musicモードで「クリック時は下部バーでBGM再生」がON、または既に一覧画面で曲送りされた場合は一覧画面のまま下部ドックバーで再生
        bool keepInBrowser = (NavMusicMode.IsChecked == true && _isMusicBgListen && PlayerView.Visibility != Visibility.Visible)
                             || (!forceZenMode && PlayerView.Visibility != Visibility.Visible);

        if (keepInBrowser)
        {
            NowPlayingMiniCard.Visibility = Visibility.Visible;
            MusicPlayerDockBar.Visibility = Visibility.Visible;
        }
        else
        {
            SwitchToPlayerView();
        }

        if (_isWebViewReady && PlayerWebView.CoreWebView2 != null)
        {
            PlayerWebView.CoreWebView2.Navigate(GetPlaybackUrl(item));
        }
        SyncCommentsWindow(item);

        _ = LoadAndInjectDanmakuForVideoAsync(item);
        _ = BuildUpNextAsync(item);
    }

    // =========================================================================
    // 💬 弾幕コメント (Danmaku) 同期取得・ローカル保存・発射制御
    // =========================================================================
    private async Task LoadAndInjectDanmakuForVideoAsync(VideoItem item)
    {
        try
        {
            // 流すのはサイト側のコメント（YouTubeコメント・ライブチャット・公式弾幕）だけ
            var combined = new List<DanmakuComment>();

            // 新しい動画のページに、前の動画のコメントが送られないように先に空にしておく
            _currentDanmakuPoolJson = "[]";

            if (item.Site == SiteType.YouTube)
            {
                DanmakuStatusText.Text = "💬 YouTubeコメント取得中...";
                var ytComments = await _commentService.GetYouTubeDanmakuAsync(item.Id, item.DurationSeconds, count =>
                {
                    if (_currentPlayingItem?.UniqueKey == item.UniqueKey)
                        DanmakuStatusText.Text = $"💬 コメント取得中... {count}件";
                });
                if (_currentPlayingItem?.UniqueKey != item.UniqueKey) return;
                combined.AddRange(ytComments);

                int timed = combined.Count(c => c.TimeSeconds >= 0);
                DanmakuStatusText.Text = combined.Count > 0
                    ? $"💬 {combined.Count}件を1回ずつ表示（時間指定 {timed}件）"
                    : "💬 ライブチャット連動中";
            }
            else if (item.Site == SiteType.Twitch || item.Site == SiteType.Abema)
            {
                DanmakuStatusText.Text = "🔴 LIVEチャット自動弾幕連動中";
            }
            else if (item.Site == SiteType.Niconico || item.Site == SiteType.Bilibili)
            {
                DanmakuStatusText.Text = "📺 公式コメント表示中";
            }
            else
            {
                DanmakuStatusText.Text = "💬 コメントなし";
            }

            var payload = combined.Select(c => new
            {
                timeSeconds = c.TimeSeconds,
                text = c.Text,
                color = c.Color,
                isUser = c.IsUser
            }).ToList();

            _currentDanmakuPoolJson = JsonSerializer.Serialize(payload);
            await InjectCurrentDanmakuPoolAsync();
        }
        catch
        {
            DanmakuStatusText.Text = "💬 弾幕スタンバイ";
        }
    }

    private async Task InjectCurrentDanmakuPoolAsync()
    {
        if (!_isWebViewReady || PlayerWebView.CoreWebView2 == null) return;
        try
        {
            await PlayerWebView.CoreWebView2.ExecuteScriptAsync(
                $"if (window.loadZenTubeDanmakuPool) window.loadZenTubeDanmakuPool({_currentDanmakuPoolJson});");
        }
        catch { }
    }

    private async void DanmakuStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || DanmakuStyleCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string style)
        {
            _danmakuStyle = style;
            await ApplyZenModeToPlayerAsync();
        }
    }

    private void UpdatePlayingTitles(VideoItem item)
    {
        PlayerTitleText.Text = item.Title;
        MiniNowPlayingTitle.Text = $"[{item.SiteDisplayName}] {item.Title}";
        MiniBarTitleText.Text = $"[{item.SiteDisplayName}] {item.Title}";
        AudioOnlyTitle.Text = item.Title;
        AudioOnlyAuthor.Text = $"{item.SiteDisplayName} • {item.Author}";
        DockTitleText.Text = item.Title;
        DockAuthorText.Text = item.Author;
    }

    private void UpdatePlayerLibraryButtons()
    {
        if (_currentPlayingItem == null) return;
        _libraryService.SyncItemFlags(_currentPlayingItem);

        BtnPlayerSubscribe.Content = _currentPlayingItem.IsChannelSubscribed ? "★ 登録済" : "☆ 登録";
        BtnPlayerSubscribe.Foreground = _currentPlayingItem.SubscribeForeground;

        BtnPlayerFavorite.Content = _currentPlayingItem.IsFavorite ? "♥" : "♡";
        BtnPlayerFavorite.Foreground = _currentPlayingItem.FavoriteForeground;

        BtnDockFavorite.Content = _currentPlayingItem.IsFavorite ? "♥" : "♡";
        BtnDockFavorite.Foreground = _currentPlayingItem.FavoriteForeground;

        BtnPlayerWatchLater.Content = _currentPlayingItem.IsWatchLater ? "✓後で" : "+後で";
        BtnPlayerWatchLater.Foreground = _currentPlayingItem.WatchLaterForeground;
    }

    private void UpdateAdBlockButtonUI()
    {
        // 設定画面の「広告ブロック」の説明に、止めた数を出す
        SetAdBlock.IsChecked = _adBlockService.IsEnabled;
        SetAdBlockDesc.Text = _adBlockService.IsEnabled
            ? $"広告やトラッカーを止め、動画の広告を自動で飛ばす（今回 {_adBlockService.BlockedCount} 件ブロック）"
            : "広告やトラッカーを止め、動画の広告を自動で飛ばす（いまはOFF）";
    }

    private void PlayNextInQueue(int step = 1)
    {
        // 「次に再生」の時の「前へ」は、ひとつ前に見た動画に戻る（キューは動画ごとに作り直すため）
        if (step < 0 && _activeQueueTab == "Current" && _libraryService.History.Count > 1)
        {
            PlayVideo(_libraryService.History[1], forceZenMode: false);
            return;
        }

        if (SideQueueItemsControl.ItemsSource is not IList<VideoItem> list || list.Count == 0) return;

        int idx = -1;
        if (_currentPlayingItem != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].UniqueKey == _currentPlayingItem.UniqueKey)
                {
                    idx = i;
                    break;
                }
            }
        }

        int nextIdx;
        if (_isShuffleMode && list.Count > 1)
        {
            var rnd = Random.Shared;
            do
            {
                nextIdx = rnd.Next(list.Count);
            } while (nextIdx == idx);
        }
        else
        {
            nextIdx = (idx + step + list.Count) % list.Count;
        }

        if (nextIdx >= 0 && nextIdx < list.Count)
        {
            PlayVideo(list[nextIdx], forceZenMode: false);
        }
    }

    private void SwitchToPlayerView()
    {
        CardBrowserView.Visibility = Visibility.Collapsed;
        StatsDashboardView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        PlayerView.Visibility = Visibility.Visible;
        NowPlayingMiniCard.Visibility = Visibility.Collapsed;
        MusicPlayerDockBar.Visibility = Visibility.Collapsed;
        BtnToggleViewMode.Content = "🗂 一覧へ";
    }

    private void SwitchToCardBrowserView()
    {
        if (_isMiniPlayerMode)
            SetMiniPlayerMode(false);

        PlayerView.Visibility = Visibility.Collapsed;
        StatsDashboardView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        CardBrowserView.Visibility = Visibility.Visible;
        BtnToggleViewMode.Content = "🎬 プレイヤー";

        if (_currentPlayingItem != null)
        {
            NowPlayingMiniCard.Visibility = Visibility.Visible;
            MusicPlayerDockBar.Visibility = Visibility.Visible;
        }
    }

    private void SwitchToStatsView()
    {
        if (_isMiniPlayerMode)
            SetMiniPlayerMode(false);

        PlayerView.Visibility = Visibility.Collapsed;
        CardBrowserView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        StatsDashboardView.Visibility = Visibility.Visible;
        BtnToggleViewMode.Content = "🎬 プレイヤー";

        if (_currentPlayingItem != null)
        {
            NowPlayingMiniCard.Visibility = Visibility.Visible;
            MusicPlayerDockBar.Visibility = Visibility.Visible;
        }

        RefreshStatsDashboard();
    }

    // =========================================================================
    // カスタムプレイリスト機能
    // =========================================================================
    private void PlaylistNav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.DataContext is CustomPlaylist pl)
        {
            LeaveSiteMode();
            _activeCustomPlaylist = pl;
            _activeSubscribedChannel = null;
            SwitchToCardBrowserView();
            RefreshDisplayedCards();
        }
    }

    private async void ChannelNav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.DataContext is SubscribedChannel ch)
        {
            await OpenChannelViewAsync(ch);
        }
    }

    private async Task OpenChannelViewAsync(SubscribedChannel ch)
    {
        LeaveSiteMode();
        _activeCustomPlaylist = null;
        _activeSubscribedChannel = ch;
        SwitchToCardBrowserView();

        ShowLoadingOverlay(
            $"「{ch.Name}」の動画を取得中...",
            $"{ch.Site} から投稿動画を読み込んでいます");

        var items = await _searchService.GetChannelVideosAsync(ch, limit: 24);
        _libraryService.SyncCollectionFlags(items);
        _channelItems = items;

        HideLoadingOverlay();
        RefreshDisplayedCards();
    }

    private async Task LoadSubscriptionsFeedAsync()
    {
        if (_libraryService.Subscriptions.Count == 0)
        {
            _subscriptionFeedItems = [];
            RefreshDisplayedCards();
            return;
        }

        ShowLoadingOverlay(
            "登録チャンネルの新着・人気動画を集計中...",
            $"登録中の {_libraryService.Subscriptions.Count} チャンネルから横断取得しています");

        var items = await _searchService.GetSubscriptionsFeedAsync(_libraryService.Subscriptions, limitPerChannel: 6);
        _libraryService.SyncCollectionFlags(items);
        _subscriptionFeedItems = items;

        HideLoadingOverlay();
        RefreshDisplayedCards();
    }

    private void NewPlaylistNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        NewPlaylistHint.Visibility = string.IsNullOrEmpty(NewPlaylistNameBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NewPlaylistNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CreateNewPlaylistFromInput();
    }

    private void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        CreateNewPlaylistFromInput();
    }

    private void CreateNewPlaylistFromInput()
    {
        var name = NewPlaylistNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!name.StartsWith("📁") && !name.StartsWith("🎵") && !name.StartsWith("✨"))
            name = "📁 " + name;

        var created = _libraryService.CreatePlaylist(name);
        NewPlaylistNameBox.Text = "";
        _activeCustomPlaylist = created;
        SwitchToCardBrowserView();
        RefreshDisplayedCards();
    }

    private void PlayAllPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCustomPlaylist == null || _activeCustomPlaylist.Items.Count == 0) return;
        _isAutoNext = true;
        BtnAutoNextToggle.Content = "⏭ 連続: ON";
        _activeQueueTab = "Current";
        RefreshSideQueue();
        PlayVideo(_activeCustomPlaylist.Items[0]);
    }

    private void DeleteCurrentPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCustomPlaylist == null) return;
        _libraryService.DeletePlaylist(_activeCustomPlaylist);
        _activeCustomPlaylist = null;
        NavHome.IsChecked = true;
        RefreshDisplayedCards();
    }

    private void AddToPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VideoItem item)
        {
            ShowPlaylistPickerMenu(btn, item);
        }
    }

    private void PlayerAddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && _currentPlayingItem != null)
        {
            ShowPlaylistPickerMenu(btn, _currentPlayingItem);
        }
    }

    private void ShowPlaylistPickerMenu(Button targetButton, VideoItem item)
    {
        var menu = new ContextMenu();

        foreach (var pl in _libraryService.Playlists)
        {
            bool exists = _libraryService.ContainsInPlaylist(pl, item);
            var mi = new MenuItem
            {
                Header = exists ? $"✓  {pl.Name} ({pl.Items.Count})" : $"    {pl.Name} ({pl.Items.Count})"
            };
            mi.Click += (_, _) =>
            {
                _libraryService.ToggleItemInPlaylist(pl, item);
                if (_activeCustomPlaylist == pl)
                    RefreshDisplayedCards();
            };
            menu.Items.Add(mi);
        }

        menu.PlacementTarget = targetButton;
        menu.IsOpen = true;
    }

    // =========================================================================
    // 🎧 BGM（音声のみ）モード ＆ 🌙 スリープタイマー ＆ 🗗 超小型ミニプレイヤー
    // =========================================================================
    private void AudioOnlyToggle_Click(object sender, RoutedEventArgs e)
    {
        _isAudioOnlyMode = !_isAudioOnlyMode;
        if (_isAudioOnlyMode)
        {
            AudioOnlyOverlay.Visibility = Visibility.Visible;
            PlayerWebView.Visibility = Visibility.Hidden;
            BtnAudioOnlyToggle.Content = "🎧 BGM: ON";
            BtnAudioOnlyToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(99, 102, 241));
        }
        else
        {
            AudioOnlyOverlay.Visibility = Visibility.Collapsed;
            PlayerWebView.Visibility = Visibility.Visible;
            BtnAudioOnlyToggle.Content = "🎧 BGMモード";
            BtnAudioOnlyToggle.BorderBrush = NeutralBorderBrush;
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isWebViewReady || PlayerWebView.CoreWebView2 == null) return;
        try
        {
            await PlayerWebView.CoreWebView2.ExecuteScriptAsync("if(window.toggleZenTubePlayPause) window.toggleZenTubePlayPause();");
        }
        catch { }
    }

    private void NextTrackButton_Click(object sender, RoutedEventArgs e)
    {
        PlayNextInQueue(step: 1);
    }

    private void PrevTrackButton_Click(object sender, RoutedEventArgs e)
    {
        PlayNextInQueue(step: -1);
    }

    private void SleepTimerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SleepTimerCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string tagStr && int.TryParse(tagStr, out int minutes))
        {
            if (minutes <= 0)
            {
                _sleepTimer.Stop();
                _sleepRemainingSeconds = 0;
                SleepTimerOffItem.Content = "🌙 タイマー: OFF";
            }
            else
            {
                _sleepRemainingSeconds = minutes * 60;
                _sleepTimer.Start();
                UpdateSleepTimerDisplay();
            }
        }
    }

    private void SleepTimer_Tick(object? sender, EventArgs e)
    {
        if (_sleepRemainingSeconds <= 0)
        {
            _sleepTimer.Stop();
            SleepTimerCombo.SelectedIndex = 0;
            SleepTimerOffItem.Content = "🌙 タイマー: OFF";
            StopPlayback_Click(this, new RoutedEventArgs());
            SwitchToCardBrowserView();
            ShowEmptyState("🌙 スリープタイマーにより再生を停止しました", "おやすみなさい！再開するには動画カードをクリックしてください");
            return;
        }

        _sleepRemainingSeconds--;
        UpdateSleepTimerDisplay();
    }

    private void UpdateSleepTimerDisplay()
    {
        int m = _sleepRemainingSeconds / 60;
        int s = _sleepRemainingSeconds % 60;
        if (SleepTimerCombo.SelectedItem is ComboBoxItem selected && selected != SleepTimerOffItem)
        {
            selected.Content = $"🌙 残り {m:D2}:{s:D2}";
        }
    }

    private void MiniPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        SetMiniPlayerMode(!_isMiniPlayerMode);
    }

    private void MiniPlayerTopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void SetMiniPlayerMode(bool enable)
    {
        if (enable == _isMiniPlayerMode) return;
        if (_isFullScreen) SetFullScreen(false);

        _isMiniPlayerMode = enable;

        if (enable)
        {
            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevLeft = Left;
            _prevTop = Top;
            _prevWidth = Width;
            _prevHeight = Height;
            _prevTopmost = Topmost;

            SwitchToPlayerView();

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            Topmost = true;

            MinWidth = 280;
            MinHeight = 180;
            Width = 420;
            Height = 268;

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 20;

            SidebarColumn.Width = new GridLength(0);
            HeaderRow.Height = new GridLength(0);
            PlayerControlBarRow.Height = new GridLength(0);
            SideQueueColumn.Width = new GridLength(0);
            MiniPlayerBarRow.Height = new GridLength(28);
        }
        else
        {
            MiniPlayerBarRow.Height = new GridLength(0);
            ApplyPlayerLayout(); // シアターモードだった時はシアターの形に戻る

            MinWidth = 820;
            MinHeight = 520;
            WindowStyle = _prevWindowStyle;
            Width = _prevWidth > 800 ? _prevWidth : 1440;
            Height = _prevHeight > 500 ? _prevHeight : 880;
            Left = _prevLeft;
            Top = _prevTop;
            WindowState = _prevWindowState;
            Topmost = _prevTopmost;
        }
    }

    // =========================================================================
    // その他 UI イベントハンドラ
    // =========================================================================
    private async void MainNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _activeCustomPlaylist = null;
        _activeSubscribedChannel = null;
        // トレンド・検索以外（お気に入り・履歴・Musicモードなど）へ移ったら「サイト別に見る」モードを抜ける
        if (sender != NavHome && sender != NavSearch) LeaveSiteMode();

        if (NavSettings.IsChecked == true)
        {
            SwitchToSettingsView();
            return;
        }

        if (NavStats.IsChecked == true)
        {
            SwitchToStatsView();
            return;
        }

        SwitchToCardBrowserView();

        // 設定画面で読み込むサイトを変えていたら、離れる時に1回だけ読み込み直す
        if (_siteSelectionChanged)
        {
            _siteSelectionChanged = false;
            await ReloadAfterSiteChangeAsync();
            if (NavHome.IsChecked == true || NavSearch.IsChecked == true ||
                NavMusicMode.IsChecked == true || NavSubscriptionsFeed.IsChecked == true)
                return;
        }

        if (NavMusicMode.IsChecked == true)
        {
            if (_musicItems.Count == 0)
                await LoadMusicFeedAsync("");
            else
                RefreshDisplayedCards();
        }
        else if (NavSubscriptionsFeed.IsChecked == true)
        {
            await LoadSubscriptionsFeedAsync();
        }
        else
        {
            if (await ResearchIfSiteChangedAsync() || await ReloadHomeIfSiteChangedAsync()) return;
            RefreshDisplayedCards();
        }
    }

    // =========================================================================
    // ⚙ 設定画面（変えた設定は library.json に保存し、次の起動時に戻す）
    // =========================================================================
    private bool _applyingSettings; // 起動時に設定を戻している間は保存しない

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        MoreMenuPopup.IsOpen = false;
        if (_isFullScreen) SetFullScreen(false);
        if (_isTheaterMode) SetTheaterMode(false);
        if (NavSettings.IsChecked == true) SwitchToSettingsView();
        else NavSettings.IsChecked = true;
    }

    private void SwitchToSettingsView()
    {
        if (_isMiniPlayerMode) SetMiniPlayerMode(false);
        PlayerView.Visibility = Visibility.Collapsed;
        CardBrowserView.Visibility = Visibility.Collapsed;
        StatsDashboardView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        BtnToggleViewMode.Content = "🎬 プレイヤー";
        if (_currentPlayingItem != null)
        {
            NowPlayingMiniCard.Visibility = Visibility.Visible;
            MusicPlayerDockBar.Visibility = Visibility.Visible;
        }
        RefreshSettingsView();
    }

    /// <summary>設定画面のスイッチなどを、今の状態に合わせる</summary>
    private void RefreshSettingsView()
    {
        SetAutoNext.IsChecked = _isAutoNext;
        SetShuffle.IsChecked = _isShuffleMode;
        SetMusicBgListen.IsChecked = _isMusicBgListen;
        SetTopmost.IsChecked = Topmost;
        _applyingSettings = true;
        SetShortsCombo.SelectedIndex = _shortsFilterMode;
        _applyingSettings = false;
        UpdateAdBlockButtonUI();
        SetHistoryDesc.Text = _libraryService.History.Count > 0
            ? $"このPCに残っている視聴履歴（{_libraryService.History.Count}件）をすべて消す"
            : "視聴履歴はありません";
    }

    /// <summary>設定画面のON/OFFスイッチ：それぞれ今までのボタンと同じ処理を呼ぶ</summary>
    private void SettingSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        bool want = cb.IsChecked == true;
        if (cb == SetAutoNext && want != _isAutoNext) AutoNextToggle_Click(cb, e);
        else if (cb == SetShuffle && want != _isShuffleMode) ShuffleToggle_Click(cb, e);
        else if (cb == SetMusicBgListen && want != _isMusicBgListen) MusicBgListenToggle_Click(cb, e);
        else if (cb == SetAdBlock && want != _adBlockService.IsEnabled) AdBlockToggle_Click(cb, e);
        else if (cb == SetTopmost && want != Topmost) PinTopmost_Click(cb, e);
    }

    private void ClearHistorySetting_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryService.History.Count == 0) return;
        var ok = MessageBox.Show($"視聴履歴（{_libraryService.History.Count}件）をすべて消しますか？元には戻せません。",
            "Prismil", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        _libraryService.ClearHistory();
        RefreshSettingsView();
    }

    /// <summary>今の設定を保存する</summary>
    private void SaveSettings()
    {
        if (_applyingSettings) return;
        var s = _libraryService.Settings;
        s.SpeedIndex = SpeedCombo.SelectedIndex;
        s.AutoNext = _isAutoNext;
        s.Shuffle = _isShuffleMode;
        s.MusicBgListen = _isMusicBgListen;
        s.AmbientIndex = AmbilightCombo.SelectedIndex;
        s.GainIndex = AudioGainCombo.SelectedIndex;
        s.PresetIndex = AudioPresetCombo.SelectedIndex;
        s.DanmakuIndex = DanmakuStyleCombo.SelectedIndex;
        s.ShortsMode = _shortsFilterMode;
        s.SortIndex = SortCombo.SelectedIndex;
        s.AdBlock = _adBlockService.IsEnabled;
        s.Topmost = Topmost;
        _libraryService.Save();
    }

    /// <summary>起動時：保存してある設定に戻す（画面の部品は今までと同じ処理を通して変える）</summary>
    private void ApplySavedSettings()
    {
        var s = _libraryService.Settings;
        _applyingSettings = true;
        try
        {
            static void Pick(ComboBox cb, int index)
            {
                if (index >= 0 && index < cb.Items.Count) cb.SelectedIndex = index;
            }
            Pick(SpeedCombo, s.SpeedIndex);
            Pick(AmbilightCombo, s.AmbientIndex);
            Pick(AudioGainCombo, s.GainIndex);
            Pick(AudioPresetCombo, s.PresetIndex);
            Pick(DanmakuStyleCombo, s.DanmakuIndex);
            Pick(SortCombo, s.SortIndex);

            if (s.AutoNext != _isAutoNext) AutoNextToggle_Click(this, new RoutedEventArgs());
            if (s.Shuffle != _isShuffleMode) ShuffleToggle_Click(this, new RoutedEventArgs());
            if (s.MusicBgListen != _isMusicBgListen) MusicBgListenToggle_Click(this, new RoutedEventArgs());
            if (s.Topmost != Topmost) PinTopmost_Click(this, new RoutedEventArgs());
            _shortsFilterMode = Math.Clamp(s.ShortsMode, 0, 2);
            _adBlockService.IsEnabled = s.AdBlock;
            UpdateAdBlockButtonUI();
        }
        finally
        {
            _applyingSettings = false;
        }

        // ここから先、⚙メニューや設定画面で選び直した時も保存する
        foreach (var cb in new[] { SpeedCombo, AmbilightCombo, AudioGainCombo, AudioPresetCombo, DanmakuStyleCombo, SortCombo })
            cb.SelectionChanged += (_, _) => SaveSettings();
    }

    private void MusicModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (NavMusicMode.IsChecked == true)
            NavHome.IsChecked = true;
        else
            NavMusicMode.IsChecked = true;
    }

    private async void MusicStation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string station = btn.Tag as string ?? "";
        FilterAll.IsChecked = true;
        NavMusicMode.IsChecked = true;
        SwitchToCardBrowserView();
        await LoadMusicFeedAsync(station);
    }

    private async void MusicStationSoundCloud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string station = btn.Tag as string ?? "";
        NavMusicMode.IsChecked = true;
        SwitchToCardBrowserView();
        await LoadMusicFeedAsync(station);
        FilterSoundCloud.IsChecked = true;
        RefreshDisplayedCards();
    }

    private void MusicBgListenToggle_Click(object sender, RoutedEventArgs e)
    {
        _isMusicBgListen = !_isMusicBgListen;
        BtnMusicBgListenToggle.Content = _isMusicBgListen
            ? "🎧 クリック時は下部バーでBGM再生: ON"
            : "🎬 クリック時はMVプレイヤー画面を開く";
        BtnMusicBgListenToggle.BorderBrush = _isMusicBgListen
            ? new SolidColorBrush(Color.FromRgb(236, 72, 153))
            : new SolidColorBrush(Color.FromRgb(99, 102, 241));
        SetMusicBgListen.IsChecked = _isMusicBgListen;
        SaveSettings();
    }

    private async void DockEqPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetPreset) return;
        _audioPreset = _audioPreset == targetPreset ? "normal" : targetPreset;

        // Player側のComboBoxとも同期
        for (int i = 0; i < AudioPresetCombo.Items.Count; i++)
        {
            if (AudioPresetCombo.Items[i] is ComboBoxItem cbi && (cbi.Tag as string) == _audioPreset)
            {
                AudioPresetCombo.SelectedIndex = i;
                break;
            }
        }

        UpdateDockEqButtonsUI();
        await ApplyZenModeToPlayerAsync();
    }

    private void UpdateDockEqButtonsUI()
    {
        if (BtnDockEqBass == null || BtnDockEqVocal == null) return;
        BtnDockEqBass.BorderBrush = _audioPreset == "bass"
            ? new SolidColorBrush(Color.FromRgb(236, 72, 153))
            : NeutralBorderBrush;
        BtnDockEqVocal.BorderBrush = _audioPreset == "vocal"
            ? new SolidColorBrush(Color.FromRgb(236, 72, 153))
            : NeutralBorderBrush;
    }

    private void SwitchToPlayer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        SwitchToPlayerView();
    }

    // 「サイト別に見る」を画面の切り替えに合わせて黙って戻している最中か（その時は読み込み直さない）
    private bool _silentSiteChange;

    /// <summary>
    /// 「サイト別に見る」は、そのサイトの動画を見るためのモード。
    /// お気に入り・履歴・プレイリストなど別の画面へ移ったら、モードを抜けて「すべてのサイト」に戻す。
    /// </summary>
    private void LeaveSiteMode()
    {
        if (FilterAll.IsChecked == true) return;
        _silentSiteChange = true;
        try
        {
            FilterAll.IsChecked = true;
            SyncSearchSiteComboFromFilter();
        }
        finally { _silentSiteChange = false; }
    }

    private async void SiteFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _silentSiteChange) return;
        SyncSearchSiteComboFromFilter();
        // 検索結果を表示中なら、そのサイト（または全サイト）で検索し直す
        if (await ResearchIfSiteChangedAsync()) return;
        // Musicモードの中では、今まで通り曲の一覧をサイトで絞り込むだけ
        if (NavMusicMode.IsChecked == true)
        {
            RefreshDisplayedCards();
            return;
        }

        // サイトを選んだら、どの画面にいても、そのサイトのおすすめ動画の画面を開く
        bool onHome = NavHome.IsChecked == true && _activeCustomPlaylist == null && _activeSubscribedChannel == null;
        if (SelectedSiteFilter() != null && !onHome)
        {
            _activeCustomPlaylist = null;
            _activeSubscribedChannel = null;
            _suppressHomeAutoReload = true;
            try { NavHome.IsChecked = true; }
            finally { _suppressHomeAutoReload = false; }
            SwitchToCardBrowserView();
            await LoadDiscoverFeedAsync("");
            return;
        }

        if (await ReloadHomeIfSiteChangedAsync()) return;
        RefreshDisplayedCards();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshDisplayedCards();
    }

    private async void TopicChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string keyword = btn.Tag as string ?? "";
        _activeCustomPlaylist = null;
        _activeSubscribedChannel = null;
        _suppressHomeAutoReload = true;
        try
        {
            FilterAll.IsChecked = true;
            NavHome.IsChecked = true;
        }
        finally { _suppressHomeAutoReload = false; }
        SwitchToCardBrowserView();
        await LoadDiscoverFeedAsync(keyword);
    }

    private async void QuickSiteFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string siteName) return;
        _activeCustomPlaylist = null;
        _activeSubscribedChannel = null;
        _suppressHomeAutoReload = true;
        try
        {
            NavHome.IsChecked = true;
            if (siteName == "Twitch") FilterTwitch.IsChecked = true;
            else if (siteName == "Abema") FilterAbema.IsChecked = true;
            else if (siteName == "SoundCloud") FilterSoundCloud.IsChecked = true;
        }
        finally { _suppressHomeAutoReload = false; }
        SwitchToCardBrowserView();

        // そのサイトのおすすめを読み込む（すでに表示中ならそのまま）
        if (_homeSite != SelectedSiteFilter() || _discoverItems.Count == 0 || !string.IsNullOrEmpty(_lastTopicKeyword))
            await LoadDiscoverFeedAsync("");
        else
            RefreshDisplayedCards();
    }

    private async void RecentSearchChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string kw && !string.IsNullOrEmpty(kw))
        {
            SearchBox.Text = kw;
            await ExecuteSearchAsync(kw);
        }
    }

    private async void AudioFxCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AudioGainCombo.SelectedItem is ComboBoxItem gainItem && gainItem.Tag is string gStr &&
            double.TryParse(gStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gain))
        {
            _audioGain = gain;
        }
        if (AudioPresetCombo.SelectedItem is ComboBoxItem presetItem && presetItem.Tag is string pStr)
        {
            _audioPreset = pStr;
            UpdateDockEqButtonsUI();
        }
        if (AmbilightCombo != null && AmbilightCombo.SelectedItem is ComboBoxItem ambiItem && ambiItem.Tag is string aStr)
        {
            _ambilightMode = aStr;
            if (_ambilightMode == "off")
                ResetNativeAmbilightTint();
        }
        await ApplyZenModeToPlayerAsync();
    }

    private void ApplyNativeAmbilightTint(byte r, byte g, byte b)
    {
        // 抽出した映像色をダークテーマに馴染むようブレンドしてツールバーをほんのり発光させる
        byte bgR = (byte)Math.Clamp(14 + r * 0.18, 14, 75);
        byte bgG = (byte)Math.Clamp(17 + g * 0.18, 17, 75);
        byte bgB = (byte)Math.Clamp(26 + b * 0.18, 26, 85);

        byte bdR = (byte)Math.Clamp(34 + r * 0.42, 34, 180);
        byte bdG = (byte)Math.Clamp(40 + g * 0.42, 40, 180);
        byte bdB = (byte)Math.Clamp(58 + b * 0.42, 58, 195);

        PlayerHeaderBar.Background = new SolidColorBrush(Color.FromArgb(0xB0, bgR, bgG, bgB));
        PlayerHeaderBar.BorderBrush = new SolidColorBrush(Color.FromRgb(bdR, bdG, bdB));
    }

    private static readonly Brush PlayerHeaderDefaultBrush = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));

    private void ResetNativeAmbilightTint()
    {
        PlayerHeaderBar.Background = PlayerHeaderDefaultBrush;
        PlayerHeaderBar.BorderBrush = NeutralBorderBrush;
        FadeOutAmbient();
    }

    // =========================================================================
    // 画面全体のアンビエントライト（動画の色をアプリ全体の背景ににじませる）
    // =========================================================================
    private DateTime _lastAmbientColorAt;
    private DispatcherTimer? _ambientWatchdog;

    /// <summary>"r,g,b;r,g,b;r,g,b"（左・全体・右）を受け取って背景の色を動かす</summary>
    private void OnAmbientColors(string payload)
    {
        var parts = payload.Split(';');
        if (parts.Length != 3) return;
        if (!TryParseRgb(parts[0], out var left) || !TryParseRgb(parts[1], out var center) || !TryParseRgb(parts[2], out var right))
            return;

        // 色が変わるたびに 0.8 秒かけてじわっと切り替える
        var dur = TimeSpan.FromMilliseconds(800);
        AnimateStop(AmbientStopLeft, ToAmbientTone(left), dur);
        AnimateStop(AmbientStopCenter, ToAmbientTone(center), dur);
        AnimateStop(AmbientStopRight, ToAmbientTone(right), dur);

        double target = _ambilightMode switch
        {
            "soft" => 0.35,
            "ultra" => 0.85,
            _ => 0.6, // vivid（標準）
        };
        // 表示し始めた時と、強さ（標準/強/ソフト）を変えた時だけ明るさを動かす
        if (!_ambientVisible || Math.Abs(_ambientTargetOpacity - target) > 0.01)
        {
            _ambientVisible = true;
            _ambientTargetOpacity = target;
            AmbientLayer.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(600)));
        }

        // 元からあるプレイヤーのバーの色付けも続ける
        ApplyNativeAmbilightTint(center.R, center.G, center.B);

        _lastAmbientColorAt = DateTime.Now;
        EnsureAmbientWatchdog();

        // 映像が再生されていない（停止・別ページ）まま 2.5 秒色が届かなければ、ふわっと消す
        void EnsureAmbientWatchdog()
        {
            if (_ambientWatchdog != null) return;
            _ambientWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ambientWatchdog.Tick += (_, _) =>
            {
                if ((DateTime.Now - _lastAmbientColorAt).TotalSeconds > 2.5) FadeOutAmbient();
            };
            _ambientWatchdog.Start();
        }
    }

    private bool _ambientVisible;
    private double _ambientTargetOpacity;

    private void FadeOutAmbient()
    {
        if (!_ambientVisible) return;
        _ambientVisible = false;
        AmbientLayer.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(1200)));
    }

    private static void AnimateStop(GradientStop stop, Color to, TimeSpan dur) =>
        stop.BeginAnimation(GradientStop.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(to, dur)
            { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() });

    private static bool TryParseRgb(string s, out Color c)
    {
        c = default;
        var p = s.Split(',');
        if (p.Length != 3 || !byte.TryParse(p[0], out var r) || !byte.TryParse(p[1], out var g) || !byte.TryParse(p[2], out var b))
            return false;
        c = Color.FromRgb(r, g, b);
        return true;
    }

    /// <summary>
    /// 動画の色を背景向けに整える：鮮やかさを少し上げ、明るさは文字が読める範囲に収める。
    /// （真っ白な場面でも画面が白くならず、真っ暗な場面でも色が消えすぎない）
    /// </summary>
    private static Color ToAmbientTone(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2;
        double d = max - min;
        if (d < 1e-6) s = 0;
        else
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }

        s = Math.Min(1, s * 1.4);
        l = Math.Clamp(l, 0.18, 0.45);

        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        double q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p2 = 2 * l - q2;
        return Color.FromRgb(
            (byte)Math.Round(Hue(p2, q2, h + 1.0 / 3) * 255),
            (byte)Math.Round(Hue(p2, q2, h) * 255),
            (byte)Math.Round(Hue(p2, q2, h - 1.0 / 3) * 255));
    }

    private async void ABRepeatButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isWebViewReady || PlayerWebView.CoreWebView2 == null) return;
        try
        {
            await PlayerWebView.CoreWebView2.ExecuteScriptAsync("if(window.stepZenTubeABRepeat) window.stepZenTubeABRepeat();");
        }
        catch { }
    }

    private void ShuffleToggle_Click(object sender, RoutedEventArgs e)
    {
        _isShuffleMode = !_isShuffleMode;
        BtnShuffleToggle.Content = _isShuffleMode ? "🔀 シャッフル: ON" : "🔀 シャッフル: OFF";
        var brush = _isShuffleMode
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        BtnShuffleToggle.BorderBrush = brush;
        BtnDockShuffle.BorderBrush = brush;
        SetShuffle.IsChecked = _isShuffleMode;
        SaveSettings();
    }

    private async void MirrorToggle_Click(object sender, RoutedEventArgs e)
    {
        _isMirrorMode = !_isMirrorMode;
        BtnMirrorToggle.Content = _isMirrorMode ? "🪞 左右反転: ON" : "🪞 左右反転: OFF";
        BtnMirrorToggle.BorderBrush = _isMirrorMode
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        await ApplyZenModeToPlayerAsync();
    }

    private async void DimToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDimMode = !_isDimMode;
        BtnDimToggle.Content = _isDimMode ? "🕶 減光フィルター: ON" : "🕶 減光フィルター: OFF";
        BtnDimToggle.BorderBrush = _isDimMode
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        await ApplyZenModeToPlayerAsync();
    }

    private async void RefreshFeed_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSubscribedChannel != null)
        {
            await OpenChannelViewAsync(_activeSubscribedChannel);
        }
        else if (NavMusicMode.IsChecked == true)
        {
            await LoadMusicFeedAsync(_lastMusicStation);
        }
        else if (NavSubscriptionsFeed.IsChecked == true)
        {
            await LoadSubscriptionsFeedAsync();
        }
        else if (NavSearch.IsChecked == true && !string.IsNullOrEmpty(_lastSearchQuery))
        {
            await ExecuteSearchAsync(_lastSearchQuery);
        }
        else
        {
            _activeCustomPlaylist = null;
            _activeSubscribedChannel = null;
            NavHome.IsChecked = true;
            await LoadDiscoverFeedAsync(_lastTopicKeyword);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _libraryService.ClearHistory();
        RefreshDisplayedCards();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBox.SelectAll();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ExecuteSearchAsync(SearchBox.Text);
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteSearchAsync(SearchBox.Text);
    }

    private void PlayVideoCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is VideoItem item)
        {
            PlayVideo(item);
        }
    }

    private void VideoTitle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is VideoItem item)
        {
            PlayVideo(item);
        }
    }

    private async void AuthorName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is VideoItem item && !string.IsNullOrWhiteSpace(item.Author))
        {
            var ch = new SubscribedChannel
            {
                Name = item.Author.TrimStart('#'),
                Site = item.Site,
                AvatarUrl = item.ThumbnailUrl
            };
            await OpenChannelViewAsync(ch);
        }
    }

    private void ToggleSubscribeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is VideoItem item)
        {
            _libraryService.ToggleSubscription(item);
            _libraryService.SyncCollectionFlags(_discoverItems);
            _libraryService.SyncCollectionFlags(_musicItems);
            _libraryService.SyncCollectionFlags(_searchItems);
            _libraryService.SyncCollectionFlags(_subscriptionFeedItems);
            _libraryService.SyncCollectionFlags(_channelItems);
            UpdatePlayerLibraryButtons();
            RefreshDisplayedCards();
        }
    }

    private void PlayerToggleSubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayingItem == null) return;
        _libraryService.ToggleSubscription(_currentPlayingItem);
        _libraryService.SyncCollectionFlags(_discoverItems);
        _libraryService.SyncCollectionFlags(_searchItems);
        _libraryService.SyncCollectionFlags(_subscriptionFeedItems);
        _libraryService.SyncCollectionFlags(_channelItems);
        UpdatePlayerLibraryButtons();
    }

    private void UnsubscribeCurrentChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSubscribedChannel == null) return;
        var existing = _libraryService.Subscriptions.FirstOrDefault(x => x.UniqueKey == _activeSubscribedChannel.UniqueKey);
        if (existing != null)
        {
            _libraryService.UnsubscribeChannel(existing);
            _libraryService.SyncCollectionFlags(_discoverItems);
            _libraryService.SyncCollectionFlags(_musicItems);
            _libraryService.SyncCollectionFlags(_searchItems);
            _libraryService.SyncCollectionFlags(_subscriptionFeedItems);
            _libraryService.SyncCollectionFlags(_channelItems);
        }
        _activeSubscribedChannel = null;
        NavSubscriptionsFeed.IsChecked = true;
        RefreshDisplayedCards();
    }

    private void ToggleFavoriteCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is VideoItem item)
        {
            _libraryService.ToggleFavorite(item);
            UpdatePlayerLibraryButtons();
            if (NavFavorites.IsChecked == true)
                RefreshDisplayedCards();
            else
                RefreshSideQueue();
        }
    }

    private void ToggleWatchLaterCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is VideoItem item)
        {
            _libraryService.ToggleWatchLater(item);
            UpdatePlayerLibraryButtons();
            if (NavWatchLater.IsChecked == true)
                RefreshDisplayedCards();
            else
                RefreshSideQueue();
        }
    }

    private void PlayerToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayingItem == null) return;
        _libraryService.ToggleFavorite(_currentPlayingItem);
        UpdatePlayerLibraryButtons();
        RefreshSideQueue();
    }

    private void PlayerToggleWatchLater_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayingItem == null) return;
        _libraryService.ToggleWatchLater(_currentPlayingItem);
        UpdatePlayerLibraryButtons();
        RefreshSideQueue();
    }

    /// <summary>
    /// 再生に使うURL。対応サイトは独自プレイヤー、未対応のもの（ABEMA など）はサイトの画面から
    /// プレイヤー部分だけを抜き出して表示する。
    /// </summary>
    private static string GetPlaybackUrl(VideoItem item) =>
        PrismilPlayer.BuildPlayerUrl(item) ?? item.WatchUrl;

    // =========================================================================
    // 💬 コメント欄ウィンドウ（サイトのページを別ウィンドウで開く。動画はそのまま再生を続ける）
    // =========================================================================
    private CommentsWindow? _commentsWindow;

    private static bool HasCommentsPage(VideoItem item) =>
        item.Site != SiteType.Podcast && item.Site != SiteType.Radio;

    private void OpenComments_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayingItem == null || _webViewEnv == null || !HasCommentsPage(_currentPlayingItem)) return;

        if (_commentsWindow == null)
        {
            _commentsWindow = new CommentsWindow(_webViewEnv) { Owner = this };
            _commentsWindow.Closed += (_, _) => _commentsWindow = null;
            _commentsWindow.PlaceNextTo(this);
            _commentsWindow.Show();
        }
        _commentsWindow.ShowItem(_currentPlayingItem);
        _commentsWindow.Activate();
    }

    // =========================================================================
    // 💬 コメント一覧（右の欄。弾幕ではなく普通のコメント欄として読む）
    // =========================================================================
    private readonly CommentService _commentService = new();
    private string? _commentsItemKey;      // いま一覧に出しているコメントの動画
    private string? _commentsNext;         // 続きを読み込む時の値（YouTube）
    private bool _commentsLoading;
    private bool _commentsSortPopular;
    private List<CommentEntry> _commentEntries = [];

    /// <summary>上の「💬 コメント」ボタン：右の欄を開いてコメントのタブにする</summary>
    private void ShowCommentsPanel_Click(object sender, RoutedEventArgs e)
    {
        // シアターモード中でも、押した時は右の欄を出す
        _isSideQueueOpen = true;
        if (!_isFullScreen && !_isMiniPlayerMode)
        {
            SideQueueColumn.Width = new GridLength(320);
            SideQueueBorder.Visibility = Visibility.Visible;
        }
        _activeQueueTab = "Comments";
        RefreshSideQueue();
    }

    /// <summary>再生中の動画のコメントがまだ出ていなければ読み込む</summary>
    private void EnsureCommentsLoaded()
    {
        var item = _currentPlayingItem;
        if (item == null)
        {
            ShowCommentEntries([], "動画を再生するとコメントが表示されます");
            return;
        }
        if (_commentsItemKey == item.UniqueKey) return;
        _ = LoadCommentsAsync(item, more: false);
    }

    private async Task LoadCommentsAsync(VideoItem item, bool more)
    {
        if (!more)
        {
            _commentsItemKey = item.UniqueKey;
            _commentsNext = null;
            _commentsSortPopular = false;
            BtnCommentsSort.Content = "人気順";
            BtnCommentsSort.Visibility = item.Site == SiteType.Niconico ? Visibility.Visible : Visibility.Collapsed;

            if (!CommentService.Supports(item))
            {
                ShowCommentEntries([], item.Site is SiteType.Twitch or SiteType.Abema
                    ? "ライブのチャットは「↗ サイトで見る」から見られます"
                    : "このサイトのコメントは「↗ サイトで見る」から見られます");
                return;
            }
            ShowCommentEntries([], "コメントを読み込み中...");
        }
        else if (_commentsLoading || _commentsNext == null) return;

        _commentsLoading = true;
        try
        {
            var page = await _commentService.GetAsync(item, more ? _commentsNext : null);
            if (_commentsItemKey != item.UniqueKey) return; // 待っている間に別の動画になった

            _commentsNext = page.Next;
            if (more)
            {
                // 続きは今の一覧の後ろに足す（スクロール位置はそのまま）
                _commentEntries.AddRange(page.Items);
                foreach (var c in page.Items) _commentView.Add(c);
                UpdateCommentsStatus(null);
            }
            else
            {
                ShowCommentEntries(page.Items, page.Items.Count == 0 ? page.Error ?? "コメントはまだありません" : null);
            }
        }
        finally
        {
            _commentsLoading = false;
        }
    }

    private System.Collections.ObjectModel.ObservableCollection<CommentEntry> _commentView = [];

    private void ShowCommentEntries(List<CommentEntry> entries, string? message)
    {
        _commentEntries = entries;
        IEnumerable<CommentEntry> view = _commentsSortPopular
            ? entries.OrderByDescending(c => c.Likes).ThenBy(c => c.TimeSeconds)
            : entries;
        _commentView = new(view);
        CommentsList.ItemsSource = _commentView;
        if (_commentView.Count > 0) CommentsList.ScrollIntoView(_commentView[0]);
        UpdateCommentsStatus(message);
    }

    private void UpdateCommentsStatus(string? message)
    {
        CommentsStatusText.Text = message ?? (_commentsNext != null
            ? $"{_commentEntries.Count}件（下へスクロールで続きを表示）"
            : $"{_commentEntries.Count}件");
    }

    private void CommentsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 下の方まで読んだら続きを読み込む
        if (_commentsNext == null || _commentsLoading || _currentPlayingItem == null) return;
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 200)
            _ = LoadCommentsAsync(_currentPlayingItem, more: true);
    }

    private void CommentsSort_Click(object sender, RoutedEventArgs e)
    {
        _commentsSortPopular = !_commentsSortPopular;
        BtnCommentsSort.Content = _commentsSortPopular ? "時間順" : "人気順";
        ShowCommentEntries(_commentEntries, null);
    }

    /// <summary>コメントの時間をクリック：その場面へ移動</summary>
    private void CommentTime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CommentEntry c } || !c.HasTime) return;
        if (!_isWebViewReady || PlayerWebView.CoreWebView2 == null) return;
        var sec = c.TimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = PlayerWebView.CoreWebView2.ExecuteScriptAsync($"if (window.prismilSeekTo) window.prismilSeekTo({sec});");
    }

    /// <summary>コメント欄を開いたまま次の動画に進んだら、コメント欄もその動画に切り替える</summary>
    private void SyncCommentsWindow(VideoItem item)
    {
        BtnOpenComments.IsEnabled = HasCommentsPage(item);
        if (_commentsWindow == null) return;
        if (HasCommentsPage(item)) _commentsWindow.ShowItem(item);
        else _commentsWindow.Close();
    }

    private async void AdBlockToggle_Click(object sender, RoutedEventArgs e)
    {
        _adBlockService.IsEnabled = !_adBlockService.IsEnabled;
        UpdateAdBlockButtonUI();
        SaveSettings();
        await UpdateYouTubeAdStripScriptAsync(); // 次に開くページから有効/無効が切り替わる
        await ApplyZenModeToPlayerAsync();
    }

    private string? _adStripScriptId;

    /// <summary>広告ブロックがONの間だけ、YouTube の広告予定を取り除くスクリプトをページ読み込み時に入れる</summary>
    private async Task UpdateYouTubeAdStripScriptAsync()
    {
        var core = PlayerWebView.CoreWebView2;
        if (core == null) return;

        if (_adBlockService.IsEnabled && _adStripScriptId == null)
        {
            _adStripScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAdStripScript.Script);
        }
        else if (!_adBlockService.IsEnabled && _adStripScriptId != null)
        {
            core.RemoveScriptToExecuteOnDocumentCreated(_adStripScriptId);
            _adStripScriptId = null;
        }
    }

    /// <summary>設定画面の「ショート動画」：0 除外 / 1 ショートだけ / 2 すべて</summary>
    private async void SetShortsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _applyingSettings || SetShortsCombo.SelectedIndex < 0) return;
        if (SetShortsCombo.SelectedIndex == _shortsFilterMode) return;
        _shortsFilterMode = SetShortsCombo.SelectedIndex;
        SaveSettings();

        await ApplyZenModeToPlayerAsync();

        if (_shortsFilterMode == 1)
        {
            if (NavSearch.IsChecked == true && !string.IsNullOrEmpty(_lastSearchQuery))
                await ExecuteSearchAsync(_lastSearchQuery);
            else if (NavHome.IsChecked == true)
                await LoadDiscoverFeedAsync(_lastTopicKeyword);
            else
                RefreshDisplayedCards();
        }
        else
        {
            RefreshDisplayedCards();
        }
    }

    // =========================================================================
    // 🎲 5サイト横断「動画ガチャ」＆ おまかせ再生機能
    // =========================================================================
    private static readonly string[] GachaThemes =
    [
        "神曲 アニメ OP ED 名曲",
        "作業用BGM カフェ ジャズ Lofi",
        "VOCALOID 伝説のマスターピース",
        "ゲーム実況 神回 爆笑",
        "RTA リアルタイムアタック 解説",
        "歌ってみた 名曲 カバー",
        "レトロゲーム BGM 名作",
        "ピアノ 演奏してみた アニソン",
        "音MAD ランキング 人気",
        "解説 科学 宇宙 雑学",
        "ガジェット レビュー デスクツアー",
        "料理 飯テロ レシピ",
        "猫 動物 癒やし かわいい",
        "インディーゲーム 神ゲー 紹介",
        "VTuber 切り抜き 歌枠",
        "EDM ナイトコア ドライブBGM",
        "格闘ゲーム 大会 名勝負",
        "夜景 散歩 4K 東京"
    ];

    private async void VideoGacha_Click(object sender, RoutedEventArgs e)
    {
        var pool = new List<string>(GachaThemes);
        foreach (var ch in _libraryService.Subscriptions)
            pool.Add(ch.Name);
        foreach (var kw in _libraryService.RecentSearches)
            pool.Add(kw);

        string pickedTheme = pool[Random.Shared.Next(pool.Count)];

        _activeCustomPlaylist = null;
        _activeSubscribedChannel = null;
        // ガチャはキーワード検索ではないので、続きの読み込みやサイト別の再検索はしない
        ++_searchGeneration;
        _searchKeyword = "";
        _searchSite = null;
        _siteSearchCursor = null;
        _allSearchCursors.Clear();
        FilterAll.IsChecked = true;
        NavSearch.IsChecked = true;
        SwitchToCardBrowserView();

        ShowLoadingOverlay(
            "🎲 5サイト横断「動画ガチャ」を抽選中...",
            $"抽選テーマ: 「{pickedTheme}」からランダムに発掘しています");

        var allCandidates = _shortsFilterMode == 1
            ? await _searchService.SearchAllShortsAsync(pickedTheme, limitPerSite: 12)
            : await _searchService.SearchAllAsync(pickedTheme, limitPerSite: 10);

        _libraryService.SyncCollectionFlags(allCandidates);

        // ランダムシャッフルして12本をピックアップ
        var gachaResult = allCandidates
            .OrderBy(_ => Random.Shared.Next())
            .Take(12)
            .ToList();

        _lastSearchQuery = $"🎰 ガチャ結果: {pickedTheme}";
        _searchItems = gachaResult;
        NavSearch.Tag = gachaResult.Count.ToString();

        HideLoadingOverlay();
        RefreshDisplayedCards();
        ViewHeaderTitle.Text = $"🎰 ガチャ10連発掘！ テーマ:「{pickedTheme}」";
    }

    private void PlayRandomOne_Click(object sender, RoutedEventArgs e)
    {
        if (_displayedCards is { Count: > 0 } list)
        {
            var picked = list[Random.Shared.Next(list.Count)];
            PlayVideo(picked);
        }
    }

    // =========================================================================
    // 📊 マイ視聴統計ダッシュボード集計
    // =========================================================================
    private void RefreshStatsDashboard()
    {
        var history = _libraryService.History;
        var favs = _libraryService.Favorites;
        var watchLater = _libraryService.WatchLater;
        var subs = _libraryService.Subscriptions;

        // 1. KPIカード集計
        StatsTotalWatchedText.Text = $"{history.Count} 本";
        long totalSec = history.Sum(x => (long)Math.Max(0, x.DurationSeconds));
        long totalHours = totalSec / 3600;
        long totalMins = (totalSec % 3600) / 60;
        StatsTotalTimeText.Text = totalHours > 0
            ? $"累計動画時間: 約 {totalHours}時間 {totalMins}分"
            : $"累計動画時間: 約 {totalMins}分";

        int totalSaved = favs.Count + watchLater.Count + _libraryService.Playlists.Sum(p => p.Items.Count);
        StatsTotalSavedText.Text = $"{totalSaved} 本";
        StatsSavedDetailText.Text = $"お気に入り {favs.Count} / 後で見る {watchLater.Count}";

        StatsTotalSubsText.Text = $"{subs.Count} ch";
        StatsPlaylistsCountText.Text = $"カスタムリスト: {_libraryService.Playlists.Count}個";
        StatsTotalBlockedText.Text = $"{_adBlockService.BlockedCount} 件";

        // 履歴＋お気に入り＋後で見るを統合した分析母集団
        var combined = history
            .Concat(favs)
            .Concat(watchLater)
            .ToList();
        int totalCount = Math.Max(1, combined.Count);

        // 2. 8ソース別利用バランス
        var siteDefinitions = new (SiteType Site, string Name, Brush Brush)[]
        {
            (SiteType.YouTube, "▶ YouTube", new SolidColorBrush(Color.FromRgb(239, 68, 68))),
            (SiteType.Niconico, "📺 ニコニコ動画", new SolidColorBrush(Color.FromRgb(226, 232, 240))),
            (SiteType.SoundCloud, "☁ SoundCloud", new SolidColorBrush(Color.FromRgb(255, 85, 0))),
            (SiteType.Podcast, "🎙 Podcast", new SolidColorBrush(Color.FromRgb(139, 92, 246))),
            (SiteType.Radio, "📻 ネットラジオ", new SolidColorBrush(Color.FromRgb(14, 165, 233))),
            (SiteType.Bilibili, "🅱 bilibili", new SolidColorBrush(Color.FromRgb(0, 161, 214))),
            (SiteType.Twitch, "🔴 Twitch", new SolidColorBrush(Color.FromRgb(145, 70, 255))),
            (SiteType.Abema, "🟢 ABEMA", new SolidColorBrush(Color.FromRgb(16, 185, 129)))
        };

        var siteRows = siteDefinitions.Select(sd =>
        {
            int c = combined.Count(x => x.Site == sd.Site);
            double pct = combined.Count == 0 ? 0 : (c * 100.0 / totalCount);
            return new
            {
                Label = sd.Name,
                Detail = $"{c} 本 ({pct:F0}%)",
                Percentage = pct,
                BarBrush = sd.Brush
            };
        }).ToList();
        SiteStatsItemsControl.ItemsSource = siteRows;

        // 3. よく見る推し投稿者・チャンネル TOP 8
        var topCreators = combined
            .Where(x => !string.IsNullOrWhiteSpace(x.Author))
            .GroupBy(x => new { x.Site, Author = x.Author.TrimStart('#') })
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select((g, i) =>
            {
                var sample = g.First();
                return new
                {
                    Rank = (i + 1).ToString(),
                    Author = g.Key.Author,
                    Site = g.Key.Site,
                    SiteName = sample.SiteDisplayName,
                    SiteBrush = sample.SiteBadgeBrush,
                    CountText = $"{g.Count()} 回",
                    ChannelObj = new SubscribedChannel { Name = g.Key.Author, Site = g.Key.Site }
                };
            }).ToList();
        TopCreatorsItemsControl.ItemsSource = topCreators;

        // 4. 動画の長さ傾向
        int shortCount = combined.Count(x => x.IsShortVideo);
        int mvCount = combined.Count(x => !x.IsShortVideo && x.DurationSeconds > 62 && x.DurationSeconds <= 600);
        int midCount = combined.Count(x => x.DurationSeconds > 600 && x.DurationSeconds <= 1800);
        int longOrLiveCount = combined.Count(x => x.DurationSeconds > 1800 || (x.DurationSeconds == 0 && !x.IsShortVideo));

        var durationRows = new[]
        {
            new { Label = "📱 ショート (〜1分)", Detail = $"{shortCount} 本", Percentage = combined.Count == 0 ? 0 : shortCount * 100.0 / totalCount, BarBrush = (Brush)new SolidColorBrush(Color.FromRgb(244, 114, 182)) },
            new { Label = "🎵 MV・短編 (1〜10分)", Detail = $"{mvCount} 本", Percentage = combined.Count == 0 ? 0 : mvCount * 100.0 / totalCount, BarBrush = (Brush)new SolidColorBrush(Color.FromRgb(99, 102, 241)) },
            new { Label = "🎬 企画・アニメ (10〜30分)", Detail = $"{midCount} 本", Percentage = combined.Count == 0 ? 0 : midCount * 100.0 / totalCount, BarBrush = (Brush)new SolidColorBrush(Color.FromRgb(56, 189, 248)) },
            new { Label = "📡 長尺・生配信 (30分〜/LIVE)", Detail = $"{longOrLiveCount} 本", Percentage = combined.Count == 0 ? 0 : longOrLiveCount * 100.0 / totalCount, BarBrush = (Brush)new SolidColorBrush(Color.FromRgb(52, 211, 153)) }
        };
        DurationStatsItemsControl.ItemsSource = durationRows;
    }

    private async void OpenCreatorFromStats_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag != null)
        {
            var prop = btn.Tag.GetType().GetProperty("ChannelObj");
            if (prop?.GetValue(btn.Tag) is SubscribedChannel ch)
            {
                await OpenChannelViewAsync(ch);
            }
        }
    }

    private async void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SpeedCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string tagStr &&
            double.TryParse(tagStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rate))
        {
            _playbackRate = rate;
            await ApplyZenModeToPlayerAsync();
        }
    }

    private async void LoopToggle_Click(object sender, RoutedEventArgs e)
    {
        _isLoopMode = !_isLoopMode;
        BtnLoopToggle.Content = _isLoopMode ? "🔁 ループ: ON" : "🔁 ループ: OFF";
        var brush = _isLoopMode
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        BtnLoopToggle.BorderBrush = brush;
        BtnDockLoop.BorderBrush = brush;
        await ApplyZenModeToPlayerAsync();
    }

    private void AutoNextToggle_Click(object sender, RoutedEventArgs e)
    {
        _isAutoNext = !_isAutoNext;
        BtnAutoNextToggle.Content = _isAutoNext ? "⏭ 連続: ON" : "⏭ 連続: OFF";
        BtnAutoNextToggle.BorderBrush = _isAutoNext
            ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
            : NeutralBorderBrush;
        SetAutoNext.IsChecked = _isAutoNext;
        SaveSettings();
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (!_isWebViewReady || PlayerWebView.CoreWebView2 == null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ZenTube");
            Directory.CreateDirectory(dir);
            var fileName = $"ZenTube_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var fullPath = Path.Combine(dir, fileName);

            using (var fs = File.Create(fullPath))
            {
                await PlayerWebView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, fs);
            }

            BtnScreenshot.Content = "✓";
            await Task.Delay(1500);
            BtnScreenshot.Content = "📸";
        }
        catch
        {
            BtnScreenshot.Content = "⚠";
            await Task.Delay(1500);
            BtnScreenshot.Content = "📸";
        }
    }

    private void ToggleSideQueue_Click(object sender, RoutedEventArgs e)
    {
        _isSideQueueOpen = !_isSideQueueOpen;
        SideQueueColumn.Width = _isSideQueueOpen ? new GridLength(320) : new GridLength(0);
        SideQueueBorder.Visibility = _isSideQueueOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueueTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tab)
        {
            _activeQueueTab = tab;
            RefreshSideQueue();
        }
    }

    private void BackToGrid_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen) SetFullScreen(false);
        if (_isMiniPlayerMode) SetMiniPlayerMode(false);
        if (_isTheaterMode) SetTheaterMode(false);
        SwitchToCardBrowserView();
    }

    private void SwitchToPlayer_Click(object sender, RoutedEventArgs e)
    {
        SwitchToPlayerView();
    }

    private void ToggleViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerView.Visibility == Visibility.Visible)
            SwitchToCardBrowserView();
        else
            SwitchToPlayerView();
    }

    private async void StopPlayback_Click(object sender, RoutedEventArgs e)
    {
        _currentPlayingItem = null;
        NowPlayingMiniCard.Visibility = Visibility.Collapsed;
        MusicPlayerDockBar.Visibility = Visibility.Collapsed;
        if (_isWebViewReady && PlayerWebView.CoreWebView2 != null)
        {
            try
            {
                await PlayerWebView.CoreWebView2.ExecuteScriptAsync("if(window.pauseZenTubeVideo) window.pauseZenTubeVideo();");
                PlayerWebView.CoreWebView2.Navigate("about:blank");
            }
            catch { }
        }
    }

    private async void ClearCookies_Click(object sender, RoutedEventArgs e)
    {
        if (!_isWebViewReady || PlayerWebView.CoreWebView2 == null) return;
        try
        {
            PlayerWebView.CoreWebView2.CookieManager.DeleteAllCookies();
            await PlayerWebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            BtnClearCookies.Content = "✓ 消去完了";
            await Task.Delay(1800);
            BtnClearCookies.Content = "🧹 Cookie消去";
        }
        catch { }
    }

    private void OpenLocalDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(_libraryService.DataFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch { }
    }

    private void OpenExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        var url = _currentPlayingItem?.WatchUrl ?? PlayerWebView.CoreWebView2?.Source;
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void PinTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        SetTopmost.IsChecked = Topmost;
        SaveSettings();
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        SetFullScreen(!_isFullScreen);
    }

    // =========================================================================
    // オーバーレイ・全画面・ショートカット
    // =========================================================================
    private void ShowLoadingOverlay(string title, string subtitle)
    {
        _isLoading = true;
        StatusTitle.Text = title;
        StatusSubtitle.Text = subtitle;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void ShowEmptyState(string title, string subtitle)
    {
        StatusTitle.Text = title;
        StatusSubtitle.Text = subtitle;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoadingOverlay()
    {
        _isLoading = false;
        StatusOverlay.Visibility = Visibility.Collapsed;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.L:
                case Key.F:
                    if (_isMiniPlayerMode) SetMiniPlayerMode(false);
                    SearchBox.Focus();
                    e.Handled = true;
                    break;
                case Key.M:
                    SetMiniPlayerMode(!_isMiniPlayerMode);
                    e.Handled = true;
                    break;
                case Key.B:
                    if (!_isFullScreen && !_isMiniPlayerMode) SetSidebarOpen(!_isSidebarOpen);
                    e.Handled = true;
                    break;
                case Key.D1:
                    FilterAll.IsChecked = true;
                    e.Handled = true;
                    break;
                case Key.D2:
                    FilterYouTube.IsChecked = true;
                    e.Handled = true;
                    break;
                case Key.D3:
                    FilterNiconico.IsChecked = true;
                    e.Handled = true;
                    break;
                case Key.D4:
                    FilterBilibili.IsChecked = true;
                    e.Handled = true;
                    break;
                case Key.D5:
                    FilterTwitch.IsChecked = true;
                    e.Handled = true;
                    break;
                case Key.D6:
                    FilterAbema.IsChecked = true;
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.F11)
        {
            SetFullScreen(!_isFullScreen);
            e.Handled = true;
        }
        else if (_shortsFilterMode == 1 && PlayerView.Visibility == Visibility.Visible &&
                 !SearchBox.IsKeyboardFocused)
        {
            if (e.Key == Key.Down)
            {
                PlayNextInQueue(step: 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                PlayNextInQueue(step: -1);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (_isFullScreen)
            {
                SetFullScreen(false);
                e.Handled = true;
            }
            else if (_isMiniPlayerMode)
            {
                SetMiniPlayerMode(false);
                e.Handled = true;
            }
            else if (_isTheaterMode)
            {
                SetTheaterMode(false);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.None &&
                 PlayerView.Visibility == Visibility.Visible && Keyboard.FocusedElement is not TextBox)
        {
            SetTheaterMode(!_isTheaterMode);
            e.Handled = true;
        }
    }

    // =========================================================================
    // 🎭 シアターモード：左のメニュー・検索欄・効果バー・右の欄を隠して、動画を横いっぱいに大きく見せる
    // （全画面とちがってウィンドウのまま。タイトルと操作ボタンの段は残す）
    // =========================================================================
    private bool _isTheaterMode;

    private void TheaterModeButton_Click(object sender, RoutedEventArgs e) => SetTheaterMode(!_isTheaterMode);

    private DateTime _settingsPopupClosedAt;
    private bool _settingsPopupHooked;

    /// <summary>⚙ 設定：ボタンの右端にそろえて設定パネルを開く（もう一度押すと閉じる）</summary>
    private void PlayerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // 開いている時にこのボタンを押すと、外側クリックでいったん閉じてからこの処理が来るので、
        // 閉じた直後なら開き直さない（＝ボタンで閉じられる）
        if (PlayerSettingsPopup.IsOpen || (DateTime.Now - _settingsPopupClosedAt).TotalMilliseconds < 250)
        {
            PlayerSettingsPopup.IsOpen = false;
            return;
        }
        if (!_settingsPopupHooked)
        {
            _settingsPopupHooked = true;
            PlayerSettingsPopup.Closed += (_, _) => _settingsPopupClosedAt = DateTime.Now;
            // ボタンの真下に、右端をそろえて出す（画面の拡大率に関係なく正しい位置になるよう、実際の大きさで計算）
            PlayerSettingsPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
            PlayerSettingsPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new System.Windows.Controls.Primitives.CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width, targetSize.Height + 6),
                    System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
            ];
        }
        PlayerSettingsPopup.IsOpen = true;
    }

    private void SetTheaterMode(bool enable)
    {
        if (enable == _isTheaterMode) return;
        if (_isMiniPlayerMode) SetMiniPlayerMode(false);
        if (_isFullScreen) SetFullScreen(false);
        _isTheaterMode = enable;
        if (enable) SwitchToPlayerView();
        ApplyPlayerLayout();

        BtnTheaterMode.Content = "🎭 シアター"; // 幅が変わらないよう文字は同じ。ON の時は枠の色で示す
        BtnTheaterMode.BorderBrush = enable ? new SolidColorBrush(Color.FromRgb(99, 102, 241)) : NeutralBorderBrush;
    }

    /// <summary>通常表示（全画面・ミニ窓でない時）のプレイヤー画面の各段・列の大きさを、シアターかどうかに合わせて決める</summary>
    private void ApplyPlayerLayout()
    {
        if (_isFullScreen || _isMiniPlayerMode) return;
        if (_isTheaterMode)
        {
            SidebarColumn.Width = new GridLength(0);
            HeaderRow.Height = new GridLength(0);
            SideQueueColumn.Width = new GridLength(0);
            SideQueueBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarColumn.Width = new GridLength(CurrentSidebarWidth);
            HeaderRow.Height = new GridLength(62);
            SideQueueColumn.Width = _isSideQueueOpen ? new GridLength(320) : new GridLength(0);
            SideQueueBorder.Visibility = _isSideQueueOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        PlayerControlBarRow.Height = new GridLength(50);
    }

    private void SetFullScreen(bool enable)
    {
        if (enable == _isFullScreen) return;
        if (_isMiniPlayerMode) SetMiniPlayerMode(false);
        _isFullScreen = enable;

        if (enable)
        {
            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            SwitchToPlayerView();

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;

            SidebarColumn.Width = new GridLength(0);
            HeaderRow.Height = new GridLength(0);
            PlayerControlBarRow.Height = new GridLength(0);
            SideQueueColumn.Width = new GridLength(0);
        }
        else
        {
            WindowStyle = _prevWindowStyle;
            WindowState = _prevWindowState;

            ApplyPlayerLayout(); // シアターモードだった時はシアターの形に戻る
        }
    }
}
