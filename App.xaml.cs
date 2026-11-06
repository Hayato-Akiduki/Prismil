using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ZenTube.Services;

namespace ZenTube;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private DateTime _lastErrorDialogAt = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 予想外のエラーが起きても、アプリごと落ちないようにする（内容は記録ファイルに残す）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("BackgroundTask", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write("Fatal", ex);
        };

        base.OnStartup(e);
        SmoothScroll.Register(); // 全画面のスクロールをなめらか＆適度な速さに
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("UI", e.Exception);
        e.Handled = true; // 落とさずに続ける

        // 同じエラーが続けて起きても、お知らせは 30 秒に 1 回まで
        if ((DateTime.Now - _lastErrorDialogAt).TotalSeconds < 30) return;
        _lastErrorDialogAt = DateTime.Now;

        MessageBox.Show(
            "予期しないエラーが起きましたが、アプリはそのまま使えます。\n" +
            "同じ操作で何度も起きる場合は、次のファイルを開発者に送ってください。\n\n" + ErrorLog.LogFilePath,
            "Prismil", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
