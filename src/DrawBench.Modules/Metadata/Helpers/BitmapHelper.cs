using System.IO;
using System.Windows.Media.Imaging;

namespace DrawBench.Modules.Metadata.Helpers;

public static class BitmapHelper
{
    /// <summary>安全加载图片为冻结的 BitmapImage（不锁文件，可跨线程）。</summary>
    public static BitmapImage? TryLoad(string path, int? decodeWidth = null)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth is > 0) bitmap.DecodePixelWidth = decodeWidth.Value;

            using (var stream = File.OpenRead(path))
            {
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
