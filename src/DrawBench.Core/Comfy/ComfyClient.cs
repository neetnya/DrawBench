using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DrawBench.Core.Comfy;

/// <summary>提交失败的原因分类，界面据此给不同提示。</summary>
public enum ComfySubmitStatus
{
    Ok,
    Unreachable,
    Rejected,
    HttpError,
    Cancelled
}

public sealed class ComfySubmitResult
{
    public ComfySubmitStatus Status { get; init; }
    public string? PromptId { get; init; }
    public string? Message { get; init; }

    public bool Ok => Status == ComfySubmitStatus.Ok;
}

/// <summary>ComfyUI 队列状态（/queue 接口）。</summary>
public sealed class ComfyQueueStatus
{
    public int Running { get; init; }
    public int Pending { get; init; }
    public bool Reachable { get; init; }
}

/// <summary>
/// ComfyUI HTTP 客户端：复用进程级共享 HttpClient 防止 Socket 泄漏，
/// 并为探活与提交分别设置合理的超时。
/// </summary>
public sealed class ComfyClient
{
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public ComfyClient(string baseUrl)
    {
        BaseUrl = Normalize(baseUrl);
    }

    public string BaseUrl { get; }

    /// <summary>提交一个 API 格式工作流。</summary>
    public async Task<ComfySubmitResult> SubmitAsync(JsonObject workflow, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SubmitTimeout);

        try
        {
            var payload = new { prompt = workflow, client_id = "DrawBench" };
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await SharedHttp.PostAsync($"{BaseUrl}/prompt", content, timeoutCts.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return new ComfySubmitResult
                {
                    Status = ComfySubmitStatus.Rejected,
                    Message = DescribeRejection(body, (int)resp.StatusCode)
                };
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("prompt_id", out var id))
            {
                if (doc.RootElement.TryGetProperty("node_errors", out var nodeErrors) &&
                    nodeErrors.ValueKind == JsonValueKind.Object &&
                    nodeErrors.EnumerateObject().Any())
                {
                    return new ComfySubmitResult
                    {
                        Status = ComfySubmitStatus.Rejected,
                        PromptId = id.GetString(),
                        Message = "工作流校验有节点报错：" + nodeErrors.ToString()
                    };
                }

                return new ComfySubmitResult { Status = ComfySubmitStatus.Ok, PromptId = id.GetString() };
            }

            return new ComfySubmitResult
            {
                Status = ComfySubmitStatus.HttpError,
                Message = "响应里没有 prompt_id：" + Truncate(body, 400)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ComfySubmitResult { Status = ComfySubmitStatus.Cancelled, Message = "已取消" };
        }
        catch (OperationCanceledException)
        {
            return new ComfySubmitResult
            {
                Status = ComfySubmitStatus.Unreachable,
                Message = $"连接 {BaseUrl} 超时"
            };
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            return new ComfySubmitResult
            {
                Status = ComfySubmitStatus.Unreachable,
                Message = $"ComfyUI 地址格式无效（{BaseUrl}）：{ex.Message}"
            };
        }
        catch (HttpRequestException ex)
        {
            return new ComfySubmitResult
            {
                Status = ComfySubmitStatus.Unreachable,
                Message = $"连不上 ComfyUI（{BaseUrl}）：{ex.Message}"
            };
        }
        catch (JsonException ex)
        {
            return new ComfySubmitResult
            {
                Status = ComfySubmitStatus.HttpError,
                Message = "ComfyUI 返回的不是 JSON：" + ex.Message
            };
        }
    }

    /// <summary>某个 prompt 是否已经跑完（/history 里有记录即完成）。</summary>
    public async Task<bool> IsDoneAsync(string promptId, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            string escaped = Uri.EscapeDataString(promptId);
            using var resp = await SharedHttp.GetAsync($"{BaseUrl}/history/{escaped}", timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            string json = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(promptId, out _);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>查询队列长度，用于提交前判断 ComfyUI 是否在线（短超时 8 秒）。</summary>
    public async Task<ComfyQueueStatus> GetQueueAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var resp = await SharedHttp.GetAsync($"{BaseUrl}/queue", timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new ComfyQueueStatus { Reachable = false };

            string json = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            int Count(string name)
            {
                if (!doc.RootElement.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return 0;
                return arr.GetArrayLength();
            }

            return new ComfyQueueStatus { Reachable = true, Running = Count("queue_running"), Pending = Count("queue_pending") };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ComfyQueueStatus { Reachable = false };
        }
    }

    public static string Normalize(string? baseUrl)
    {
        string url = (baseUrl ?? "").Trim();
        if (url.Length == 0) url = "http://127.0.0.1:8188";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        return url.TrimEnd('/');
    }

    private static string DescribeRejection(string body, int statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    string details = "";
                    if (error.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.String)
                        details = "（" + d.GetString() + "）";
                    return $"ComfyUI 拒绝了请求：{message.GetString()}{details}";
                }

                if (error.ValueKind == JsonValueKind.String)
                    return "ComfyUI 拒绝了请求：" + error.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return $"ComfyUI 返回 HTTP {statusCode}：{Truncate(body, 400)}";
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
