using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ZenTube.Services;

/// <summary>
/// サムネイルのURLを、表示する大きさに縮めて読み込んだ画像に変える。
/// 元画像（1000px以上のこともある）をそのまま展開するとメモリと処理が重くなるため。
/// ConverterParameter に横幅（ピクセル）を渡す。
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    // 同じ画像をスクロールのたびに読み直さないよう、使われている間は使い回す
    private static readonly Dictionary<string, WeakReference<BitmapImage>> Cache = [];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        int width = int.TryParse(parameter as string, out var w) && w > 0 ? w : 440;
        string key = width + "|" + url;
        if (Cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
            return cached;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = width;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();

            if (Cache.Count > 3000) PruneCache();
            Cache[key] = new WeakReference<BitmapImage>(bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void PruneCache()
    {
        foreach (var k in Cache.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList())
            Cache.Remove(k);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
