using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DrawBench.Core.Logging;

namespace DrawBench.Core;

/// <summary>
/// 统一的 settings.json（位于 data\ 下）。各模块各占一个段，互不覆盖。
/// 写盘采用临时文件原子替换，避免中途崩溃损坏原有配置。
/// </summary>
public sealed class SettingsStore
{
    private static readonly Lazy<SettingsStore> SharedLazy = new(() => new SettingsStore());

    /// <summary>全进程唯一实例。</summary>
    public static SettingsStore Shared => SharedLazy.Value;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.Current.SettingsPath;
        Load();
    }

    public string Path_ => _path;

    /// <summary>读出某段的强类型对象；没有或解析失败时返回 null。</summary>
    public T? Get<T>(string section) where T : class
    {
        lock (_gate)
        {
            if (!_sections.TryGetValue(section, out string? json)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(json, SettingsJson.Options);
            }
            catch (Exception ex)
            {
                AppLog.Error($"解析配置段 [{section}] 失败", ex);
                return null;
            }
        }
    }

    /// <summary>写入某段并立即原子落盘。</summary>
    public void Set<T>(string section, T value)
    {
        lock (_gate)
        {
            try
            {
                _sections[section] = JsonSerializer.Serialize(value, SettingsJson.Options);
            }
            catch (Exception ex)
            {
                AppLog.Error($"序列化配置段 [{section}] 失败", ex);
                return;
            }
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in doc.RootElement.EnumerateObject())
                _sections[prop.Name] = prop.Value.GetRawText();
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取配置文件失败（{_path}）", ex);
        }
    }

    private void Save()
    {
        string? tmpPath = null;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            tmpPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var kv in _sections)
                {
                    writer.WritePropertyName(kv.Key);
                    writer.WriteRawValue(kv.Value, skipInputValidation: true);
                }
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmpPath, _path, overwrite: true);
            tmpPath = null;
        }
        catch (Exception ex)
        {
            AppLog.Error($"保存配置文件失败（{_path}）", ex);
        }
        finally
        {
            if (tmpPath != null)
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }
}

/// <summary>全应用统一的 JSON 选项：缩进、宽松、中文不转义。</summary>
public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>
/// 全局段（shell 自己用）：Python 解释器路径。
/// ComfyUI 地址不在这里——它只属于批量提交模块，存在 "batch" 段。
/// </summary>
public sealed class AppSection
{
    /// <summary>留空 = 自动探测。</summary>
    public string? PythonPath { get; set; }
}
