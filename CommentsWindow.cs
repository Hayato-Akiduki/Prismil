using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ZenTube.Models;

namespace ZenTube;

/// <summary>
/// 💬 コメント欄ウィンドウ。
/// 再生中の動画のサイトのページ（コメント欄・チャット）を別ウィンドウで開く。
/// メインの独自プレイヤーは再生を続けたままにしたいので、このウィンドウ内の動画は止めて音も消す。
/// </summary>
public class CommentsWindow : Window
{
    private readonly CoreWebView2Environment _env;
    private readonly WebView2 _webView = new();
    private string? _pendingUrl;

    // このウィンドウで開いたページの動画は再生させない（音が二重にならないように）。
    // あわせて YouTube・bilibili はコメント欄の位置まで自動でスクロールする。
    private const string QuietScript = """
        (function() {
            if (window.__prismilQuiet) return;
            window.__prismilQuiet = true;
            const quiet = () => {
                document.querySelectorAll('video, audio').forEach(v => {
                    try { v.muted = true; v.autoplay = false; if (!v.paused) v.pause(); } catch {}
                });
            };
            document.addEventListener('play', quiet, true);
            setInterval(quiet, 700);

            if (window !== window.top) return;
            let tries = 0;
            const timer = setInterval(() => {
                if (++tries > 40) { clearInterval(timer); return; }
                const h = location.hostname;
                let el = null;
                if (h.includes('youtube.com')) el = document.querySelector('ytd-comments#comments');
                else if (h.includes('bilibili.com')) el = document.querySelector('#commentapp, bili-comments, .comment-container');
                if (el && el.getBoundingClientRect().height > 0) {
                    el.scrollIntoView({ block: 'start' });
                    clearInterval(timer);
                }
            }, 500);
        })();
        """;

    public CommentsWindow(CoreWebView2Environment env)
    {
        _env = env;
        Title = "💬 コメント";
        Width = 520;
        Height = 820;
        MinWidth = 360;
        MinHeight = 400;
        Background = new SolidColorBrush(Color.FromRgb(15, 15, 20));
        ShowInTaskbar = false;
        Content = _webView;
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async(_env);
            var core = _webView.CoreWebView2;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(QuietScript);

            // リンクから新しいウィンドウを開こうとした時は、このウィンドウの中で開く
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };

            if (_pendingUrl != null) core.Navigate(_pendingUrl);
        }
        catch
        {
            // WebView2 の初期化に失敗した時は何もしない（メインのプレイヤーには影響させない）
        }
    }

    /// <summary>メインウィンドウの右横に並べる（画面からはみ出す時は重ねて置く）</summary>
    public void PlaceNextTo(Window owner)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        double left = owner.Left + owner.ActualWidth + 8;
        if (owner.WindowState == WindowState.Maximized || left + Width > area.Right)
            left = area.Right - Width - 16;
        Left = Math.Max(area.Left, left);
        Top = Math.Max(area.Top, Math.Min(owner.Top, area.Bottom - Height));
        Height = Math.Min(Height, area.Height);
    }

    public void ShowItem(VideoItem item)
    {
        Title = $"💬 コメント - {item.Title}";
        var url = GetCommentsUrl(item);
        var core = _webView.CoreWebView2;
        if (core == null)
        {
            _pendingUrl = url; // まだ準備中。準備ができたら開く
            return;
        }
        if (!string.Equals(core.Source, url, StringComparison.Ordinal)) core.Navigate(url);
    }

    /// <summary>コメント欄を見るのに一番向いているページ</summary>
    private static string GetCommentsUrl(VideoItem item)
    {
        var id = item.Id ?? "";
        switch (item.Site)
        {
            case SiteType.YouTube when Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$"):
                return $"https://www.youtube.com/watch?v={id}";

            // Twitch の生配信はチャットだけのページを開く
            case SiteType.Twitch when !item.WatchUrl.Contains("/videos/") && Regex.IsMatch(id, @"^[A-Za-z0-9_]{2,40}$"):
                return $"https://www.twitch.tv/popout/{id}/chat?popout=";

            // 保存されている URL は埋め込み用なので、通常の視聴ページを開く
            case SiteType.Dailymotion when Regex.IsMatch(id, @"^[A-Za-z0-9]+$"):
                return $"https://www.dailymotion.com/video/{id}";

            default:
                return item.WatchUrl;
        }
    }
}
