using Velopack;

namespace ZenTube.Services;

/// <summary>
/// 更新の置き場所（Cloudflare R2 の公開 URL）に新しい版が出ていないか確かめて、あれば裏でダウンロードする。
/// ダウンロードが済んだ版は、「今すぐ再起動」で適用するか、次に起動した時に自動で適用される。
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// 更新を置く場所の公開 URL（Cloudflare R2 の「パブリック開発 URL」。例: https://pub-xxxxxxxx.r2.dev ）。
    /// release.ps1 も同じ値を使う。空のままなら更新の確認はしない。
    /// </summary>
    public const string UpdateFeedUrl = "";

    private UpdateManager? _manager;

    /// <summary>今動いているアプリのバージョン（例: 1.0.0）</summary>
    public static string CurrentVersionText =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>
    /// 新しい版があればダウンロードまで済ませて、その版を返す。無ければ null。
    /// 開発中（Setup.exe からインストールしていない状態）では何もしない。
    /// </summary>
    public async Task<VelopackAsset?> CheckAndDownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(UpdateFeedUrl)) return null;

        var manager = new UpdateManager(UpdateFeedUrl);
        if (!manager.IsInstalled) return null;

        var info = await manager.CheckForUpdatesAsync();
        if (info == null) return null;

        await manager.DownloadUpdatesAsync(info);
        _manager = manager;
        return info.TargetFullRelease;
    }

    /// <summary>ダウンロード済みの版を適用して、アプリを起動し直す</summary>
    public void RestartToApply(VelopackAsset release) => _manager?.ApplyUpdatesAndRestart(release);
}
