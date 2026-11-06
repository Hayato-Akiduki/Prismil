using System.IO;

namespace ZenTube.Services;

/// <summary>
/// 予想外のエラーを記録するファイル（%LOCALAPPDATA%\ZenTube\logs\error.log）。
/// あとから原因を調べられるように、時刻・どこで起きたか・エラーの中身を残す。
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZenTube", "logs", "error.log");

    public static void Write(string where, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);

                // 大きくなりすぎたら古いものを1世代だけ残して作り直す
                var info = new FileInfo(LogFilePath);
                if (info.Exists && info.Length > 1024 * 1024)
                    File.Move(LogFilePath, LogFilePath + ".old", overwrite: true);

                File.AppendAllText(LogFilePath,
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] v{AppVersion}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // 記録に失敗してもアプリは止めない
        }
    }

    private static string AppVersion =>
        typeof(ErrorLog).Assembly.GetName().Version?.ToString(3) ?? "?";
}
