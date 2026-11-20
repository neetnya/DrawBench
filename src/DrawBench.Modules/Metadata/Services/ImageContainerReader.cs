using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DrawBench.Modules.Metadata.Services;

/// <summary>
/// 从 PNG / WebP / JPEG 容器里把“文本元数据块”抖出来。
/// </summary>
public static class ImageContainerReader
{
    private const int MaxChunkBytes = 64 * 1024 * 1024;
    private const int MaxDecompressedBytes = 16 * 1024 * 1024;

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
        if (stream.Length < 8) return result;
        stream.Position = 8;

        Span<byte> typeBytes = stackalloc byte[4];
        while (stream.Position + 12 <= stream.Length)
        {
            long chunkStart = stream.Position;
            int length = ReadInt32BigEndian(stream);
            if (length < 0 || length > MaxChunkBytes) break;

            stream.ReadExactly(typeBytes);
            string type = Encoding.ASCII.GetString(typeBytes);

            long remaining = stream.Length - stream.Position;
            if (length > remaining) break;

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

            long next = chunkStart + 8L + length + 4L;
            if (next > stream.Length) break;
            stream.Position = next;
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
                    if (zero + 2 > data.Length) return;
                    var compressed = data[(zero + 2)..];
                    text = ZlibDecompress(compressed);
                    break;
                }

                case "iTXt":
                {
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
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int total = 0;
        int n;
        while ((n = zlib.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > MaxDecompressedBytes)
                throw new InvalidDataException("压缩文本块超过最大允许体积。");
            output.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    // ----------------------------------------------------------------- WebP

    private static Dictionary<string, string> ReadWebp(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stream.Length < 12) return result;
        stream.Position = 12;

        Span<byte> fourccBytes = stackalloc byte[4];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(fourccBytes);
            string fourcc = Encoding.ASCII.GetString(fourccBytes);
            int size = ReadInt32LittleEndian(stream);
            if (size < 0 || size > MaxChunkBytes) break;

            long dataStart = stream.Position;
            if (size > stream.Length - dataStart) break;

            if (fourcc == "EXIF")
            {
                var data = new byte[size];
                stream.ReadExactly(data);
                // Pillow / ComfyUI 写入的 WebP EXIF 块常带 6 字节 Exif\0\0 前缀
                if (HasExifHeader(data))
                    AddExifText(result, data[6..]);
                else
                    AddExifText(result, data);
            }
            else if (fourcc == "XMP ")
            {
                var data = new byte[size];
                stream.ReadExactly(data);
                string xmp = Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n');
                if (xmp.Length > 0) result["XMP"] = xmp;
            }

            long next = dataStart + size + (size % 2);
            if (next <= dataStart || next > stream.Length) break;
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
        if (stream.Length < 2) return result;
        stream.Position = 2;

        while (stream.Position + 2 <= stream.Length)
        {
            int marker = stream.ReadByte();
            if (marker < 0) break;
            if (marker != 0xFF) continue;

            int code;
            do { code = stream.ReadByte(); } while (code == 0xFF);
            if (code < 0) break;

            if (code == 0x00) continue; // 字节填充
            if (code == 0xD9 || code == 0xDA) break; // EOI 或 SOS（图像数据开始）
            if (code is 0xD8 or 0x01 || (code >= 0xD0 && code <= 0xD7)) continue;

            int high = stream.ReadByte();
            int low = stream.ReadByte();
            if (high < 0 || low < 0) break;
            int length = (high << 8) | low;
            if (length < 2) break;

            int payloadLen = length - 2;
            if (payloadLen > stream.Length - stream.Position) break;

            // 仅对 APP1(0xE1) 与 COM(0xFE) 分配缓冲区，其余段直接跳过
            if (code != 0xE1 && code != 0xFE)
            {
                stream.Seek(payloadLen, SeekOrigin.Current);
                continue;
            }

            var data = new byte[payloadLen];
            if (payloadLen > 0) stream.ReadExactly(data);

            if (code == 0xE1 && HasExifHeader(data))
            {
                AddExifText(result, data[6..]);
            }
            else if (code == 0xE1 && data.Length > 29 &&
                     data[28] == 0 &&
                     Encoding.ASCII.GetString(data, 0, 28).Contains("ns.adobe.com/xap/1.0", StringComparison.Ordinal))
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

    private static bool HasExifHeader(byte[] data) =>
        data.Length > 6 &&
        data[0] == 'E' && data[1] == 'x' && data[2] == 'i' && data[3] == 'f' &&
        data[4] == 0 && data[5] == 0;

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
