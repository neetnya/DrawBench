using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DrawBench.Core.Images;

/// <summary>图片扩展名白名单（三个工具原本各有一份，合并成一份）。</summary>
public static class ImageExtensions
{
    /// <summary>带点的扩展名，小写比较。含 ComfyCollage 与 WD14 都在用的全部格式。</summary>
    public static readonly string[] All =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };

    /// <summary>元数据工具只处理带文本块的容器。</summary>
    public static readonly string[] Metadata = { ".png", ".jpg", ".jpeg", ".jfif", ".webp" };

    /// <summary>反推模型能吃的格式。</summary>
    public static readonly string[] Taggable = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff", ".tif" };

    public static bool Matches(string path, IReadOnlyCollection<string> allowed)
        => allowed.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 文件名自然排序：调用 Windows 的 StrCmpLogicalW，
/// 让「副本 (2)」排在「副本 (10)」前面，与资源管理器一致。
/// </summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        try
        {
            return StrCmpLogicalW(x, y);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class ScanOptions
{
    /// <summary>递归子目录。</summary>
    public bool Recursive { get; set; }

    /// <summary>允许的扩展名（带点）。为空表示不过滤。</summary>
    public IReadOnlyCollection<string> Extensions { get; set; } = ImageExtensions.All;

    /// <summary>跳过以 '.' 或 '_' 开头的目录（拼图工具要跳过 _output）。</summary>
    public bool SkipUnderscoreDirs { get; set; }
}

/// <summary>目录扫描：把三个工具各自写的枚举逻辑收成一处。</summary>
public static class ImageScanner
{
    public static List<string> Scan(string folder, ScanOptions? options = null)
    {
        options ??= new ScanOptions();
        var result = new List<string>();
        if (!Directory.Exists(folder)) return result;

        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = options.Recursive,
            AttributesToSkip = FileAttributes.System
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.*", enumeration))
            {
                if (options.SkipUnderscoreDirs && IsInSkippedDir(folder, file)) continue;
                if (options.Extensions.Count > 0 && !ImageExtensions.Matches(file, options.Extensions)) continue;
                result.Add(file);
            }
        }
        catch
        {
            // 权限等问题：返回已扫到的部分，不抛
        }

        result.Sort(NaturalComparer.Instance.Compare);
        return result;
    }

    private static bool IsInSkippedDir(string root, string file)
    {
        try
        {
            string relative = Path.GetRelativePath(root, file);
            string? dir = Path.GetDirectoryName(relative);
            if (string.IsNullOrEmpty(dir)) return false;

            foreach (string part in dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (part.Length > 0 && (part[0] == '.' || part[0] == '_')) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
