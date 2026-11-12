using System;
using System.Collections.Generic;

namespace DrawBench.Modules.Batch;

/// <summary>
/// 批量提交模块自己的设置，存在统一 settings.json 的 "batch" 段。
/// 只记录「下次打开还有意义」的东西：工作流勾选与 ComfyUI 地址；
/// 数量/日期/种子这类一次性的输入不落盘。
/// </summary>
public sealed class BatchSettings
{
    /// <summary>上次处于勾选状态的工作流文件名。</summary>
    public List<string> EnabledWorkflows { get; set; } = new();

    /// <summary>是否已经保存过勾选状态；没保存过时全部默认勾选。</summary>
    public bool HasSavedSelection { get; set; }

    public string ComfyBaseUrl { get; set; } = "http://127.0.0.1:8188";

    public bool IsEnabled(string fileName) => EnabledWorkflows.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}
