using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DrawBench.Core;
using DrawBench.Core.Logging;
using DrawBench.Core.Ui;
using DrawBench.Modules.Tagger.ViewModels;

namespace DrawBench.Modules.Tagger.Views;

/// <summary>
/// 反推模块界面。原来是最大化窗口，这里作为 UserControl 住进外壳；
/// 快捷键、拖放、首次激活补扫模型的行为都保留。
/// </summary>
public partial class TaggerView : UserControl
{
    private bool _activationRescan;

    public TaggerView()
    {
        InitializeComponent();

        var vm = new MainViewModel();
        DataContext = vm;

        PreviewKeyDown += OnPreviewKeyDown;

        // 启动就把模型目录读出来（纯同步操作，很快）
        try
        {
            vm.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Error("反推模块初始化失败", ex);
            UiHost.Warn("初始化失败：" + ex.Message, "反推");
        }

        Loaded += OnViewLoaded;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnViewLoaded;

        if (!_activationRescan)
        {
            _activationRescan = true;
            if (!Vm.HasModels) Vm.ReloadModels();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool allow = Vm.IsIdle && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = allow ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!Vm.IsIdle) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        try
        {
            foreach (var path in paths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                    await Vm.AddPathAsync(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("拖入文件失败", ex);
            UiHost.Warn("添加拖入文件失败：" + ex.Message, "反推");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox)
        {
            if (Vm.RemoveSelectedCommand.CanExecute(null))
                Vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            if (Vm.StartCommand.CanExecute(null))
                Vm.StartCommand.Execute(null);
            e.Handled = true;
        }
    }
}
