using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DrawBench.Core.Logging;

namespace DrawBench.Modules.Tagger.Core;

/// <summary>
/// 结果落盘：软件目录的 data\results.csv。
/// 启动时读入已有记录（按「目录+文件名」去重，同一张图再次反推就是覆盖那一行，不会追加重复行）。
/// 列：文件名,目录,标签数,评级,评级分,标签。
/// </summary>
public sealed class ResultWriter
{
    private const string Header = "文件名,目录,标签数,评级,评级分,标签";

    private readonly object _sync = new();
    private readonly string _csvPath;
    private readonly List<string> _lines = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private bool _loadFailed;
    private bool _dirty;

    public ResultWriter(string csvPath) => _csvPath = Path.GetFullPath(csvPath);

    /// <summary>CSV 里存下的一条结果（原始值）。</summary>
    public readonly record struct SavedRecord(
        string FileName, string Directory, int TagCount, string Rating, string RatingScore, string Tags);

    /// <summary>读出的全部历史记录，顺序就是 CSV 里的顺序（越靠后越新）。</summary>
    public IReadOnlyList<SavedRecord> GetRecords()
    {
        lock (_sync)
        {
            EnsureLoaded();

            var list = new List<SavedRecord>(_lines.Count);
            foreach (string line in _lines)
            {
                var cells = SplitCsv(line);
                if (cells.Count < 6) continue;
                list.Add(new SavedRecord(cells[0], cells[1],
                    int.TryParse(cells[2], out int count) ? count : 0,
                    cells[3], cells[4], cells[5]));
            }
            return list;
        }
    }

    /// <summary>按图片路径查一条历史记录。</summary>
    public bool TryGet(string imagePath, out SavedRecord record)
    {
        record = default;
        lock (_sync)
        {
            EnsureLoaded();

            string file = Path.GetFileName(imagePath);
            string dir = Path.GetDirectoryName(imagePath) ?? "";
            if (!_index.TryGetValue(MakeKey(dir, file), out int at)) return false;

            var cells = SplitCsv(_lines[at]);
            if (cells.Count < 6) return false;

            record = new SavedRecord(cells[0], cells[1],
                int.TryParse(cells[2], out int count) ? count : 0,
                cells[3], cells[4], cells[5]);
            return true;
        }
    }

    /// <summary>删掉一条历史记录（界面上删掉队列项时同步调用，免得下次打开又冒出来）。</summary>
    public void Remove(string imagePath)
    {
        lock (_sync)
        {
            EnsureLoaded();

            string file = Path.GetFileName(imagePath);
            string dir = Path.GetDirectoryName(imagePath) ?? "";
            string key = MakeKey(dir, file);
            if (!_index.TryGetValue(key, out int at)) return;

            _lines.RemoveAt(at);
            _index.Remove(key);

            // 后面的索引整体前移
            foreach (string k in _index.Keys.ToArray())
                if (_index[k] > at) _index[k]--;

            Save();
        }
    }

    /// <summary>已载入的历史记录条数（首次访问时从磁盘读取）。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                EnsureLoaded();
                return _lines.Count;
            }
        }
    }

    /// <summary>清空全部记录，并把磁盘上的文件重写成只有表头（相当于删掉 results.csv）。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            EnsureLoaded();
            _lines.Clear();
            _index.Clear();
            Save();
        }
    }

    public void Write(string imagePath, TagResult result, TaggerOptions options, bool flush = true)
    {
        lock (_sync)
        {
            EnsureLoaded();
            if (_loadFailed)
                throw new IOException($"无法读取原有结果文件（可能正被 Excel 等程序占用），已中止写入以避免覆盖历史数据：{_csvPath}");

            string file = Path.GetFileName(imagePath);
            string dir = Path.GetDirectoryName(imagePath) ?? "";
            string key = MakeKey(dir, file);

            var sb = new StringBuilder(256);
            sb.Append(Csv(file)).Append(',');
            sb.Append(Csv(dir)).Append(',');
            sb.Append(result.Tags.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(options.OutputRating ? result.Rating : "")).Append(',');
            sb.Append(options.OutputRating ? result.RatingScore.ToString("0.0000", CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(Csv(result.Text));
            string line = sb.ToString();

            if (_index.TryGetValue(key, out int at)) _lines[at] = line;
            else
            {
                _index[key] = _lines.Count;
                _lines.Add(line);
            }

            _dirty = true;
            if (flush) Save();
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (!_dirty || _loadFailed) return;
            Save();
        }
    }

    private static string MakeKey(string dir, string file) => dir.TrimEnd('\\', '/') + "\u0001" + file;

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!File.Exists(_csvPath)) return;

        try
        {
            foreach (string raw in File.ReadAllLines(_csvPath, new UTF8Encoding(true)))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var cells = SplitCsv(raw);
                if (cells.Count == 0) continue;

                // 表头
                if (cells[0] is "文件名" or "时间" || cells[0].StartsWith("时间", StringComparison.Ordinal)) continue;

                // 旧版 11 列（时间,文件名,目录,模型,阈值,角色阈值,标签数,评级,评级分,标签,耗时ms）→ 迁移到新格式
                string file, dir, tagCount, rating, score, tags;
                if (cells.Count >= 10)
                {
                    file = cells[1];
                    dir = cells[2];
                    tagCount = cells[6];
                    rating = cells[7];
                    score = cells[8];
                    tags = cells[9];
                }
                else if (cells.Count >= 6)
                {
                    file = cells[0];
                    dir = cells[1];
                    tagCount = cells[2];
                    rating = cells[3];
                    score = cells[4];
                    tags = cells[5];
                }
                else continue;

                var sb = new StringBuilder(256);
                sb.Append(Csv(file)).Append(',');
                sb.Append(Csv(dir)).Append(',');
                sb.Append(Csv(tagCount)).Append(',');
                sb.Append(Csv(rating)).Append(',');
                sb.Append(Csv(score)).Append(',');
                sb.Append(Csv(tags));

                string key = MakeKey(dir, file);
                if (_index.TryGetValue(key, out int at)) _lines[at] = sb.ToString();
                else
                {
                    _index[key] = _lines.Count;
                    _lines.Add(sb.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            AppLog.Error($"读取打标结果 CSV 失败（{_csvPath}）", ex);
        }
    }

    private void Save()
    {
        string? dir = Path.GetDirectoryName(_csvPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder(64 + _lines.Count * 128);
        sb.AppendLine(Header);
        foreach (string line in _lines) sb.AppendLine(line);

        string tmp = $"{_csvPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            File.Move(tmp, _csvPath, overwrite: true);
            _dirty = false;
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool needQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>(8);
        var sb = new StringBuilder(64);
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }

        cells.Add(sb.ToString());
        return cells;
    }
}
