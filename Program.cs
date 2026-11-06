using Velopack;

namespace ZenTube;

/// <summary>
/// アプリの入口。Velopack（自動更新の仕組み）は、画面を作るより前に一番最初に動かす必要がある。
/// インストール・更新・アンインストールの途中で呼ばれた場合は、ここで処理して終わる。
/// ダウンロード済みの新しい版があれば、ここで自動的に適用してから起動する。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
