using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawBench.Core;
using DrawBench.Core.Logging;
using DrawBench.Core.Ui;

namespace DrawBench.App;

/// <summary>
/// 外壳：左侧竖排导航 + 右侧内容区。
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
                    FontFamily = (FontFamily)FindResource("IconFont"),
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
        }
        else
        {
            AppLog.Info("已加载模块：" + string.Join("、", _modules.Keys));
        }
    }

    private void Settings_Checked(object sender, RoutedEventArgs e)
    {
        if (_activeModuleId != null && _modules.TryGetValue(_activeModuleId, out var previous)
            && previous is IActivatableModule previousActivatable)
        {
            try { previousActivatable.OnDeactivated(); } catch (Exception ex) { AppLog.Error("模块停用失败", ex); }
        }

        _activeModuleId = null;
        ContentHost.Content = new Views.SettingsView(_store, _appSection);
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

    /// <summary>模块点「发送到」后请求切到目标页，统一在这里处理线程与存在性。</summary>
    private void OnModuleNavigationRequested(string moduleId)
    {
        if (Dispatcher.CheckAccess()) NavigateTo(moduleId);
        else Dispatcher.BeginInvoke(new Action(() => NavigateTo(moduleId)));
    }

    /// <summary>打开全局设置页。</summary>
    public void NavigateToSettings() => BtnSettings.IsChecked = true;

    // ==================== 收尾 ====================

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_activeModuleId != null && _modules.TryGetValue(_activeModuleId, out var module)
            && module is IActivatableModule activatable)
        {
            try { activatable.OnDeactivated(); } catch { }
        }

        SharedContext.Current.ModuleNavigationRequested -= OnModuleNavigationRequested;
    }
}
