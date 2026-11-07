using Velopack;
using Velopack.Sources;

namespace ZenTube.Services;

/// <summary>
/// GitHub Releases に新しい版が出ていないか確かめて、あれば裏でダウンロードする。
/// ダウンロードが済んだ版は、「今すぐ再起動」で適用するか、次に起動した時に自動で適用される。
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// 更新を置く GitHub リポジトリの URL（例: https://github.com/あなたのユーザー名/prismil ）。
    /// 公開（Public）リポジトリにすること。release.ps1 も同じ値を使う。空のままなら更新の確認はしない。
    /// </summary>
    public const string GitHubRepoUrl = "https://github.com/Hayato-Akiduki/Prismil";

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
        if (string.IsNullOrWhiteSpace(GitHubRepoUrl)) return null;

        // 公開リポジトリなので鍵（トークン）は使わない。アプリに鍵を入れると誰でも取り出せてしまうため
        var manager = new UpdateManager(new GithubSource(GitHubRepoUrl, null, false));
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
