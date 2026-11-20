using System;
using System.Collections.Generic;
using System.Windows;
using DrawBench.Core;
using DrawBench.Modules.Batch.Views;

namespace DrawBench.Modules.Batch;

/// <summary>
/// 批量提交模块：按配置的工作流 API 参数动态生成 ComfyUI 请求并批量排队。
/// 同时接收其他模块投递过来的提示词（支持覆盖发送与末尾追加）。
/// </summary>
public sealed class BatchModule : IToolModule, IActivatableModule, ITextReceiver
{
    private readonly SettingsStore _store = SettingsStore.Shared;
    private readonly object _gate = new();
    private readonly List<(string Text, string Source)> _pending = new();
    private BatchView? _view;

    public BatchModule()
    {
        SharedContext.Current.RegisterSink(ModuleIds.Batch, ReceiveTextInternal);
    }

    public string Id => ModuleIds.Batch;
    public string Title => "批量提交";
    public string Description => "按配置的工作流 API 参数批量排队提交给 ComfyUI";
    public string Glyph => "\uE768";   // Play

    public FrameworkElement CreateView()
    {
        BatchView view;
        List<(string Text, string Source)>? toFlush = null;

        lock (_gate)
        {
            if (_view != null) return _view;

            view = new BatchView(_store);
            _view = view;

            if (_pending.Count > 0)
            {
                toFlush = new List<(string Text, string Source)>(_pending);
                _pending.Clear();
            }
        }

        if (toFlush != null)
        {
            foreach (var item in toFlush)
                ApplyToView(view, item.Text);
        }

        return view;
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

        BatchView? view;
        lock (_gate)
        {
            view = _view;
            if (view == null)
            {
                _pending.Add((text, source));
                return true;
            }
        }

        ApplyToView(view, text);
        return true;
    }

    private bool ReceiveTextInternal(string text, string source) => ReceiveText(text, source);

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
