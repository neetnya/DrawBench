using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            var settings = SettingsStore.Shared.Get<MetadataSettings>(ModuleIds.Metadata);
            if (settings != null)
                _vm.IncludeSubFolders = settings.IncludeSubFolders;

            string? folder = settings?.LastFolder;
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

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Shared.Set(ModuleIds.Metadata, new MetadataSettings
            {
                LastFolder = string.IsNullOrWhiteSpace(_vm.FolderPath) ? null : _vm.FolderPath.Trim(),
                IncludeSubFolders = _vm.IncludeSubFolders
            });
        }
        catch
        {
            // 保存失败不阻塞
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
            SaveSettings();
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
            SaveSettings();
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

    /// <summary>把读到的正向/负向提示词发送或追加到批量提交模块。</summary>
    private void SendPositiveToBatch_Click(object sender, RoutedEventArgs e)
        => DeliverToBatch(_vm.Selected?.PositivePrompt, negative: false, append: false);

    private void AppendPositiveToBatch_Click(object sender, RoutedEventArgs e)
        => DeliverToBatch(_vm.Selected?.PositivePrompt, negative: false, append: true);

    private void SendNegativeToBatch_Click(object sender, RoutedEventArgs e)
        => DeliverToBatch(_vm.Selected?.NegativePromptText, negative: true, append: false);

    private void AppendNegativeToBatch_Click(object sender, RoutedEventArgs e)
        => DeliverToBatch(_vm.Selected?.NegativePromptText, negative: true, append: true);

    private void DeliverToBatch(string? text, bool negative, bool append)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            UiHost.Info(append ? "还没有可追加的内容。" : "还没有可发送的内容。");
            return;
        }

        bool ok = (negative, append) switch
        {
            (false, false) => AiBridge.SendToBatchQueue(text, "参数读取"),
            (false, true)  => AiBridge.AppendToBatchQueue(text, "参数读取"),
            (true, false)  => AiBridge.SendToBatchNegative(text, "参数读取"),
            (true, true)   => AiBridge.AppendToBatchNegative(text, "参数读取"),
        };

        if (!ok)
        {
            UiHost.Warn("批量提交模块没有接收这段文本。");
            return;
        }

        if (append)
        {
            _vm.StatusText = negative ? "已追加到批量提交的负向提示词" : "已追加到批量提交的正向队列";
        }
        else
        {
            _vm.StatusText = negative ? "已发送到批量提交的负向提示词" : "已发送到批量提交的正向队列";
            SharedContext.Current.RequestModule(ModuleIds.Batch);
        }
    }

    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            UiHost.Info("还没有可复制的内容。");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _vm.StatusText = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            UiHost.Warn("复制失败：" + ex.Message);
        }
    }

    /// <summary>生成参数表只复制值：选中的每一行取「值」列，一行一个，不带项目名和表头。</summary>
    private void CopyParams_Click(object sender, RoutedEventArgs e) => CopyParamValues();

    private void ParamsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        e.Handled = true;
        CopyParamValues();
    }

    private void CopyParamValues()
    {
        var rows = ParamsGrid.SelectedItems.OfType<KeyValuePair<string, string>>().ToList();
        if (rows.Count == 0)
            rows = _vm.Selected?.Params.ParameterItems.ToList() ?? new List<KeyValuePair<string, string>>();

        var values = rows.Where(kv => kv.Value.Length > 0).Select(kv => kv.Value).ToList();
        if (values.Count == 0)
        {
            UiHost.Info("还没有可复制的内容。");
            return;
        }

        CopyToClipboard(string.Join(Environment.NewLine, values));
    }

    // ---------------------------------------------------------------- 导出

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasImages)
        {
            UiHost.Info("当前列表没有可导出的图片。");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出参数为 CSV",
            Filter = "CSV 表格|*.csv|所有文件|*.*",
            FileName = "metadata_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            _vm.ExportCsv(dlg.FileName);
        }
        catch (Exception ex)
        {
            UiHost.Warn("导出 CSV 失败：" + ex.Message);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasImages)
        {
            UiHost.Info("当前列表没有可导出的图片。");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出参数为 JSON",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            FileName = "metadata_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json"
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            _vm.ExportJson(dlg.FileName);
        }
        catch (Exception ex)
        {
            UiHost.Warn("导出 JSON 失败：" + ex.Message);
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
            SaveSettings();
            await _vm.LoadFolderAsync(target);
        }
        catch (Exception ex)
        {
            _vm.StatusText = "拖入的路径读不了：" + ex.Message;
        }
    }
}
