using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIArtToolBox.Modules.Tagger.Core;

public sealed class AppConfig
{
    public string? ModelName { get; set; }
    public string? LastSourceDir { get; set; }

    public double Threshold { get; set; } = 0.35;
    public double CharacterThreshold { get; set; } = 0.85;
    public bool OutputRating { get; set; } = true;

    /// <summary>复制时生效，首次运行默认勾选。</summary>
    public bool ReplaceUnderscore { get; set; } = true;

    /// <summary>复制时生效，首次运行默认不勾选。</summary>
    public bool TrailingComma { get; set; }

    /// <summary>复制时生效，把括号转义成 \( \)；默认勾选。</summary>
    public bool EscapeParens { get; set; } = true;

    /// <summary>配置版本号，用于旧配置升级（v2 起括号转义默认勾选）。</summary>
    public int ConfigVersion { get; set; } = CurrentConfigVersion;

    public const int CurrentConfigVersion = 2;

    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public bool Recursive { get; set; } = true;
    public string[] Extensions { get; set; } = { "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "tif" };

    public TaggerOptions ToTaggerOptions() => new()
    {
        Threshold = Threshold,
        CharacterThreshold = CharacterThreshold,
        OutputRating = OutputRating,
        ReplaceUnderscore = ReplaceUnderscore,
        TrailingComma = TrailingComma,
        EscapeParens = EscapeParens
    };
}

/// <summary>暂停/继续控制器。</summary>
public sealed class PauseController
{
    private readonly object _gate = new();
    private volatile bool _paused;
    private TaskCompletionSource<bool>? _resume;

    public bool IsPaused => _paused;

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            tcs = _resume;
            _resume = null;
        }
        tcs?.TrySetResult(true);
    }

    public void Reset() => Resume();

    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task waitTask;
        lock (_gate)
        {
            if (!_paused || _resume == null) return;
            waitTask = _resume.Task;
        }
        await waitTask.WaitAsync(ct).ConfigureAwait(false);
    }
}
