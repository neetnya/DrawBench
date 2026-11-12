using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DrawBench.Core;
using DrawBench.Core.Images;
using DrawBench.Core.Logging;
using DrawBench.Core.Python;
using DrawBench.Core.Ui;

namespace DrawBench.Modules.Collage.Views;

/// <summary>
/// 对比图模块：按模型文件夹横向拼图，排版交给 gen_sheets_cli.py（Pillow）。
/// 与旧版的区别：Python 探测走统一的 PythonLocator，参数存进统一 settings.json，
/// 脚本从 scripts\ 找（原先在 exe 同级）。
/// </summary>
public partial class CollageView : UserControl
{
    private const string DefaultOutputFolder = "_output";
    private const int MaxLogLength = 400_000;

    private readonly SettingsStore _store;
    private CollageSettings _settings;
    private readonly List<ModelInfo> _models = new();

    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public CollageView(SettingsStore store)
    {
        _store = store;
        _settings = store.Get<CollageSettings>(ModuleIds.Collage) ?? new CollageSettings();

        InitializeComponent();
    }

    // ==================== 生命周期 ====================

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= View_Loaded;

        ApplySettings(_settings);
        AppendLog("[INFO] 就绪。参数会自动保存，下次启动沿用。");

        // 优先用共享的当前图片目录
        string shared = SharedContext.Current.CurrentImageFolder;
        if (shared.Length > 0 && Directory.Exists(shared))
            TxtBase.Text = shared;

