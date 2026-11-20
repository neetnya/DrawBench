using System.ComponentModel;

namespace DrawBench.Modules.Batch.Models;

/// <summary>
/// 主界面左侧工作流列表中的一条配置项，带「是否启用」勾选状态。
/// </summary>
public class WorkflowItem : INotifyPropertyChanged
{
    private bool _isEnabled = true;

    public WorkflowItem(WorkflowApiConfig config)
    {
        Config = config;
    }

    public WorkflowApiConfig Config { get; }

    public string Id => Config.Id;

    /// <summary>界面上显示的名称。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Config.Name) ? "(未命名工作流)" : Config.Name;

    /// <summary>鼠标悬停时展示底模、LoRA 与采样参数摘要。</summary>
    public string Summary => Config.BuildSummary();

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
