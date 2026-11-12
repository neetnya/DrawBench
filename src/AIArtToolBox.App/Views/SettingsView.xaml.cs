using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIArtToolBox.Core;
using AIArtToolBox.Core.Comfy;
using AIArtToolBox.Core.Logging;
using AIArtToolBox.Core.Python;
using AIArtToolBox.Core.Ui;

namespace AIArtToolBox.App.Views;

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
        TxtComfyUrl.Text = current.ComfyBaseUrl;
        TxtPython.Text = current.PythonPath ?? string.Empty;
        TxtDataDir.Text = AppPaths.Current.AppDir;
        ShowPython(PythonLocator.Resolve(current.PythonPath));

        TxtAbout.Text =
            "AIArtToolBox —— 把提示词抽卡、批量提交、参数回读、WD14 反推、多模型对比图五件事装进一个窗口。\n" +
            "提示词抽卡与 WD14 反推源自 MIT 许可的开源项目，详见程序目录里的 THIRD-PARTY-NOTICES.md。";
    }

    private AppSection Current => _store.Get<AppSection>("app") ?? _section;

    private void Save(AppSection section)
    {
        _store.Set("app", section);
        _shell.UpdateStatusBar();
    }

    // ==================== ComfyUI ====================

    private void TestComfy_Click(object sender, RoutedEventArgs e)
    {
        string url = ComfyClient.Normalize(TxtComfyUrl.Text);

        var section = Current;
        section.ComfyBaseUrl = url;
        Save(section);
        TxtComfyUrl.Text = url;

        BtnTestComfy.IsEnabled = false;
        BtnTestComfy.Content = "测试中…";

        SetComfyHint($"正在连接 {url} …", false);

        var client = new ComfyClient(url);
        client.GetQueueAsync().ContinueWith(task =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BtnTestComfy.IsEnabled = true;
                BtnTestComfy.Content = "测试连接";

                var status = task.Result;
                if (status.Reachable)
                {
                    SetComfyHint($"连接正常。当前队列：运行中 {status.Running}，等待 {status.Pending}。", false);
                    _shell.SetStatus("ComfyUI 连接正常。");
                }
                else
                {
                    SetComfyHint($"连不上 {url}。请确认 ComfyUI 已启动并开了 --listen 以外的本机端口。", true);
                    _shell.SetStatus("ComfyUI 连接失败。");
                }
            }));
        });
    }

    private void SetComfyHint(string text, bool isError)
    {
        TxtComfyHint.Text = text;
        TxtComfyHint.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "SubTextBrush");
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

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = AppLog.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            UiHost.Error("打不开日志文件：" + ex.Message);
        }
    }
}
