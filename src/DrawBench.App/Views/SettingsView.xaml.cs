using System;
using System.Windows;
using System.Windows.Controls;
using DrawBench.Core;
using DrawBench.Core.Python;

namespace DrawBench.App.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsStore _store;
    private readonly AppSection _section;
    private readonly MainWindow _shell;

    public SettingsView(SettingsStore store, AppSection section, MainWindow shell)
    {
        _store = store;
        _section = section;
        _shell = shell;

        InitializeComponent();

        var current = Current;
        TxtPython.Text = current.PythonPath ?? string.Empty;
        TxtDataDir.Text = AppPaths.Current.AppDir;
        ShowPython(PythonLocator.Resolve(current.PythonPath));
    }

    private AppSection Current => _store.Get<AppSection>("app") ?? _section;

    private void Save(AppSection section)
    {
        _store.Set("app", section);
        _shell.UpdateStatusBar();
    }

    // ==================== Python ====================

    private void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 python.exe",
            Filter = "Python 解释器|python*.exe|所有程序|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            TxtPython.Text = dialog.FileName;
    }

    private void Redetect_Click(object sender, RoutedEventArgs e)
    {
        var section = Current;
        section.PythonPath = string.IsNullOrWhiteSpace(TxtPython.Text) ? null : TxtPython.Text.Trim();
        Save(section);

        var python = PythonLocator.Refresh(section.PythonPath);
        ShowPython(python);
        _shell.SetStatus(python.Found ? "Python 就绪：" + python.Describe() : "未找到 Python。");
    }

    private void ShowPython(PythonInterpreter python)
    {
        TxtPython.Text = python.Found ? python.Exe : string.Empty;
        TxtPythonHint.Text = python.Found
            ? "已找到：" + python.Describe()
            : "未找到 Python。抽卡与拼图功能需要 Python 3.8+，装好并把 python.exe 加进 PATH，或在上面的框里指定路径。";
        TxtPythonHint.Foreground = (System.Windows.Media.Brush)FindResource(python.Found ? "SubTextBrush" : "DangerBrush");
    }

    // ==================== 目录 ====================

    private void OpenData_Click(object sender, RoutedEventArgs e)
        => AppPaths.OpenInExplorer(AppPaths.Current.DataDir);

    private void OpenApi_Click(object sender, RoutedEventArgs e)
        => AppPaths.OpenInExplorer(AppPaths.Current.ApiDir);

    private void OpenModels_Click(object sender, RoutedEventArgs e)
        => AppPaths.OpenInExplorer(AppPaths.Current.ModelsDir);
}
