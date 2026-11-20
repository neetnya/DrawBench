using System;
using System.Collections.Concurrent;

namespace DrawBench.Core;

/// <summary>
/// 模块之间的轻量通信中枢：负责请求外壳切换页面，以及把文本投递给目标模块。
/// 有意做得极薄——模块之间不互相持有引用。
/// </summary>
public sealed class SharedContext
{
    private static readonly Lazy<SharedContext> CurrentLazy = new(() => new SharedContext());

    public static SharedContext Current => CurrentLazy.Value;

    /// <summary>发文本给某个模块：由模块注册投递函数（线程安全）。</summary>
    private readonly ConcurrentDictionary<string, Func<string, string, bool>> _sinks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>模块请求外壳切换页面；外壳订阅后调用自己的导航。</summary>
    public event Action<string>? ModuleNavigationRequested;

    /// <summary>请求切到某个模块。没有外壳订阅时（命令行/自检）什么也不做。</summary>
    public void RequestModule(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId)) return;
        ModuleNavigationRequested?.Invoke(moduleId);
    }

    public void RegisterSink(string moduleId, Func<string, string, bool> sink)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || sink == null) return;
        _sinks[moduleId] = sink;
    }

    public void UnregisterSink(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId)) return;
        _sinks.TryRemove(moduleId, out _);
    }

    /// <summary>把文本投递给目标模块；返回 false 表示该模块还没注册或不接收文本。</summary>
    public bool Deliver(string moduleId, string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return _sinks.TryGetValue(moduleId, out var sink) && sink(text, source);
    }

    public bool HasSink(string moduleId) => _sinks.ContainsKey(moduleId);
}
