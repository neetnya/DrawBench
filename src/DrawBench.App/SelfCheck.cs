using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawBench.Core;
using DrawBench.Core.Logging;

namespace DrawBench.App;

/// <summary>
/// 开发期自检：DrawBench.exe --render &lt;输出目录&gt; [--shots]
/// 依次切到每个模块，把窗口截图保存下来，然后退出。用来在无人值守时检查界面是否正常。
/// 只在带 --render 参数时启用，正常使用不会触发。
/// </summary>
internal static class SelfCheck
{
    public static bool WantsRender(string[] args)
        => args.Any(a => a.Equals("--render", StringComparison.OrdinalIgnoreCase));

    public static string? OutputDir(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--render", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>逐个模块截图。结束返回 true。</summary>
    public static void Run(MainWindow window, string outputDir, IEnumerable<string> moduleIds)
    {
        try { Directory.CreateDirectory(outputDir); } catch { }

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = 1400;
        window.Height = 880;

        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                await Task.Delay(400);

                foreach (string id in moduleIds)
                {
                    window.NavigateTo(id);
                    await Task.Delay(900);

                    string path = Path.Combine(outputDir, "module-" + id + ".png");
                    SaveWindow(window, path);
                }

                await Task.Delay(300);
                window.NavigateToSettings();
                await Task.Delay(700);
                SaveWindow(window, Path.Combine(outputDir, "module-settings.png"));

                AppLog.Info("自检截图完成：" + outputDir);
            }
            catch (Exception ex)
            {
                AppLog.Error("自检截图失败", ex);
            }
            finally
            {
                Application.Current.Shutdown(0);
            }
        }));
    }

    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();

        int w = (int)Math.Ceiling(window.ActualWidth > 0 ? window.ActualWidth : window.Width);
        int h = (int)Math.Ceiling(window.ActualHeight > 0 ? window.ActualHeight : window.Height);
        if (w <= 0) w = 1400;
        if (h <= 0) h = 880;

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
