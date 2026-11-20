using System;
using System.Collections.Generic;
using DrawBench.Modules.Batch.Models;

namespace DrawBench.Modules.Batch;

/// <summary>
/// 批量提交模块自己的设置，存在统一 settings.json 的 "batch" 段。
/// 记录 ComfyUI 地址、用户配置的工作流 API 列表、全局 LoRA 触发词库以及上次勾选状态；
/// 数量/日期/种子这类一次性的输入不落盘。
/// </summary>
public sealed class BatchSettings
{
    /// <summary>用户通过配置菜单维护的工作流 API 列表（不再依赖外部 .json 文件）。</summary>
    public List<WorkflowApiConfig> Workflows { get; set; } = new();

    /// <summary>全局 LoRA 触发词库（Key = LoRA 相对路径，Value = 触发词），跟着 LoRA 走，所有工作流共享。</summary>
    public Dictionary<string, string> LoraTriggerWords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次处于勾选状态的工作流 Id。</summary>
    public List<string> EnabledWorkflows { get; set; } = new();

    /// <summary>是否已经保存过勾选状态；没保存过时全部默认勾选。</summary>
    public bool HasSavedSelection { get; set; }

    public string ComfyBaseUrl { get; set; } = "http://127.0.0.1:8188";

    public bool IsEnabled(string idOrName) =>
        EnabledWorkflows.Contains(idOrName, StringComparer.OrdinalIgnoreCase);
}
