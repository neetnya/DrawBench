using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace DrawBench.Modules.Metadata.Services;

/// <summary>JsonNode 取值的小工具，避免重复写 TryGetValue。</summary>
internal static class JsonUtil
{
    public static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        return value.ToJsonString().Trim('"');
    }

    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return (long)Math.Round(d);
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    public static int? Int(JsonNode? node)
    {
        var v = Long(node);
        if (v is null) return null;
        if (v > int.MaxValue || v < int.MinValue) return null;
        return (int)v.Value;
    }

    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    public static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        return null;
    }
}
