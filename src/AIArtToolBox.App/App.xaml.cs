using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AIArtToolBox.Core;
using AIArtToolBox.Core.Cli;
using AIArtToolBox.Core.Logging;
using AIArtToolBox.Core.Ui;

namespace AIArtToolBox.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var paths = AppPaths.Current;
        paths.EnsureCreated();
        StartupTasks.Run(paths);

        // 开发期自检：--selftest <图片目录> 直接跑解析逻辑后退出
        string? selfTestDir = SelfTest.TargetDir(e.Args);
        if (SelfTest.WantsSelfTest(e.Args) && selfTestDir != null)
        {
            CliDispatcher.AttachConsole();
            Shutdown(SelfTest.Run(selfTestDir));
            return;
        }

        // 无界面模式：AIArtToolBox.exe tagger --cli <目录> …（兼容旧的 WD14Tagger.exe --cli 写法）
        if (CliDispatcher.WantsCli(e.Args))
        {
            CliDispatcher.AttachConsole();
            int code = CliDispatcher.Run(e.Args, paths);
            Shutdown(code);
            return;
        }

        base.OnStartup(e);

        var store = SettingsStore.Shared;
        var appSection = store.Get<AppSection>("app") ?? new AppSection();

        UiHost.OwnerProvider = () => Current.MainWindow;

        // 统一日志：把异常留下痕迹，别让用户只看到窗口闪一下
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("未处理异常", args.Exception);
            UiHost.Error("出了点问题：" + args.Exception.Message +
                         "\n\n详情已写入日志：\n" + AppLog.FilePath);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) AppLog.Error("后台线程异常", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("任务异常", args.Exception);
            args.SetObserved();
        };

        var window = new MainWindow(store, appSection);
        MainWindow = window;

        // 开发期自检：--render <目录> 逐个模块截图后退出
        string? renderDir = SelfCheck.OutputDir(e.Args);
        if (SelfCheck.WantsRender(e.Args) && renderDir != null)
        {
            SelfCheck.Run(window, renderDir, window.ModuleIds);
            return;
        }

        window.Show();
    }
}
