using System;
using System.Collections.Generic;
using System.Windows;
using DrawBench.Core;
using DrawBench.Modules.Batch.Views;

namespace DrawBench.Modules.Batch;

/// <summary>
/// 批量提交模块：把提示词队列依次写进 api\ 下的 API 格式工作流并送进 ComfyUI 队列。
/// 同时接收其他模块投递过来的提示词（抽卡结果、读到的提示词、反推标签都能直接填进来）。
/// </summary>
public sealed class BatchModule : IToolModule, ITextReceiver
{
    private readonly SettingsStore _store = SettingsStore.Shared;
    private BatchView? _view;

    /// <summary>外壳创建模块时可能还没有视图，先暂存的文本。</summary>
    private string? _pendingPositive;
    private string? _pendingNegative;

    public string Id => ModuleIds.Batch;
    public string Title => "批量提交";
    public string Description => "把提示词队列写进 ComfyUI 工作流并批量排队";
    public string Glyph => "\uE768";   // Play

    public System.Windows.FrameworkElement CreateView()
    {
        if (_view != null) return _view;

        _view = new BatchView(_store);
        _view.Loaded += (_, _) => FlushPending();
        SharedContext.Current.RegisterSink(ModuleIds.Batch, ReceiveTextInternal);

        return _view;
    }

    /// <summary>
    /// 接收投递：以 "negative:" 开头的是负向提示词，其余按正向处理。
    /// 视图还没建好时先存起来，建好后自动补上。
    /// </summary>
    public bool ReceiveText(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool negative = text.StartsWith("negative:", StringComparison.Ordinal);
        string payload = negative ? text["negative:".Length..] : text;

        if (_view == null)
        {
            if (negative) _pendingNegative = payload;
            else _pendingPositive = payload;
            return true;
        }

        if (negative) _view.SetNegative(payload);
        else _view.AppendPositive(payload);

        return true;
    }

    private bool ReceiveTextInternal(string text, string source) => ReceiveText(text, source);

    private void FlushPending()
    {
        if (_view == null) return;

        if (_pendingPositive != null)
        {
            _view.AppendPositive(_pendingPositive);
            _pendingPositive = null;
        }

        if (_pendingNegative != null)
        {
            _view.SetNegative(_pendingNegative);
            _pendingNegative = null;
        }
    }
}
