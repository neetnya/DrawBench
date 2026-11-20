using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace DrawBench.Modules.Metadata.Helpers;

public static class BitmapHelper
{
    /// <summary>
    /// 安全加载图片为冻结的 BitmapImage（FileShare.ReadWrite 不与出图软件冲突，按长边只缩小不放大）。
    /// </summary>
    public static BitmapImage? TryLoad(string path, int? maxLongEdge = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            int decodeW = 0;
            int decodeH = 0;
            if (maxLongEdge is > 0)
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count > 0)
                {
                    int pw = decoder.Frames[0].PixelWidth;
                    int ph = decoder.Frames[0].PixelHeight;
                    if (pw > maxLongEdge.Value || ph > maxLongEdge.Value)
                    {
                        if (pw >= ph) decodeW = maxLongEdge.Value;
                        else decodeH = maxLongEdge.Value;
                    }
                }
                stream.Position = 0;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeW > 0) bitmap.DecodePixelWidth = decodeW;
            else if (decodeH > 0) bitmap.DecodePixelHeight = decodeH;

            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
