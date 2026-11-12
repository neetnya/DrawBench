using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DrawBench.Core;
using DrawBench.Core.Ui;
using DrawBench.Modules.Metadata.ViewModels;

namespace DrawBench.Modules.Metadata.Views;

/// <summary>
/// 参数读取模块的界面。原来是独立窗口（最大化显示），
/// 这里改成 UserControl 住进外壳的内容区，其余交互不变。
/// </summary>
public partial class ImageView : UserControl
{
    private readonly MainViewModel _vm = new();

    public ImageView()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnViewLoaded;
    }

    public MainViewModel ViewModel => _vm;

    private async void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnViewLoaded;

        try
        {
            // 优先用共享的「当前图片目录」——在别的模块里选过目录就直接读它
            string? folder = SharedContext.Current.CurrentImageFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                folder = FindImageFolder(Environment.CurrentDirectory);

            if (!string.IsNullOrEmpty(folder))
            {
                _vm.FolderPath = folder!;
                await _vm.LoadFolderAsync(folder!);
            }
        }
        catch (Exception ex)
        {
            UiHost.Warn("初始化失败：" + ex.Message);
        }
    }

    private static string? FindImageFolder(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (MainViewModel.SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    return directory;
            }
        }
        catch
        {
            // 忽略无权限目录
        }

        return null;
    }

    // ---------------------------------------------------------------- 扫描

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选一个放着 AI 图片的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            _vm.FolderPath = dialog.FolderName;
            SharedContext.Current.CurrentImageFolder = dialog.FolderName;
            await _vm.LoadFolderAsync(dialog.FolderName);
        }
        catch (Exception ex)
        {
            UiHost.Warn(ex.Message, "读取失败");
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.LoadFolderAsync(_vm.FolderPath);
        }
        catch (Exception ex)
        {
            UiHost.Warn(ex.Message, "读取失败");
        }
    }

    // ---------------------------------------------------------------- 复制 / 发送

    private void CopyPositive_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(_vm.Selected?.PositivePrompt);

    private void CopyNegative_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(_vm.Selected?.NegativePromptText);

    /// <summary>把读到的正向提示词送到批量提交模块的队列里。</summary>
    private void SendPositiveToBatch_Click(object sender, RoutedEventArgs e) => SendToBatch(_vm.Selected?.PositivePrompt);

    private void SendNegativeToBatch_Click(object sender, RoutedEventArgs e) => SendToBatch(_vm.Selected?.NegativePromptText, negative: true);

    private void SendToBatch(string? text, bool negative = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            UiHost.Info("这里还没有内容可以发送呢～");
            return;
        }

        bool ok = negative
            ? AiBridge.SendToBatchNegative(text, "参数读取")
            : AiBridge.SendToBatchQueue(text, "参数读取");

        if (!ok) UiHost.Warn("批量提交模块没有接收这段文本。");
    }

    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            UiHost.Info("这里还没有内容可以复制呢～");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _vm.StatusText = "已经复制到剪贴板啦～";
        }
        catch (Exception ex)
        {
            UiHost.Warn("复制失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 其他交互

    private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => RevealSelected();

    private void Reveal_Click(object sender, RoutedEventArgs e)
        => RevealSelected();

    private void RevealSelected()
    {
        var path = _vm.Selected?.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _vm.StatusText = "打开资源管理器失败：" + ex.Message;
        }
    }

    private void View_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void View_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        try
        {
            string target = paths[0];
            if (File.Exists(target)) target = Path.GetDirectoryName(target) ?? target;
            if (!Directory.Exists(target)) return;

            _vm.FolderPath = target;
            SharedContext.Current.CurrentImageFolder = target;
            await _vm.LoadFolderAsync(target);
        }
        catch (Exception ex)
        {
            _vm.StatusText = "拖入的路径读不了：" + ex.Message;
        }
    }
}
