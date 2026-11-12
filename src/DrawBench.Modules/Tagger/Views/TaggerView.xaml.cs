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

    private async void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnViewLoaded;

        // 首次进入时如果别的模块已经选过图片目录，直接把那里的图加进队列
        string folder = SharedContext.Current.CurrentImageFolder;
        if (folder.Length > 0 && Directory.Exists(folder) && Vm.Items.Count == 0)
        {
            try { await Vm.AddPathAsync(folder); } catch (Exception ex) { AppLog.Error("添加目录失败", ex); }
        }

        if (!_activationRescan)
        {
            _activationRescan = true;
            if (!Vm.HasModels) Vm.ReloadModels();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
                await Vm.AddPathAsync(path);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            Vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Vm.StartCommand.Execute(null);
            e.Handled = true;
        }
    }
}
