using System;
using System.IO;
using System.Text.Json;

namespace DrawBench.Core;

/// <summary>
/// 统一的 settings.json（位于 data\ 下）。各模块各占一个段，互不覆盖：
///
/// <code>
/// {
///   "app":     { "lastModule": "batch", "comfyBaseUrl": "http://127.0.0.1:8188", "pythonPath": "" },
///   "batch":   { ... },
///   "collage": { ... },
///   "tagger":  { ... },
///   "gacha":   { ... }
/// }
/// </code>
///
/// 用「段 → JSON 原文」的方式存，模块读写自己的强类型对象，Core 不关心各模块长什么样。
/// 旧版分散的配置文件由 <see cref="ImportLegacySection"/> 一次性搬进对应段。
/// </summary>
public sealed class SettingsStore
{
    private static SettingsStore? _shared;

    /// <summary>
    /// 全进程唯一实例。模块和外壳必须共用它——各自 new 一份会各自缓存文件内容，
    /// 任一方写盘时会把别家刚写进去的段覆盖掉。
    /// </summary>
    public static SettingsStore Shared => _shared ??= new SettingsStore();

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
            catch
            {
                return null;
            }
        }
    }

    /// <summary>写入某段并立即落盘。</summary>
    public void Set<T>(string section, T value)
    {
        lock (_gate)
        {
            try
            {
                _sections[section] = JsonSerializer.Serialize(value, SettingsJson.Options);
            }
            catch
            {
                return;
            }
            Save();
        }
    }

    /// <summary>把一段外部 JSON（旧配置文件全文）写进指定段。</summary>
    public bool ImportLegacySection(string section, string legacyFilePath)
    {
        try
        {
            if (!File.Exists(legacyFilePath)) return false;
            string text = File.ReadAllText(legacyFilePath);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            lock (_gate)
            {
                if (_sections.ContainsKey(section)) return false;   // 已经有新配置就不覆盖
                _sections[section] = doc.RootElement.GetRawText();
                Save();
            }
            return true;
        }
        catch
        {
            return false;
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
        catch
        {
            // 配置损坏按首次运行处理，不影响启动
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            using var stream = File.Create(_path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            foreach (var kv in _sections)
            {
                writer.WritePropertyName(kv.Key);
                writer.WriteRawValue(kv.Value, skipInputValidation: true);
            }
            writer.WriteEndObject();
        }
        catch
        {
            // 目录只读等情况忽略：配置丢了不影响本次使用
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
/// 全局段（shell 自己用）：上次停留的模块、ComfyUI 地址、Python 解释器路径。
/// </summary>
public sealed class AppSection
{
    public string? LastModule { get; set; }
    public string ComfyBaseUrl { get; set; } = "http://127.0.0.1:8188";

    /// <summary>留空 = 自动探测。</summary>
    public string? PythonPath { get; set; }

    public bool SidebarCollapsed { get; set; }
}
