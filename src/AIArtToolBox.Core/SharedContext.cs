using System;
using System.Collections.Generic;

namespace AIArtToolBox.Core;

/// <summary>
/// 模块之间共享的一点状态。目前只有三样：
/// 「当前图片目录」、「键值交换区」以及「把文本发给某个模块」。
/// 有意做得极薄——模块之间不该互相持有引用。
/// </summary>
public sealed class SharedContext
{
    private static SharedContext? _current;

    public static SharedContext Current => _current ??= new SharedContext();

    private readonly Dictionary<string, object?> _bag = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前图片目录变化（任选一处目录，其他模块可以跟着用）。</summary>
    public event Action<string>? ImageFolderChanged;

    /// <summary>当前图片目录，空字符串表示还没选。</summary>
    public string CurrentImageFolder
    {
        get => Get<string>("imageFolder") ?? string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (string.Equals(CurrentImageFolder, value, StringComparison.OrdinalIgnoreCase)) return;

            Set("imageFolder", value);
            ImageFolderChanged?.Invoke(value);
        }
    }

    /// <summary>发文本给某个模块：由外壳在模块创建时注册投递函数。</summary>
    private readonly Dictionary<string, Func<string, string, bool>> _sinks = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterSink(string moduleId, Func<string, string, bool> sink)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || sink == null) return;
        _sinks[moduleId] = sink;
    }

    /// <summary>把文本投递给目标模块；返回 false 表示该模块还没创建或不接收文本。</summary>
    public bool Deliver(string moduleId, string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return _sinks.TryGetValue(moduleId, out var sink) && sink(text, source);
    }

    public bool HasSink(string moduleId) => _sinks.ContainsKey(moduleId);

    public T? Get<T>(string key)
    {
        lock (_bag)
        {
            return _bag.TryGetValue(key, out object? value) && value is T typed ? typed : default;
        }
    }

    public void Set(string key, object? value)
    {
        lock (_bag) _bag[key] = value;
    }
}
