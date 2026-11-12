using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawBench.Core;
using DrawBench.Core.Logging;
using DrawBench.Core.Python;
using DrawBench.Core.Ui;

namespace DrawBench.App;

/// <summary>
/// 外壳：左侧竖排导航 + 右侧内容区 + 底部状态栏。
/// 模块视图首次进入时创建，之后一直保留（切走不销毁，避免丢掉队列/日志）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private readonly AppSection _appSection;
    private readonly Dictionary<string, IToolModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RadioButton> _navButtons = new();

    private string? _activeModuleId;

    public MainWindow(SettingsStore store, AppSection appSection)
    {
        _store = store;
        _appSection = appSection;

        InitializeComponent();

        // 版本号只显示到语义化版本，后面那串 +commit 太长，放在侧栏会被截断
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "1.0.0";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        TxtVersion.Text = "v" + version;

        AppLog.Line += OnLogLine;
        SharedContext.Current.ModuleNavigationRequested += OnModuleNavigationRequested;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        foreach (var module in ReflectiveModuleFactory.CreateAll())
            Register(module);
    }

    // ==================== 模块注册与导航 ====================

    private void Register(IToolModule module)
    {
        if (_modules.ContainsKey(module.Id)) return;
        _modules[module.Id] = module;

        var button = new RadioButton
        {
            Style = (Style)FindResource("NavButton"),
            GroupName = "Nav",
            Tag = module.Id,
            ToolTip = module.Description,
        };
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = module.Glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 17,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                },
                new TextBlock
                {
                    Text = module.Title,
                    FontSize = 14.5,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        button.Checked += (_, _) => Activate(module.Id);

        _navButtons.Add(button);
        NavPanel.Children.Add(button);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateStatusBar();

        // 启动时停在导航里的第一个模块
        var target = _navButtons.FirstOrDefault()?.Tag as string;
        if (target != null)
        {
            var button = _navButtons.FirstOrDefault(b => (string)b.Tag == target);
            if (button != null) button.IsChecked = true;
        }

        AppLog.Info($"界面已启动（数据目录 {AppPaths.Current.AppDir}）");

        if (_modules.Count == 0)
        {
            AppLog.Error("没有加载到任何模块，导航栏会是空的");
            SetStatus("没有加载到任何模块。请检查 Modules 程序集是否随程序一起部署。");
        }
        else
        {
            AppLog.Info("已加载模块：" + string.Join("、", _modules.Keys));
        }
    }

    private void Settings_Checked(object sender, RoutedEventArgs e)
    {
        _activeModuleId = null;
        ContentHost.Content = new Views.SettingsView(_store, _appSection, this);
        SetStatus("设置改动会立即生效。");
        UpdateStatusBar();
    }

    private void Activate(string moduleId)
    {
        if (!_modules.TryGetValue(moduleId, out var module)) return;

        // 离开上一个模块
        if (_activeModuleId != null && _modules.TryGetValue(_activeModuleId, out var previous)
            && previous is IActivatableModule previousActivatable)
        {
            try { previousActivatable.OnDeactivated(); } catch (Exception ex) { AppLog.Error("模块停用失败", ex); }
        }

        if (!_views.TryGetValue(moduleId, out var view))
        {
            try
            {
                view = module.CreateView();
                _views[moduleId] = view;

                // 渲染异常兜底：模块崩了不要带着整个程序一起崩
                view.DispatcherUnhandledExceptionGuard();
            }
            catch (Exception ex)
            {
                AppLog.Error($"创建模块「{module.Title}」失败：{ex}");
                UiHost.Error($"无法打开「{module.Title}」：\n{ex.Message}");
                return;
            }
        }

        _activeModuleId = moduleId;
        ContentHost.Content = view;

        if (module is IActivatableModule activatable)
        {
            try { activatable.OnActivated(); } catch (Exception ex) { AppLog.Error("模块激活失败", ex); }
        }

        SetStatus(module.Description);
        UpdateStatusBar();
    }

    // ==================== 供模块调用 ====================

    /// <summary>已注册的模块 id，按导航顺序。</summary>
    public IEnumerable<string> ModuleIds => _modules.Keys;

    /// <summary>切到指定模块（模块之间投递完文本后可以顺手切过去）。</summary>
    public void NavigateTo(string moduleId)
    {
        var button = _navButtons.FirstOrDefault(b => string.Equals((string)b.Tag, moduleId, StringComparison.OrdinalIgnoreCase));
        if (button != null) button.IsChecked = true;
    }

    /// <summary>模块点「发送到队列」后请求切到目标页，统一在这里处理线程与存在性。</summary>
    private void OnModuleNavigationRequested(string moduleId)
    {
        if (Dispatcher.CheckAccess()) NavigateTo(moduleId);
        else Dispatcher.BeginInvoke(new Action(() => NavigateTo(moduleId)));
    }

    /// <summary>打开全局设置页。</summary>
    public void NavigateToSettings() => BtnSettings.IsChecked = true;

    /// <summary>把一段文本投递给目标模块，并切到它那里。</summary>
    public bool SendText(string moduleId, string text, string source)
    {
        if (!_modules.TryGetValue(moduleId, out var module)) return false;

        // 目标模块还没创建时先创建，否则投递会落在空处
        if (!_views.ContainsKey(moduleId))
        {
            var button = _navButtons.FirstOrDefault(b => string.Equals((string)b.Tag, moduleId, StringComparison.OrdinalIgnoreCase));
            if (button == null) return false;
            var previous = _activeModuleId;
            button.IsChecked = true;
            if (previous != null) NavigateBackAfterCreate(previous);
        }

        if (!_views.TryGetValue(moduleId, out var view)) return false;
        if (view is not ITextReceiver receiver) return false;

        try
        {
            bool ok = receiver.ReceiveText(text, source);
            if (ok) SetStatus($"{source} → {module.Title}");
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Error($"投递文本到「{module.Title}」失败", ex);
            return false;
        }
    }

    private void NavigateBackAfterCreate(string previousId)
    {
        // 创建过程中会触发模块自身的初始化，这里只在目标模块不接收文本时把用户切回原处
        var target = _activeModuleId;
        if (target == null) return;

        var view = _views.TryGetValue(target, out var v) ? v : null;
        if (view is ITextReceiver) return;

        var button = _navButtons.FirstOrDefault(b => string.Equals((string)b.Tag, previousId, StringComparison.OrdinalIgnoreCase));
        if (button != null) button.IsChecked = true;
    }

    public void SetStatus(string message)
    {
        TxtStatus.Text = message;
    }

    public void UpdateStatusBar()
    {
        // Python 探测结果（抽卡与拼图共用同一个解释器）
        var python = PythonLocator.Resolve(_appSection.PythonPath);
        TxtPython.Text = python.Found ? "Python 就绪" : "Python 未找到";
        TxtPython.ToolTip = python.Describe();

        // ComfyUI 地址只在「批量提交」页里维护，这里从批量模块的配置里读，避免两处不一致
        var batch = _store.Get<DrawBench.Modules.Batch.BatchSettings>(DrawBench.Modules.ModuleIds.Batch);
        string comfy = Core.Comfy.ComfyClient.Normalize(batch?.ComfyBaseUrl);
        TxtComfy.Text = "ComfyUI " + comfy.Replace("http://", "");
        TxtComfy.ToolTip = "在「批量提交」页顶部修改 ComfyUI 地址";
    }

    private void OnLogLine(string line)
    {
        // 错误行顺带显示在状态栏，正常日志只进文件
        if (!line.Contains("[ERROR]", StringComparison.Ordinal)) return;
        Dispatcher.BeginInvoke(new Action(() => SetStatus(line)));
    }

    // ==================== 收尾 ====================

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_activeModuleId != null && _modules.TryGetValue(_activeModuleId, out var module)
            && module is IActivatableModule activatable)
        {
            try { activatable.OnDeactivated(); } catch { }
        }

        AppLog.Line -= OnLogLine;
        SharedContext.Current.ModuleNavigationRequested -= OnModuleNavigationRequested;
    }
}
