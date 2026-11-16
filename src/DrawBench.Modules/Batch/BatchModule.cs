using System;
using System.Collections.Generic;
using System.Windows;
using DrawBench.Core;
using DrawBench.Modules.Batch.Views;

namespace DrawBench.Modules.Batch;

/// <summary>
/// 批量提交模块：把提示词队列依次写进 api\ 下的 API 格式工作流并送进 ComfyUI 队列。
/// 同时接收其他模块投递过来的提示词（支持覆盖发送与末尾追加）。
/// </summary>
public sealed class BatchModule : IToolModule, IActivatableModule, ITextReceiver
{
    private readonly SettingsStore _store = SettingsStore.Shared;
    private readonly List<(string Text, string Source)> _pending = new();
    private BatchView? _view;

    public BatchModule()
    {
        SharedContext.Current.RegisterSink(ModuleIds.Batch, ReceiveTextInternal);
    }

    public string Id => ModuleIds.Batch;
    public string Title => "批量提交";
    public string Description => "把提示词队列写进 ComfyUI 工作流并批量排队";
    public string Glyph => "\uE768";   // Play

    public System.Windows.FrameworkElement CreateView()
    {
        if (_view != null) return _view;

        _view = new BatchView(_store);
        SharedContext.Current.RegisterSink(ModuleIds.Batch, ReceiveTextInternal);
        FlushPending();

        return _view;
    }

    public void OnActivated() { }

    public void OnDeactivated() => _view?.SaveSettings();

    /// <summary>
    /// 接收投递：
    /// - "replace:" 开头：清空正向队列并写入
    /// - "append:" 开头：追加到正向队列末尾
    /// - "negative:" 开头：替换负向提示词
    /// - "negative-append:" 开头：追加到负向提示词末尾
    /// </summary>
    public bool ReceiveText(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (_view == null)
        {
            _pending.Add((text, source));
            return true;
        }

        ApplyToView(_view, text);
        return true;
    }

    private bool ReceiveTextInternal(string text, string source) => ReceiveText(text, source);

    private void FlushPending()
    {
        if (_view == null || _pending.Count == 0) return;

        foreach (var item in _pending)
            ApplyToView(_view, item.Text);

        _pending.Clear();
    }

    private static void ApplyToView(BatchView view, string text)
    {
        if (text.StartsWith("negative-append:", StringComparison.Ordinal))
        {
            view.AppendNegative(text["negative-append:".Length..]);
        }
        else if (text.StartsWith("negative:", StringComparison.Ordinal))
        {
            view.SetNegative(text["negative:".Length..]);
        }
        else if (text.StartsWith("append:", StringComparison.Ordinal))
        {
            view.AppendPositive(text["append:".Length..]);
        }
        else if (text.StartsWith("replace:", StringComparison.Ordinal))
        {
            view.SetPositive(text["replace:".Length..]);
        }
        else
        {
            view.SetPositive(text);
        }
    }
}
