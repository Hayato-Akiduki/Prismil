using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZenTube.Services;

/// <summary>
/// アプリ内のすべての ScrollViewer に、なめらかで速すぎないホイールスクロールを付ける。
/// WPF 標準はタッチパッドの細かい入力も毎回 1 ノッチ分動かすため、異様に速くなる。
/// ここではホイール量に比例して動かし、さらにイージングでなめらかにする。
/// </summary>
public static class SmoothScroll
{
    /// <summary>ホイール 1 ノッチ（Delta=120）あたりの移動量(px)。小さいほどゆっくり。</summary>
    public static double PixelsPerNotch { get; set; } = 60;

    /// <summary>1 フレームごとに目標位置へ近づく割合（0〜1）。小さいほどなめらか・ゆっくり。</summary>
    public static double Easing { get; set; } = 0.22;

    private sealed class State
    {
        public double TargetV = double.NaN;
        public double TargetH = double.NaN;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    private static readonly HashSet<ScrollViewer> Animating = new();
    private static bool _hooked;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;

        bool horizontal = Keyboard.Modifiers == ModifierKeys.Shift;
        var sv = FindTarget(e.OriginalSource as DependencyObject, e.Delta, horizontal);
        if (sv == null && !horizontal)
        {
            // 縦に動かせる場所が無ければ、横スクロール専用の帯（タグ一覧など）を横に動かす
            sv = FindTarget(e.OriginalSource as DependencyObject, e.Delta, horizontal: true);
            horizontal = sv != null;
        }
        if (sv == null) return;

        var st = States.GetOrCreateValue(sv);
        double step = -e.Delta / 120.0 * PixelsPerNotch;

        if (horizontal)
        {
            double from = Animating.Contains(sv) && !double.IsNaN(st.TargetH) ? st.TargetH : sv.HorizontalOffset;
            st.TargetH = Math.Clamp(from + step, 0, sv.ScrollableWidth);
        }
        else
        {
            double from = Animating.Contains(sv) && !double.IsNaN(st.TargetV) ? st.TargetV : sv.VerticalOffset;
            st.TargetV = Math.Clamp(from + step, 0, sv.ScrollableHeight);
        }

        StartAnimation(sv);
        e.Handled = true;
    }

    /// <summary>マウスの下から外側へたどって、その方向にまだ動ける一番内側の ScrollViewer を探す。</summary>
    private static ScrollViewer? FindTarget(DependencyObject? node, int delta, bool horizontal)
    {
        while (node != null)
        {
            if (node is ScrollViewer sv && !sv.CanContentScroll && CanScroll(sv, delta, horizontal))
                return sv;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static bool CanScroll(ScrollViewer sv, int delta, bool horizontal)
    {
        if (horizontal)
        {
            if (sv.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled || sv.ScrollableWidth <= 0) return false;
            return delta > 0 ? sv.HorizontalOffset > 0 : sv.HorizontalOffset < sv.ScrollableWidth;
        }
        if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled || sv.ScrollableHeight <= 0) return false;
        return delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight;
    }

    private static void StartAnimation(ScrollViewer sv)
    {
        Animating.Add(sv);
        if (_hooked) return;
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        foreach (var sv in Animating.ToArray())
        {
            if (!States.TryGetValue(sv, out var st) || !sv.IsLoaded)
            {
                Animating.Remove(sv);
                continue;
            }

            bool doneV = Step(sv.VerticalOffset, ref st.TargetV, Math.Min(st.TargetV, sv.ScrollableHeight), sv.ScrollToVerticalOffset);
            bool doneH = Step(sv.HorizontalOffset, ref st.TargetH, Math.Min(st.TargetH, sv.ScrollableWidth), sv.ScrollToHorizontalOffset);
            if (doneV && doneH) Animating.Remove(sv);
        }

        if (Animating.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private static bool Step(double current, ref double target, double clamped, Action<double> scrollTo)
    {
        if (double.IsNaN(target)) return true;
        double diff = clamped - current;
        if (Math.Abs(diff) < 0.5)
        {
            scrollTo(clamped);
            target = double.NaN;
            return true;
        }
        scrollTo(current + diff * Easing);
        return false;
    }
}
