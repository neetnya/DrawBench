using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Data;
using DrawBench.Modules.Metadata.Helpers;
using DrawBench.Modules.Metadata.Models;

namespace DrawBench.Modules.Metadata.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public static readonly string[] SupportedExtensions =
        { ".png", ".webp", ".jpg", ".jpeg", ".jfif" };

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _previewCts;

    public MainViewModel()
    {
        ImagesView = CollectionViewSource.GetDefaultView(Images);
        ImagesView.Filter = FilterItem;
    }

    public ObservableCollection<ImageTagInfo> Images { get; } = new();

    public ICollectionView ImagesView { get; }

    private string _folderPath = "";
    public string FolderPath
    {
        get => _folderPath;
        set => Set(ref _folderPath, value);
    }

    private bool _includeSubFolders = true;
    public bool IncludeSubFolders
    {
        get => _includeSubFolders;
        set => Set(ref _includeSubFolders, value);
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            ImagesView.Refresh();
            UpdateStatusForFilter();
        }
    }

    private string _statusText = "选择或拖入图片文件夹以读取生成参数";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    private ImageTagInfo? _selected;
    public ImageTagInfo? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!Set(ref _selected, value)) return;
            if (previous is not null) previous.Preview = null;
            _ = LoadPreviewAsync(value);
        }
    }

    public void CancelPendingWork()
    {
        ReplaceCts(ref _scanCts, null);
        ReplaceCts(ref _previewCts, null);
    }

    private static void ReplaceCts(ref CancellationTokenSource? field, CancellationTokenSource? next)
    {
        var old = field;
        field = next;
        if (old is not null)
        {
            try { old.Cancel(); } catch { }
            old.Dispose();
        }
    }

    public bool HasImages => Images.Count > 0;

    private int _parsedCount;

    // ---------------------------------------------------------------- 扫描

    public async Task LoadFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!Directory.Exists(folder))
        {
            StatusText = "文件夹不存在：" + folder;
            return;
        }

        var cts = new CancellationTokenSource();
        ReplaceCts(ref _scanCts, cts);
        ReplaceCts(ref _previewCts, null);

        FolderPath = folder;
        Images.Clear();
        Selected = null;
        OnPropertyChanged(nameof(HasImages));
        IsBusy = true;
        StatusText = "正在解析图片元数据…";

        List<string> files;
        try
        {
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = IncludeSubFolders,
                IgnoreInaccessible = true
            };
            files = await Task.Run(() =>
            {
                var matched = new List<string>();
                foreach (var f in Directory.EnumerateFiles(folder, "*", enumOptions))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                        matched.Add(f);
                }
                matched.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
                return matched;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_scanCts, cts)) return;
            IsBusy = false;
            StatusText = "读取文件夹失败：" + ex.Message;
            return;
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(_scanCts, cts)) return;

        if (files.Count == 0)
        {
            IsBusy = false;
            StatusText = "这个文件夹里没有找到图片（支持 png / webp / jpg）";
            return;
        }

        List<ImageTagInfo> items;
        try
        {
            items = await Task.Run(() =>
            {
                var list = new List<ImageTagInfo>(files.Count);
                foreach (var f in files)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    list.Add(ImageTagInfo.Load(f));
                }
                return list;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested) return;

        foreach (var item in items) Images.Add(item);
        OnPropertyChanged(nameof(HasImages));

        _parsedCount = items.Count(i => i.HasParameters);
        IsBusy = false;
        RefreshStatus();
        Selected = Images.FirstOrDefault();

        _ = LoadThumbnailsAsync(items, cts.Token);
    }

    public void RefreshStatus()
    {
        _parsedCount = Images.Count(i => i.HasParameters);
        UpdateStatusForFilter();
    }

    private void UpdateStatusForFilter()
    {
        if (Images.Count == 0)
        {
            StatusText = "选择或拖入图片文件夹以读取生成参数";
            return;
        }

        int visible = ImagesView.Cast<object>().Count();
        var baseText = $"共 {Images.Count} 张图片，{_parsedCount} 张解析到参数";
        if (visible != Images.Count) baseText += $"（当前筛选显示 {visible} 张）";
        StatusText = baseText;
    }

    // ---------------------------------------------------------------- 预览 / 缩略图

    private async Task LoadPreviewAsync(ImageTagInfo? item)
    {
        var cts = new CancellationTokenSource();
        ReplaceCts(ref _previewCts, cts);

        if (item is null || item.Preview is not null) return;

        try
        {
            var image = await Task.Run(() => BitmapHelper.TryLoad(item.FilePath, 1400), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(item, Selected)) return;
            if (image is not null) item.Preview = image;
        }
        catch (OperationCanceledException)
        {
            // 快速切换选择时忽略
        }
        catch
        {
            // 预览加载失败不影响元数据展示
        }
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<ImageTagInfo> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) return;
            if (item.Thumbnail is not null) continue;

            try
            {
                var thumb = await Task.Run(() => BitmapHelper.TryLoad(item.FilePath, 120), token);
                if (token.IsCancellationRequested) return;
                if (thumb is not null) item.Thumbnail = thumb;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 单张失败不打断整体
            }
        }
    }

    // ---------------------------------------------------------------- 导出

    public void ExportJson(string path)
    {
        var payload = Images.Select(i => new
        {
            file = i.FilePath,
            name = i.FileName,
            size = i.FileSize,
            modified = i.ModifiedTime.ToString("yyyy-MM-dd HH:mm:ss"),
            source = i.Params.Source,
            model = i.Params.Model,
            modelHash = i.Params.ModelHash,
            vae = i.Params.Vae,
            sampler = i.Params.Sampler,
            scheduler = i.Params.Scheduler,
            steps = i.Params.Steps,
            cfg = i.Params.CfgScale,
            seed = i.Params.Seed,
            width = i.Params.Width,
            height = i.Params.Height,
            denoise = i.Params.Denoise,
            clipSkip = i.Params.ClipSkip,
            positive = i.Params.PositivePrompt,
            negative = i.Params.NegativePrompt,
            extra = i.Params.Extra,
            error = i.Error
        }).ToList();

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, options), new UTF8Encoding(false));
        StatusText = $"已导出 JSON（{payload.Count} 条）：{path}";
    }

    public void ExportCsv(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("文件名,完整路径,生成工具,模型,模型哈希,VAE,采样器,调度器,步数,CFG,种子,宽,高,降噪,ClipSkip,正向提示词,负向提示词,附加参数");

        foreach (var item in Images)
        {
            var p = item.Params;
            string extra = string.Join(" | ", p.Extra.Select(kv => $"{kv.Key}={kv.Value}"));
            var cells = new[]
            {
                Csv(item.FileName),
                Csv(item.FilePath),
                Csv(p.Source),
                Csv(p.Model),
                Csv(p.ModelHash),
                Csv(p.Vae),
                Csv(p.Sampler),
                Csv(p.Scheduler),
                Csv(p.Steps?.ToString()),
                Csv(p.CfgScale?.ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(p.Seed?.ToString(CultureInfo.InvariantCulture)),
                Csv(p.Width?.ToString(CultureInfo.InvariantCulture)),
                Csv(p.Height?.ToString(CultureInfo.InvariantCulture)),
                Csv(p.Denoise?.ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(p.ClipSkip),
                Csv(p.PositivePrompt),
                Csv(p.NegativePrompt),
                Csv(extra)
            };
            builder.AppendLine(string.Join(",", cells));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        StatusText = $"已导出 CSV（{Images.Count} 条）：{path}";
    }

    public string BuildDetailText(ImageTagInfo item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("【文件】" + item.FilePath);
        builder.AppendLine("【生成工具】" + item.Params.Source);
        builder.AppendLine("【模型】" + (item.Params.Model ?? "—"));
        builder.AppendLine("【参数】" +
            string.Join(", ", item.Params.ParameterItems
                .Where(kv => kv.Key is not "生成工具" and not "模型")
                .Select(kv => $"{kv.Key}: {kv.Value}")));
        builder.AppendLine();
        builder.AppendLine("【正向提示词】");
        builder.AppendLine(item.Params.PositivePrompt);
        if (!string.IsNullOrWhiteSpace(item.Params.NegativePrompt))
        {
            builder.AppendLine();
            builder.AppendLine("【负向提示词】");
            builder.AppendLine(item.Params.NegativePrompt);
        }
        return builder.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= "";
        value = value.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
        if (value.Contains(',') || value.Contains('"'))
            value = '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }

    private bool FilterItem(object obj)
    {
        if (obj is not ImageTagInfo item) return false;

        var text = SearchText?.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        if (item.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Params.PositivePrompt.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Params.NegativePrompt.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Params.Source.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Params.Model?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (item.Params.Sampler?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) return true;

        return false;
    }
}
