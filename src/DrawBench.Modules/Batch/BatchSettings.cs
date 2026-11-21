using System;
using System.Collections.Generic;
using System.Linq;
using DrawBench.Modules.Batch.Models;

namespace DrawBench.Modules.Batch;

/// <summary>
/// 批量提交模块自己的设置，存在统一 settings.json 的 "batch" 段。
/// 记录 ComfyUI 地址、用户配置的工作流 API 列表、全局 LoRA 库以及上次勾选状态；
/// 数量/日期/种子这类一次性的输入不落盘。
/// </summary>
public sealed class BatchSettings
{
    /// <summary>用户通过配置菜单维护的工作流 API 列表。</summary>
    public List<WorkflowApiConfig> Workflows { get; set; } = new();

    /// <summary>全局 LoRA 库（包含路径、别名、默认权重、全局触发词），所有工作流共享。</summary>
    public List<LoraProfile> LoraProfiles { get; set; } = new();

    /// <summary>兼容旧版字典格式的触发词（启动时自动迁移合并进 LoraProfiles）。</summary>
    public Dictionary<string, string> LoraTriggerWords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次处于勾选状态的工作流 Id。</summary>
    public List<string> EnabledWorkflows { get; set; } = new();

    /// <summary>是否已经保存过勾选状态；没保存过时全部默认勾选。</summary>
    public bool HasSavedSelection { get; set; }

    public string ComfyBaseUrl { get; set; } = "http://127.0.0.1:8188";

    public bool IsEnabled(string idOrName) =>
        EnabledWorkflows.Contains(idOrName, StringComparer.OrdinalIgnoreCase);

    /// <summary>确保集合非空，并将旧版 LoraTriggerWords 自动迁移到 LoraProfiles。</summary>
    public void Normalize()
    {
        Workflows ??= new List<WorkflowApiConfig>();
        LoraProfiles ??= new List<LoraProfile>();
        LoraTriggerWords = LoraTriggerWords is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(LoraTriggerWords, StringComparer.OrdinalIgnoreCase);

        // 将旧版字典迁移进 LoraProfiles
        foreach (var kv in LoraTriggerWords)
        {
            string key = (kv.Key ?? string.Empty).Trim();
            if (key.Length == 0) continue;

            var existing = LoraProfiles.FirstOrDefault(p =>
                string.Equals(p.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                LoraProfiles.Add(new LoraProfile
                {
                    LoraName = key,
                    TriggerWords = (kv.Value ?? string.Empty).Trim()
                });
            }
            else if (string.IsNullOrWhiteSpace(existing.TriggerWords) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                existing.TriggerWords = kv.Value.Trim();
            }
        }

        // 确保工作流里已经引用的 LoRA 也在全局库里可见
        foreach (var lora in Workflows.SelectMany(w => w.Loras))
        {
            string key = (lora.LoraName ?? string.Empty).Trim();
            if (key.Length == 0) continue;

            bool exists = LoraProfiles.Any(p =>
                string.Equals(p.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                LoraProfiles.Add(new LoraProfile
                {
                    LoraName = key,
                    DefaultStrengthModel = lora.StrengthModel,
                    DefaultStrengthClip = lora.StrengthClip
                });
            }
        }
    }

    /// <summary>构建供 WorkflowApiBuilder 使用的 LoRA -> 触发词映射表。</summary>
    public Dictionary<string, string> BuildTriggerLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in LoraProfiles)
        {
            string key = (p.LoraName ?? string.Empty).Trim();
            string trigger = (p.TriggerWords ?? string.Empty).Trim();
            if (key.Length > 0 && trigger.Length > 0)
                map[key] = trigger;
        }
        return map;
    }
}
