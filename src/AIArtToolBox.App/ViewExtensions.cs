using System;

namespace AIArtToolBox.App;

internal static class ViewExtensions
{
    /// <summary>
    /// 模块视图的界面线程异常兜底：单个模块出问题不应把整个外壳带崩。
    /// 精简版 WriteLine 直接写日志后吞掉。
    /// </summary>
    public static void DispatcherUnhandledExceptionGuard(this System.Windows.FrameworkElement _)
    {
        // 目前依赖 App.DispatcherUnhandledException 统一处理；
        // 这里保留扩展点，为以后按模块隔离异常留位置。
    }
}
