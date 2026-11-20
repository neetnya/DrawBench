using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawBench.Core;
using DrawBench.Core.Logging;
using DrawBench.Core.Python;

namespace DrawBench.App.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsStore _store;
    private readonly AppSection _section;
    private CancellationTokenSource? _detectCts;

    public SettingsView(SettingsStore store, AppSection section)
    {
        _store = store;
        _section = section;

        InitializeComponent();

        var current = Current;
        TxtPython.Text = current.PythonPath ?? string.Empty;
        TxtDataDir.Text = AppPaths.Current.AppDir;

        Loaded += (_, _) => _ = DetectPythonAsync(forceRefresh: false);
        Unloaded += (_, _) => CancelDetect();
    }

    private AppSection Current => _store.Get<AppSection>("app") ?? _section;

    private string? NormalizedManualPath()
    {
        string text = (TxtPython.Text ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private void SaveCurrentPythonPath()
    {
        var section = Current;
        string? path = NormalizedManualPath();
        if (string.Equals(section.PythonPath, path, StringComparison.Ordinal)) return;

        section.PythonPath = path;
        _store.Set("app", section);
    }

    // ==================== Python ====================

    private void TxtPython_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveCurrentPythonPath();
    }

    private void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 python.exe",
            Filter = "Python 解释器|python*.exe|所有程序|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            TxtPython.Text = dialog.FileName;
            SaveCurrentPythonPath();
            _ = DetectPythonAsync(forceRefresh: true);
        }
    }

    private void Redetect_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentPythonPath();
        _ = DetectPythonAsync(forceRefresh: true);
    }

    private async Task DetectPythonAsync(bool forceRefresh)
    {
        CancelDetect();
        var cts = new CancellationTokenSource();
        _detectCts = cts;

        string? manualPath = NormalizedManualPath();
        BtnRedetect.IsEnabled = false;
        TxtPythonHint.Text = "正在检测 Python…";
        TxtPythonHint.Foreground = TryGetBrush("Muted");

        try
        {
            var python = forceRefresh
                ? await PythonLocator.RefreshAsync(manualPath, cts.Token)
                : await PythonLocator.ResolveAsync(manualPath, cts.Token);

            if (!ReferenceEquals(_detectCts, cts)) return;
            ShowPython(python, manualPath);
        }
        catch (OperationCanceledException)
        {
            // 页面切换或新一轮检测取代
        }
        catch (Exception ex)
        {
            AppLog.Error("检测 Python 失败", ex);
            if (ReferenceEquals(_detectCts, cts))
            {
                TxtPythonHint.Text = "检测失败：" + ex.Message;
                TxtPythonHint.Foreground = TryGetBrush("Danger");
            }
        }
        finally
        {
            if (ReferenceEquals(_detectCts, cts))
                BtnRedetect.IsEnabled = true;
        }
    }

    private void CancelDetect()
    {
        var old = _detectCts;
        _detectCts = null;
        if (old != null)
        {
            try { old.Cancel(); } catch { }
            old.Dispose();
        }
    }

    private void ShowPython(PythonInterpreter python, string? manualPath)
    {
        // 不反写 TxtPython.Text，保留用户的原始配置（留空 = 自动探测）
        if (python.Found)
        {
            string mode = string.IsNullOrEmpty(manualPath) ? "自动探测" : "已指定";
            TxtPythonHint.Text = $"已找到（{mode}）：{python.Describe()}";
            TxtPythonHint.Foreground = TryGetBrush("Muted");
        }
        else
        {
            TxtPythonHint.Text = "未找到 Python。拼图等脚本功能需要 Python 3.8+，装好并把 python.exe 加进 PATH，或在上面的框里指定路径。";
            TxtPythonHint.Foreground = TryGetBrush("Danger");
        }
    }

    private Brush TryGetBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;

    // ==================== 目录 ====================

    private void OpenData_Click(object sender, RoutedEventArgs e)
        => AppPaths.OpenInExplorer(AppPaths.Current.DataDir);

    private void OpenApi_Click(object sender, RoutedEventArgs e)
        => AppPaths.OpenInExplorer(AppPaths.Current.ApiDir);

    private void OpenModels_Click(object sender, RoutedEventArgs e)
        => AppPaths.OpenInExplorer(AppPaths.Current.ModelsDir);
}
