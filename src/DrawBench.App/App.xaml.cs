using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DrawBench.Core;
using DrawBench.Core.Cli;
using DrawBench.Core.Logging;
using DrawBench.Core.Ui;

namespace DrawBench.App;

public partial class App : Application
{
    private int _handlingUnhandledException;

    protected override void OnStartup(StartupEventArgs e)
    {
        var paths = AppPaths.Current;
        paths.EnsureCreated();
        StartupTasks.Run();

        // 尽早注册后台线程与未观察任务异常钩子，确保 CLI / SelfTest 模式崩溃也有日志
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) AppLog.Error("后台线程异常", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("任务异常", args.Exception);
            args.SetObserved();
        };

        // 开发期自检：--selftest <图片目录> 直接跑解析逻辑后退出
        string? selfTestDir = SelfTest.TargetDir(e.Args);
        if (SelfTest.WantsSelfTest(e.Args) && selfTestDir != null)
        {
            CliDispatcher.AttachConsole();
            Shutdown(SelfTest.Run(selfTestDir));
            return;
        }

        // 无界面模式：DrawBench.exe tagger --cli <目录> …
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

        UiHost.OwnerProvider = () => Current?.MainWindow;

        // UI 线程未处理异常：防重入，避免渲染循环异常造成无限弹窗死循环
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("未处理异常", args.Exception);
            if (Interlocked.Exchange(ref _handlingUnhandledException, 1) == 0)
            {
                try
                {
                    UiHost.Error("出了点问题：" + args.Exception.Message +
                                 "\n\n详情已写入日志：\n" + AppLog.FilePath);
                }
                finally
                {
                    Interlocked.Exchange(ref _handlingUnhandledException, 0);
                }
            }
            args.Handled = true;
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
