using System;
using System.IO;
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
    HttpError
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
/// ComfyUI HTTP 客户端。原 ComfyBatchTool 只做了 /prompt + 一个没接上的 /history，
/// 这里补齐探测、队列查询和错误体解析，其他模块以后也能直接用。
/// </summary>
public sealed class ComfyClient
{
    private readonly HttpClient _http;

    public ComfyClient(string baseUrl)
    {
        BaseUrl = Normalize(baseUrl);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public string BaseUrl { get; }

    /// <summary>提交一个 API 格式工作流。</summary>
    public async Task<ComfySubmitResult> SubmitAsync(JsonObject workflow, CancellationToken ct = default)
    {
        try
        {
            var payload = new { prompt = workflow, client_id = "DrawBench" };
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{BaseUrl}/prompt", content, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

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
            return new ComfySubmitResult { Status = ComfySubmitStatus.HttpError, Message = "已取消" };
        }
        catch (HttpRequestException ex)
        {
            return new ComfySubmitResult
            {
                Status = ComfySubmitStatus.Unreachable,
                Message = $"连不上 ComfyUI（{BaseUrl}）：{ex.Message}"
            };
        }
        catch (TaskCanceledException)
        {
            return new ComfySubmitResult
            {
                Status = ComfySubmitStatus.Unreachable,
                Message = $"连接 {BaseUrl} 超时"
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
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/history/{promptId}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(promptId, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>查询队列长度，用于提交前判断 ComfyUI 是否在线。</summary>
    public async Task<ComfyQueueStatus> GetQueueAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/queue", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new ComfyQueueStatus { Reachable = false };

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            int Count(string name)
            {
                if (!doc.RootElement.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return 0;
                return arr.GetArrayLength();
            }

            return new ComfyQueueStatus { Reachable = true, Running = Count("queue_running"), Pending = Count("queue_pending") };
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

    /// <summary>
    /// ComfyUI 校验失败时返回的是 {error, node_errors} 结构，
    /// 把里面最有用的一句挑出来，别把整段 JSON 甩到用户脸上。
    /// </summary>
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
            // 落到下面的通用文案
        }

        return $"ComfyUI 返回 HTTP {statusCode}：{Truncate(body, 400)}";
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
