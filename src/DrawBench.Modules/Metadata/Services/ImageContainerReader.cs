using System.IO;
using System.Text;

namespace DrawBench.Modules.Metadata.Services;

/// <summary>
/// 从 PNG / WebP / JPEG 容器里把“文本元数据块”抖出来。
/// 返回的字典 key 是大致固定的名称（prompt、workflow、parameters、Comment、EXIF:UserComment…）。
/// </summary>
public static class ImageContainerReader
{
    private const int MaxChunkBytes = 64 * 1024 * 1024;

    public static Dictionary<string, string> ReadTextChunks(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> head = stackalloc byte[12];
        int read = stream.Read(head);
        stream.Position = 0;

        if (read >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            return ReadPng(stream);

        if (read >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
            head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
            return ReadWebp(stream);

        if (read >= 2 && head[0] == 0xFF && head[1] == 0xD8)
            return ReadJpeg(stream);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ PNG

    private static Dictionary<string, string> ReadPng(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var header = new byte[8];
        stream.ReadExactly(header);
        stream.Position = 8;

        while (stream.Position + 12 <= stream.Length)
        {
            long chunkStart = stream.Position;
            int length = ReadInt32BigEndian(stream);
            if (length < 0 || length > MaxChunkBytes) break;

            var typeBytes = new byte[4];
            stream.ReadExactly(typeBytes);
            string type = Encoding.ASCII.GetString(typeBytes);

            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                var data = new byte[length];
                stream.ReadExactly(data);
                TryAddPngText(result, type, data);
            }
            else if (type is "IEND")
            {
                break;
            }

            // 跳过数据 + CRC
            stream.Position = chunkStart + 8 + length + 4;
        }

        return result;
    }

    private static void TryAddPngText(Dictionary<string, string> result, string type, byte[] data)
    {
        int zero = Array.IndexOf(data, (byte)0);
        if (zero <= 0) return;

        string keyword = Encoding.Latin1.GetString(data, 0, zero);
        string text;

        try
        {
            switch (type)
            {
                case "tEXt":
                    text = Encoding.Latin1.GetString(data, zero + 1, data.Length - zero - 1);
                    break;

                case "zTXt":
                {
                    // keyword\0 compressionMethod(1) compressedText
                    if (zero + 2 > data.Length) return;
                    var compressed = data[(zero + 2)..];
                    text = ZlibDecompress(compressed);
                    break;
                }

                case "iTXt":
                {
                    // keyword\0 compFlag(1) compMethod(1) languageTag\0 translatedKeyword\0 text
                    int p = zero + 1;
                    if (p + 2 > data.Length) return;
                    bool compressed = data[p] != 0;
                    p += 2;
                    int langEnd = Array.IndexOf(data, (byte)0, p);
                    if (langEnd < 0) return;
                    p = langEnd + 1;
                    int transEnd = Array.IndexOf(data, (byte)0, p);
                    if (transEnd < 0) return;
                    p = transEnd + 1;
                    var payload = data[p..];
                    text = compressed ? ZlibDecompress(payload) : Encoding.UTF8.GetString(payload);
                    break;
                }

                default:
                    return;
            }
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;
        if (!result.ContainsKey(keyword)) result[keyword] = text;
    }

    private static int ReadInt32BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
    }

    private static string ZlibDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    // ----------------------------------------------------------------- WebP

    private static Dictionary<string, string> ReadWebp(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var header = new byte[12];
        stream.ReadExactly(header);
        stream.Position = 12;

        while (stream.Position + 8 <= stream.Length)
        {
            var fourccBytes = new byte[4];
            stream.ReadExactly(fourccBytes);
            string fourcc = Encoding.ASCII.GetString(fourccBytes);
            int size = ReadInt32LittleEndian(stream);
            if (size < 0 || size > MaxChunkBytes) break;

            long dataStart = stream.Position;

            if (fourcc == "EXIF")
            {
                var data = new byte[size];
                stream.ReadExactly(data);
                AddExifText(result, data);
            }
            else if (fourcc == "XMP ")
            {
                var data = new byte[size];
                stream.ReadExactly(data);
                string xmp = Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n');
                if (xmp.Length > 0) result["XMP"] = xmp;
            }

            long next = dataStart + size + (size % 2); // 偶数对齐
            if (next <= stream.Position) next = dataStart + size;
            stream.Position = next;
        }

        return result;
    }

    private static int ReadInt32LittleEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
    }

    // ----------------------------------------------------------------- JPEG

    private static Dictionary<string, string> ReadJpeg(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        stream.Position = 2;

        while (stream.Position + 4 <= stream.Length)
        {
            int marker = stream.ReadByte();
            if (marker != 0xFF) continue;

            int code;
            do { code = stream.ReadByte(); } while (code == 0xFF);
            if (code < 0) break;

            // 无长度的标记
            if (code is 0xD8 or 0xD9 or 0x01 || (code >= 0xD0 && code <= 0xD7)) continue;
            if (code == 0xDA) break; // 图像数据开始，后面不会有元数据了

            int high = stream.ReadByte();
            int low = stream.ReadByte();
            if (high < 0 || low < 0) break;
            int length = (high << 8) | low;
            if (length < 2) break;

            var data = new byte[length - 2];
            if (data.Length > 0) stream.ReadExactly(data);

            if (code == 0xE1 && data.Length > 6 &&
                data[0] == 'E' && data[1] == 'x' && data[2] == 'i' && data[3] == 'f' &&
                data[4] == 0 && data[5] == 0)
            {
                AddExifText(result, data[6..]);
            }
            else if (code == 0xE1 && data.Length > 29 &&
                     Encoding.ASCII.GetString(data, 0, 28).Contains("ns.adobe.com/xap/1.0"))
            {
                string xmp = Encoding.UTF8.GetString(data, 29, data.Length - 29).Trim('\0');
                if (xmp.Length > 0) result["XMP"] = xmp;
            }
            else if (code == 0xFE) // COM
            {
                string comment = DecodeText(data);
                if (!string.IsNullOrWhiteSpace(comment) && !result.ContainsKey("Comment"))
                    result["Comment"] = comment;
            }
        }

        return result;
    }

    // ----------------------------------------------------------------- EXIF

    private static void AddExifText(Dictionary<string, string> result, byte[] tiff)
    {
        var tags = ExifTiffReader.ReadTextTags(tiff);
        foreach (var kv in tags)
        {
            string key = kv.Key switch
            {
                "ImageDescription" => "EXIF:ImageDescription",
                "UserComment" => "EXIF:UserComment",
                "Software" => "EXIF:Software",
                "Make" => "EXIF:Make",
                "Model" => "EXIF:Model",
                "Artist" => "EXIF:Artist",
                "DateTime" => "EXIF:DateTime",
                _ => "EXIF:" + kv.Key
            };
            if (!result.ContainsKey(key)) result[key] = kv.Value;
        }
    }

    private static string DecodeText(byte[] data)
    {
        int start = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) start = 3;
        return Encoding.UTF8.GetString(data, start, data.Length - start);
    }
}
