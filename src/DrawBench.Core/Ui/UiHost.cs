using System;
using System.Windows;

namespace DrawBench.Core.Ui;

/// <summary>
/// 弹窗统一入口。自动调度回 WPF UI 线程，由外壳提供当前窗口作为父窗口。
/// </summary>
public static class UiHost
{
    /// <summary>外壳启动时注册，返回当前活动窗口（可能为 null）。</summary>
    public static Func<Window?>? OwnerProvider { get; set; }

    private static Window? Owner
    {
        get
        {
            try { return OwnerProvider?.Invoke(); } catch { return null; }
        }
    }

    public static void Info(string message, string title = "提示")
        => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(string message, string title = "提示")
        => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Error(string message, string title = "出错了")
        => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(string message, string title = "确认")
        => Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
        {
            return dispatcher.Invoke(() => ShowOnUiThread(message, title, buttons, image));
        }

        return ShowOnUiThread(message, title, buttons, image);
    }

    private static MessageBoxResult ShowOnUiThread(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Owner;
        if (owner != null && owner.IsVisible)
            return MessageBox.Show(owner, message, title, buttons, image);

        return MessageBox.Show(message, title, buttons, image);
    }
}
