using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ZenTube.Services;

/// <summary>
/// Windows 11 のすりガラス（アクリル / Mica）効果をウィンドウ全体に付ける。
/// 対応していない Windows では何もせず、元の不透明な背景のままにする。
/// </summary>
public static class GlassBackdrop
{
    public enum Kind { Mica = 2, Acrylic = 3, MicaAlt = 4 }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_CAPTION_COLOR = 35;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>Windows 11 22H2 (build 22621) 以降ならシステムのすりガラス効果が使える</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>ウィンドウにすりガラス効果を適用する。成功したら true。</summary>
    public static bool Apply(Window window, Kind kind = Kind.Acrylic)
    {
        if (!IsSupported) return false;

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget == null) return false;

        // WPF の描画を透明にして、後ろの DWM 効果を見せる
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // タイトルバーにアクセントカラーを塗らず、すりガラスをそのまま見せる
        int noColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref noColor, sizeof(int));

        int type = (int)kind;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
    }
}
