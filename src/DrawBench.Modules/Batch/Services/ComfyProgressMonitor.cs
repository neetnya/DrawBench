using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DrawBench.Core.Comfy;

namespace DrawBench.Modules.Batch.Services;

/// <summary>单次批量生成的实时进度快照。</summary>
public sealed class BatchProgressSnapshot
{
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Total { get; init; }
    public string CurrentWorkflow { get; init; } = string.Empty;
    public int CurrentStep { get; init; }
    public int MaxStep { get; init; }
    public bool WebSocketConnected { get; init; }

    public int Finished => Completed + Failed;

    public double OverallPercent
    {
        get
        {
            if (Total <= 0) return 0;
            double stepFrac = MaxStep > 0 ? Math.Clamp((double)CurrentStep / MaxStep, 0.0, 1.0) : 0.0;
            return Math.Clamp(((Finished + stepFrac) / Total) * 100.0, 0.0, 100.0);
        }
    }
}

/// <summary>
/// 通过 ComfyUI WebSocket（ws://.../ws?clientId=...）实时监听单张采样步数与任务完成事件，
/// 同时辅以轻量 /history 兜底核对，确保断线或缓存命中时进度条也能准确走到 100%。
/// </summary>
public sealed class ComfyProgressMonitor : IDisposable
{
    private readonly ComfyClient _client;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _tracked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preFinished = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _allDoneTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _expectedTotal;
    private bool _submissionClosed;
    private bool _wsConnected;
    private string? _activePromptId;
    private int _currentStep;
    private int _maxStep;
    private bool _disposed;

    public ComfyProgressMonitor(ComfyClient client)
    {
        _client = client;
    }

    public event Action<BatchProgressSnapshot>? ProgressChanged;
    public event Action<string, string>? ExecutionError;

    public bool IsWebSocketConnected => _wsConnected;

    /// <summary>连接 WebSocket 并启动后台监听（连不上也不会抛异常，自动降级为 /history 轮询）。</summary>
    public async Task StartAsync(int expectedTotal, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _expectedTotal = Math.Max(0, expectedTotal);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        try
        {
            _ws = new ClientWebSocket();
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _ws.ConnectAsync(_client.BuildWebSocketUri(), connectTimeout.Token).ConfigureAwait(false);
            _wsConnected = true;
            _ = Task.Run(() => ReceiveLoopAsync(_ws, token), token);
        }
        catch
        {
            _wsConnected = false;
            _ws?.Dispose();
            _ws = null;
        }

        _ = Task.Run(() => ReconcileLoopAsync(token), token);
        EmitSnapshot();
    }

    /// <summary>登记一个已提交成功的 prompt_id 及其所属工作流名。</summary>
    public void TrackPrompt(string promptId, string workflowName)
    {
        if (string.IsNullOrWhiteSpace(promptId)) return;

        bool completedEarly = false;
        lock (_gate)
        {
            _tracked[promptId] = workflowName;
            if (_preFinished.Remove(promptId))
            {
                _completed.Add(promptId);
                completedEarly = true;
            }
        }

        if (completedEarly)
            CheckAllDone();
        EmitSnapshot();
    }

    /// <summary>标记本批全部提交循环已结束，此时以实际成功入队的数量作为监控总数。</summary>
    public void CompleteSubmission()
    {
        lock (_gate)
        {
            _submissionClosed = true;
            _expectedTotal = _tracked.Count;
        }

        CheckAllDone();
        EmitSnapshot();
    }

    /// <summary>返回尚未完成的 prompt_id 列表（用于中止时从 ComfyUI 队列删除未开始任务）。</summary>
    public List<string> GetPendingPromptIds()
    {
        lock (_gate)
        {
            return _tracked.Keys
                .Where(id => !_completed.Contains(id) && !_failed.Contains(id))
                .ToList();
        }
    }

