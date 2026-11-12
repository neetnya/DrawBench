using System.IO;
using System.Windows.Media;
using AIArtToolBox.Modules.Metadata.Services;

namespace AIArtToolBox.Modules.Metadata.Models;

/// <summary>列表里的一个图片条目。</summary>
public sealed class ImageTagInfo : ObservableObject
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    public DateTime ModifiedTime { get; init; }
    public GenerationParameters Params { get; init; } = new();
    public string? Error { get; init; }

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    private ImageSource? _preview;
    public ImageSource? Preview
    {
        get => _preview;
        set => Set(ref _preview, value);
    }

    public bool HasParameters => Params.HasAnyData;

    public string PositivePrompt => Params.PositivePrompt;
    public string NegativePromptText => Params.NegativePrompt;
    public string RawText => Params.RawText;

    public string SourceText => string.IsNullOrWhiteSpace(Params.Source) ? "未知格式" : Params.Source;

    public string FileSizeText => FileSize switch
    {
        >= 1024 * 1024 => $"{FileSize / 1024.0 / 1024.0:0.##} MB",
        >= 1024 => $"{FileSize / 1024.0:0.#} KB",
        _ => $"{FileSize} B"
    };

    public string ResolutionText => Params.ResolutionText;

    public string ModelText => string.IsNullOrWhiteSpace(Params.Model) ? "—" : Params.Model!;

    /// <summary>列表里显示的一行提示词摘要。</summary>
    public string PromptPreview
    {
        get
        {
            var text = Params.PositivePrompt.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 110) text = text[..110] + "…";
            return text;
        }
    }

    public string SubtitleText
    {
        get
        {
            var parts = new List<string> { SourceText };
            if (Params.Steps.HasValue) parts.Add($"{Params.Steps} 步");
            if (!string.IsNullOrWhiteSpace(Params.Sampler)) parts.Add(Params.Sampler!);
            if (!string.IsNullOrWhiteSpace(Params.Seed?.ToString())) parts.Add($"种子 {Params.Seed}");
            return string.Join(" · ", parts);
        }
    }

    public string TooltipText
    {
        get
        {
            var lines = new List<string> { FilePath, $"大小：{FileSizeText}", $"修改时间：{ModifiedTime:yyyy-MM-dd HH:mm}" };
            if (!string.IsNullOrEmpty(Error)) lines.Add($"解析异常：{Error}");
            if (!string.IsNullOrWhiteSpace(PromptPreview)) lines.Add($"提示词：{PromptPreview}");
            return string.Join("\n", lines);
        }
    }

    /// <summary>读取单个文件（纯 IO，可在后台线程调用）。</summary>
    public static ImageTagInfo Load(string path)
    {
        var info = new FileInfo(path);
        var parameters = new GenerationParameters();
        string? error = null;

        try
        {
            var chunks = ImageContainerReader.ReadTextChunks(path);
            parameters = ParameterParser.Parse(chunks);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new ImageTagInfo
        {
            FilePath = path,
            FileName = info.Name,
            FileSize = info.Exists ? info.Length : 0,
            ModifiedTime = info.Exists ? info.LastWriteTime : DateTime.MinValue,
            Params = parameters,
            Error = error
        };
    }
}
