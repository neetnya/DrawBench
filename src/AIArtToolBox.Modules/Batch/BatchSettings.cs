using System;
using System.Collections.Generic;
using System.Linq;

namespace AIArtToolBox.Modules.Batch;

/// <summary>
/// 批量提交模块自己的设置，存在统一 settings.json 的 "batch" 段。
/// 原来是 exe 同级的 settings.json（只存工作流勾选），这里加了完整选项并兼容旧文件。
/// </summary>
public sealed class BatchSettings
{
    /// <summary>上次处于勾选状态的工作流文件名。</summary>
    public List<string> EnabledWorkflows { get; set; } = new();

    /// <summary>上次保存时 api 目录里出现过的文件名（新文件默认勾选）。</summary>
    public List<string> KnownWorkflows { get; set; } = new();

    /// <summary>是否已经保存过勾选状态（决定新文件是否默认勾选）。</summary>
    public bool HasSavedSelection { get; set; }

    public string ComfyBaseUrl { get; set; } = "http://127.0.0.1:8188";
    public int PerPromptCount { get; set; } = 1;
    public string DateSuffix { get; set; } = string.Empty;
    public long Seed { get; set; } = -1;
    public bool LockSeed { get; set; }
    public bool GlobalLockSeed { get; set; }

    public BatchSettings Clone() => (BatchSettings)MemberwiseClone();

    public bool IsEnabled(string fileName) => EnabledWorkflows.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string fileName, bool enabled)
    {
        EnabledWorkflows.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
        if (enabled) EnabledWorkflows.Add(fileName);
    }

    public void MarkKnown(IEnumerable<string> fileNames)
    {
        KnownWorkflows = fileNames.ToList();
        HasSavedSelection = true;
    }
}