    /// <summary>等待本批所有已入队任务全部完成（或被取消）。</summary>
    public async Task<BatchProgressSnapshot> WaitForCompletionAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _allDoneTcs.TrySetCanceled(ct));
        await _allDoneTcs.Task.ConfigureAwait(false);
        return BuildSnapshot();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _wsConnected = false;
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || ms.Length == 0)
                    continue;

                string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                HandleWsMessage(text);
            }
        }
        catch
        {
            _wsConnected = false;
        }
    }

    private void HandleWsMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                return;

            string type = typeProp.GetString() ?? string.Empty;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return;

            string? promptId = TryGetString(data, "prompt_id");

            switch (type)
            {
                case "execution_start":
                    if (!string.IsNullOrEmpty(promptId))
                    {
                        lock (_gate)
                        {
                            _activePromptId = promptId;
                            _currentStep = 0;
                            _maxStep = 0;
                        }
                        EmitSnapshot();
                    }
                    break;

                case "executing":
                    string? node = TryGetString(data, "node");
                    if (string.IsNullOrEmpty(node))
                    {
                        // node == null 表示该 prompt 执行完成
                        if (!string.IsNullOrEmpty(promptId))
                            MarkCompleted(promptId);
                    }
                    else if (!string.IsNullOrEmpty(promptId))
                    {
                        lock (_gate)
                        {
                            if (!string.Equals(_activePromptId, promptId, StringComparison.Ordinal))
                            {
                                _activePromptId = promptId;
                                _currentStep = 0;
                                _maxStep = 0;
                            }
                        }
                        EmitSnapshot();
                    }
                    break;

                case "progress":
                    int val = TryGetInt(data, "value");
                    int max = TryGetInt(data, "max");
                    lock (_gate)
                    {
                        if (!string.IsNullOrEmpty(promptId))
                            _activePromptId = promptId;
                        _currentStep = val;
                        _maxStep = max;
                    }
                    EmitSnapshot();
                    break;

                case "execution_error":
                    if (!string.IsNullOrEmpty(promptId))
                    {
                        string nodeType = TryGetString(data, "node_type") ?? "节点";
                        string errMsg = TryGetString(data, "exception_message") ?? "执行出错";
                        MarkFailed(promptId, $"{nodeType}: {errMsg.Trim()}");
                    }
                    break;

                case "execution_interrupted":
                    if (!string.IsNullOrEmpty(promptId))
                        MarkFailed(promptId, "已被中断");
                    break;
            }
        }
        catch
        {
            // 忽略无法解析的扩展事件
        }
    }

    private void MarkCompleted(string promptId)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_tracked.ContainsKey(promptId))
            {
                if (!_failed.Contains(promptId))
                    changed = _completed.Add(promptId);
            }
            else
            {
                _preFinished.Add(promptId);
            }

            if (string.Equals(_activePromptId, promptId, StringComparison.Ordinal))
            {
                _currentStep = 0;
                _maxStep = 0;
            }
        }

        if (changed)
        {
            CheckAllDone();
            EmitSnapshot();
        }
    }

    private void MarkFailed(string promptId, string reason)
    {
        string workflowName = string.Empty;
        bool changed = false;

        lock (_gate)
        {
            if (_tracked.TryGetValue(promptId, out var wf))
            {
                workflowName = wf;
                _completed.Remove(promptId);
                changed = _failed.Add(promptId);
            }

            if (string.Equals(_activePromptId, promptId, StringComparison.Ordinal))
            {
                _currentStep = 0;
                _maxStep = 0;
            }
        }

        if (changed)
        {
            ExecutionError?.Invoke(workflowName, reason);
            CheckAllDone();
            EmitSnapshot();
        }
    }

    /// <summary>每 2.5 秒用 /history 对未完成的任务做一次轻量兜底核对，防止 WS 断线或丢包卡住。</summary>
    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_allDoneTcs.Task.IsCompleted)
            {
                await Task.Delay(2500, ct).ConfigureAwait(false);
                var pending = GetPendingPromptIds();
                if (pending.Count == 0)
                {
                    CheckAllDone();
                    continue;
                }

                // 优先检查最早提交的几个未完成任务，避免一次性发太多请求
                foreach (var id in pending.Take(4))
                {
                    if (ct.IsCancellationRequested) return;
                    if (await _client.IsDoneAsync(id, ct).ConfigureAwait(false))
                        MarkCompleted(id);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CheckAllDone()
    {
        bool done;
        lock (_gate)
        {
            done = _submissionClosed && (_completed.Count + _failed.Count >= _tracked.Count);
        }

        if (done)
            _allDoneTcs.TrySetResult(true);
    }

    private BatchProgressSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            string wfName = string.Empty;
            if (!string.IsNullOrEmpty(_activePromptId) && _tracked.TryGetValue(_activePromptId, out var name))
                wfName = name;

            int total = _submissionClosed ? _tracked.Count : Math.Max(_expectedTotal, _tracked.Count);
            return new BatchProgressSnapshot
            {
                Completed = _completed.Count,
                Failed = _failed.Count,
                Total = total,
                CurrentWorkflow = wfName,
                CurrentStep = _currentStep,
                MaxStep = _maxStep,
                WebSocketConnected = _wsConnected
            };
        }
    }

    private void EmitSnapshot() => ProgressChanged?.Invoke(BuildSnapshot());

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var val)) return null;
        return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private static int TryGetInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var val)) return 0;
        return val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int n) ? n : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}
