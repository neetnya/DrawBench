using System;

namespace AIArtToolBox.Core;

/// <summary>
/// 工具模块契约。每个工具做成一个 UserControl（视图），由外壳按需创建、常驻不销毁。
/// </summary>
public interface IToolModule
{
    /// <summary>模块标识，同时是 settings.json 里的段名与命令行子命令名。</summary>
    string Id { get; }

    /// <summary>左侧导航上显示的名字。</summary>
    string Title { get; }

    /// <summary>导航上的一行说明，鼠标悬停提示用。</summary>
    string Description { get; }

    /// <summary>导航图标字符（Segoe Fluent Icons / Segoe MDL2 Assets）。</summary>
    string Glyph { get; }

    /// <summary>
    /// 创建视图。首次进入时调用一次，之后外壳保留实例。
    /// </summary>
    System.Windows.FrameworkElement CreateView();
}

/// <summary>
/// 模块可选的第二个接口：需要知道「现在被切到前台 / 被切走」时实现它。
/// </summary>
public interface IActivatableModule
{
    void OnActivated();
    void OnDeactivated();
}

/// <summary>
/// 模块可选的第三个接口：接收别的模块投递过来的文本（比如抽卡结果直接进队列）。
/// </summary>
public interface ITextReceiver
{
    /// <summary>返回 true 表示接收成功，外壳会给一句状态提示。</summary>
    bool ReceiveText(string text, string source);
}