        if (!string.IsNullOrWhiteSpace(TxtBase.Text) && Directory.Exists(TxtBase.Text.Trim()))
            RefreshModels();
    }

    /// <summary>应用退出时调用，把当前参数存盘。</summary>
    public void SaveSettings()
    {
        try
        {
            _settings = CollectSettings();
            _store.Set(ModuleIds.Collage, _settings);
        }
        catch
        {
            // 保存失败不阻塞
        }
    }

    // ==================== 目录与模型 ====================

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "请选择包含各个模型文件夹的根目录"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                SetBaseDirectory(dialog.FolderName);
        }
        catch (Exception ex)
        {
            UiHost.Error("选择文件夹失败：" + ex.Message);
        }
    }

    private void SetBaseDirectory(string path)
    {
        TxtBase.Text = path;
        TxtBase.ToolTip = path;
        _models.Clear();
        AppendLog($"[INFO] 已选择目录：{path}");
        SharedContext.Current.CurrentImageFolder = path;
        RefreshModels();
    }

    private void View_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void View_Drop(object sender, DragEventArgs e)
    {
        if (_isRunning || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items) return;

        var directory = items.FirstOrDefault(Directory.Exists);
        if (directory == null)
        {
            AppendLog("[WARN] 拖入的内容不是文件夹。");
            return;
        }

        SetBaseDirectory(directory);
    }

    private void RefreshModels()
    {
        string basePath = TxtBase.Text.Trim();
        var discovered = new List<string>();

        if (Directory.Exists(basePath))
        {
            try
            {
                discovered = new DirectoryInfo(basePath).GetDirectories()
                    .Where(d => !d.Name.StartsWith(".", StringComparison.Ordinal)
                                && !d.Name.StartsWith("_", StringComparison.Ordinal))
                    .Select(d => d.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] 无法读取目录：{ex.Message}");
            }
        }

        var previousOrder = _models.Select(m => m.Name).ToList();
        var previousSelected = _models.Where(m => m.IsSelected).Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hadPrevious = previousOrder.Count > 0;

        var available = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = previousOrder.Where(name => available.Contains(name)).ToList();
        var newcomers = discovered.Where(name => !ordered.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        newcomers.Sort(NaturalComparer.Instance);
        ordered.AddRange(newcomers);

        _models.Clear();
        LstModels.ItemsSource = null;

        foreach (var name in ordered)
        {
            var info = new ModelInfo(name) { IsSelected = !hadPrevious || previousSelected.Contains(name) };
            info.PropertyChanged += (_, _) => UpdateModelCount();
            _models.Add(info);
        }

        LstModels.ItemsSource = _models;
        UpdateModelCount();
    }

    private void BtnRefreshModels_Click(object sender, RoutedEventArgs e) => RefreshModels();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var model in _models) model.IsSelected = true;
        UpdateModelCount();
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var model in _models) model.IsSelected = false;
        UpdateModelCount();
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        int index = LstModels.SelectedIndex;
        if (index < 0) return;

        int target = index + delta;
        if (target < 0 || target >= _models.Count) return;

        var item = _models[index];
        _models.RemoveAt(index);
        _models.Insert(target, item);
        LstModels.SelectedIndex = target;
        LstModels.ScrollIntoView(item);
    }

    private void UpdateModelCount()
    {
        TxtModelCount.Text = _models.Count == 0
            ? "没有找到模型文件夹（以 . 或 _ 开头的文件夹会被忽略）。"
            : $"共 {_models.Count} 个模型，已勾选 {_models.Count(m => m.IsSelected)} 个。";
    }

    // ==================== 执行 ====================

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        string basePath = TxtBase.Text.Trim();
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
        {
            UiHost.Warn("请先选择有效的图片根目录！");
            return;
        }

        if (_models.Count > 0 && _models.All(m => !m.IsSelected))
        {
            UiHost.Warn("请至少勾选一个模型。");
            return;
        }

        if (!TryGetInt(CmbTile.Text, 64, 4096, "单元格边长", out int tile)) return;
        if (!TryGetInt(CmbRows.Text, 0, 500, "每张行数", out int rows)) return;
        if (!TryGetInt(TxtGap.Text, 0, 200, "间距", out int gap)) return;
        if (!TryGetInt(TxtQuality.Text, 1, 100, "画质", out int quality)) return;

        string scriptPath = PythonRunner.ResolveScript("gen_sheets_cli.py", AppPaths.Current.ScriptsDir);
        if (!File.Exists(scriptPath))
        {
            UiHost.Error($"找不到拼图脚本：\n{scriptPath}\n\n发布包里应在程序目录的 scripts\\ 下。", "缺少文件");
            return;
        }

        var python = PythonLocator.Resolve(CurrentPythonOverride());
        if (!python.Found)
        {
            UiHost.Error("找不到 Python。\n请安装 Python 3.8 以上版本并把 python.exe 加入 PATH，或在「设置」里手动指定。", "Python 未找到");
            return;
        }

        var args = BuildArguments(basePath, tile, rows, gap, quality);

        SaveSettings();
        SetRunning(true);
        TxtLog.Clear();
        ResetProgress();
        AppendLog($"[INFO] Python：{python.Describe()}");
        AppendLog($"[INFO] 脚本：{scriptPath}");
        AppendLog($"[INFO] 参数：{string.Join(" ", args.Select(QuoteIfNeeded))}");

        _cts = new CancellationTokenSource();

        try
        {
            string outputDir = ResolveOutputDirectory();

            var result = await PythonRunner.RunAsync(
                python,
                scriptPath,
                args,
                onLine: HandleOutputLine,
                workingDirectory: Path.GetDirectoryName(scriptPath),
                ct: _cts.Token);

            if (result.Failure != null)
            {
                AppendLog("[ERROR] " + result.Failure);
                UiHost.Error(result.Failure, "生成失败");
                return;
            }

            if (result.ExitCode == 0)
            {
                if (ChkPreview.IsChecked == true)
                {
                    AppendLog("[SUCCESS] 预览完成，没有写入任何文件。");
                    return;
                }

                AppendLog("[SUCCESS] 处理完成。");
                AppendLog($"[INFO] 输出目录：{outputDir}");

                if (ChkOpenAfter.IsChecked == true && Directory.Exists(outputDir))
                    AppPaths.OpenInExplorer(outputDir);
            }
            else
            {
                AppendLog($"[ERROR] Python 进程异常退出，代码：{result.ExitCode}");
                if (!string.IsNullOrWhiteSpace(result.StdErr))
                    AppendLog(result.StdErr.TrimEnd());
                UiHost.Warn($"生成失败，退出代码：{result.ExitCode}\n请查看下方日志。", "生成失败");
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
            UpdateModelCount();
        }
    }

    /// <summary>拼图模块自己填的 Python 路径优先，其次是全局设置里的。</summary>
    private string? CurrentPythonOverride()
    {
        string local = TxtPython.Text.Trim();
        if (local.Length > 0) return local;

        var app = SettingsStore.Shared.Get<AppSection>("app");
        return app?.PythonPath;
    }

    private List<string> BuildArguments(string basePath, int tile, int rows, int gap, int quality)
    {
        string fit = CmbFit.SelectedIndex == 1 ? "cover" : "contain";
        string format = CmbFormat.SelectedIndex switch
        {
            1 => "jpg",
            2 => "webp",
            _ => "png"
        };
        string match = CmbMatch.SelectedIndex == 1 ? "stem" : "full";

        var args = new List<string>
        {
            basePath,
            "--tile", tile.ToString(),
            "--rows", rows.ToString(),
            "--gap", gap.ToString(),
            "--fit", fit,
            "--format", format,
            "--quality", quality.ToString(),
            "--match", match,
            "--out", TxtOutDir.Text.Trim()
        };

        var selected = _models.Where(m => m.IsSelected).Select(m => m.Name).ToList();
        if (selected.Count > 0)
        {
            args.Add("--models");
            args.Add(string.Join(",", selected));
        }

        if (ChkOnlyCommon.IsChecked == true) args.Add("--only-common");
        if (ChkSkipSingle.IsChecked != true) args.Add("--keep-single");
        if (ChkHeader.IsChecked != true) args.Add("--no-header");
        if (ChkLabel.IsChecked != true) args.Add("--no-label");
        if (ChkClean.IsChecked == true) args.Add("--clean");
        if (ChkPreview.IsChecked == true) args.Add("--dry-run");

        return args;
    }

    private void HandleOutputLine(string line, bool isError)
    {
        if (!isError && line.StartsWith("[PROGRESS] ", StringComparison.Ordinal))
        {
            var parts = line["[PROGRESS] ".Length..].Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int done)
                && int.TryParse(parts[1], out int total)
                && total > 0)
            {
                void Update()
                {
                    PrgBar.Maximum = total;
                    PrgBar.Value = Math.Min(done, total);
                    TxtProgress.Text = $"{Math.Min(done, total)}/{total}";
                }

                if (Dispatcher.CheckAccess()) Update();
                else Dispatcher.BeginInvoke(new Action(Update));
            }
            return;
        }

        AppendLog(isError ? "[STDERR] " + line : line);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning || _cts == null) return;
        AppendLog("[INFO] 正在取消任务……");
        _cts.Cancel();
    }

    private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        string outputDir = ResolveOutputDirectory();
        if (!Directory.Exists(outputDir))
        {
            UiHost.Info($"输出目录还不存在：\n{outputDir}");
            return;
        }

        AppPaths.OpenInExplorer(outputDir);
    }

    private string ResolveOutputDirectory()
    {
        string basePath = TxtBase.Text.Trim();
        string folder = TxtOutDir.Text.Trim();
        if (folder.Length == 0) folder = DefaultOutputFolder;

        try
        {
            return Path.GetFullPath(Path.Combine(basePath, folder));
        }
        catch
        {
            return Path.Combine(basePath, folder);
        }
    }

    private static string QuoteIfNeeded(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private bool TryGetInt(string raw, int min, int max, string label, out int value)
    {
        value = 0;
        if (!int.TryParse(raw?.Trim(), out int parsed))
        {
            UiHost.Warn($"{label} 需要填写整数。", "参数错误");
            return false;
        }

        if (parsed < min || parsed > max)
        {
            UiHost.Warn($"{label} 需要在 {min} ~ {max} 之间。", "参数错误");
            return false;
        }

        value = parsed;
        return true;
    }

    // ==================== 界面状态 ====================

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }

        if (TxtLog.Text.Length > MaxLogLength)
            TxtLog.Text = TxtLog.Text[^(MaxLogLength / 2)..];

        TxtLog.AppendText(message + Environment.NewLine);
        TxtLog.ScrollToEnd();
    }

    private void ResetProgress()
    {
        PrgBar.Maximum = 100;
        PrgBar.Value = 0;
        TxtProgress.Text = string.Empty;
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        BtnRun.IsEnabled = !running;
        BtnBrowse.IsEnabled = !running;
        BtnCancel.IsEnabled = running;
        BtnRun.Content = running ? "正在生成，请稍候……" : "生成对比图";
    }

    // ==================== 配置 ====================

    private CollageSettings CollectSettings() => new()
    {
        LastDirectory = TxtBase.Text.Trim(),
        TileSize = int.TryParse(CmbTile.Text.Trim(), out int tile) ? tile : 500,
        Rows = int.TryParse(CmbRows.Text.Trim(), out int rows) ? rows : 3,
        Gap = int.TryParse(TxtGap.Text.Trim(), out int gap) ? gap : 8,
        Quality = int.TryParse(TxtQuality.Text.Trim(), out int quality) ? quality : 92,
        FitMode = CmbFit.SelectedIndex,
        Format = CmbFormat.SelectedIndex,
        Match = CmbMatch.SelectedIndex,
        OutputFolder = TxtOutDir.Text.Trim(),
        PythonPath = TxtPython.Text.Trim(),
        ShowHeader = ChkHeader.IsChecked == true,
        ShowLabels = ChkLabel.IsChecked == true,
        OnlyCommon = ChkOnlyCommon.IsChecked == true,
        SkipSingle = ChkSkipSingle.IsChecked == true,
        Clean = ChkClean.IsChecked == true,
        OpenAfter = ChkOpenAfter.IsChecked == true
    };

    private void ApplySettings(CollageSettings settings)
    {
        TxtBase.Text = settings.LastDirectory ?? string.Empty;
        TxtBase.ToolTip = TxtBase.Text;
        CmbTile.Text = Math.Clamp(settings.TileSize, 64, 4096).ToString();
        CmbRows.Text = Math.Clamp(settings.Rows, 0, 500).ToString();
        TxtGap.Text = Math.Clamp(settings.Gap, 0, 200).ToString();
        TxtQuality.Text = Math.Clamp(settings.Quality, 1, 100).ToString();
        CmbFit.SelectedIndex = Math.Clamp(settings.FitMode, 0, 1);
        CmbFormat.SelectedIndex = Math.Clamp(settings.Format, 0, 2);
        CmbMatch.SelectedIndex = Math.Clamp(settings.Match, 0, 1);
        TxtOutDir.Text = string.IsNullOrWhiteSpace(settings.OutputFolder) ? DefaultOutputFolder : settings.OutputFolder;
        TxtPython.Text = settings.PythonPath ?? string.Empty;
        ChkHeader.IsChecked = settings.ShowHeader;
        ChkLabel.IsChecked = settings.ShowLabels;
        ChkOnlyCommon.IsChecked = settings.OnlyCommon;
        ChkSkipSingle.IsChecked = settings.SkipSingle;
        ChkClean.IsChecked = settings.Clean;
        ChkOpenAfter.IsChecked = settings.OpenAfter;
    }

    private sealed class ModelInfo : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public ModelInfo(string name) => Name = name;

        public string Name { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
