using System;
using System.Collections.Generic;
using System.Text;

namespace DrawBench.Modules.Metadata.Services;

/// <summary>
/// 极简 TIFF/EXIF 读取器：支持 IFD0 与 Exif SubIFD（0x8769），
/// 读取 ImageDescription、UserComment 等文本标签。
/// </summary>
public static class ExifTiffReader
{
    private const int MaxTagBytes = 16 * 1024 * 1024;
    private const int MaxIfdDepth = 2;

    private const ushort TagImageDescription = 0x010E;
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagSoftware = 0x0131;
    private const ushort TagDateTime = 0x0132;
    private const ushort TagArtist = 0x013B;
    private const ushort TagExifIfdPointer = 0x8769;
    private const ushort TagUserComment = 0x9286;

    private static readonly Dictionary<ushort, string> Names = new()
    {
        [TagImageDescription] = "ImageDescription",
        [TagMake] = "Make",
        [TagModel] = "Model",
        [TagSoftware] = "Software",
        [TagDateTime] = "DateTime",
        [TagArtist] = "Artist",
        [TagUserComment] = "UserComment"
    };

    public static Dictionary<string, string> ReadTextTags(byte[] tiff)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tiff.Length < 8) return result;

        bool littleEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I') littleEndian = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') littleEndian = false;
        else return result;

        try
        {
            uint ifdOffset = ReadUInt32(tiff, 4, littleEndian);
            var visited = new HashSet<uint>();
            ReadIfd(tiff, ifdOffset, littleEndian, depth: 0, visited, result);
        }
        catch
        {
            // 坏掉的 EXIF 不影响主流程
        }

        return result;
    }

    private static void ReadIfd(
        byte[] tiff,
        uint ifdOffset,
        bool littleEndian,
        int depth,
        HashSet<uint> visited,
        Dictionary<string, string> result)
    {
        if (depth > MaxIfdDepth || !visited.Add(ifdOffset)) return;
        if ((long)ifdOffset + 2 > tiff.Length) return;

        int count = ReadUInt16(tiff, (int)ifdOffset, littleEndian);
        long pos = (long)ifdOffset + 2;

        for (int i = 0; i < count; i++, pos += 12)
        {
            if (pos + 12 > tiff.Length) break;

            int entryPos = (int)pos;
            ushort tag = ReadUInt16(tiff, entryPos, littleEndian);
            ushort type = ReadUInt16(tiff, entryPos + 2, littleEndian);
            uint valueCount = ReadUInt32(tiff, entryPos + 4, littleEndian);
            uint valueOffset = ReadUInt32(tiff, entryPos + 8, littleEndian);

            // 标准 EXIF 的 UserComment (0x9286) 位于 Exif SubIFD (0x8769)
            if (tag == TagExifIfdPointer && valueCount == 1)
            {
                ReadIfd(tiff, valueOffset, littleEndian, depth + 1, visited, result);
                continue;
            }

            if (!Names.TryGetValue(tag, out string? name)) continue;
            if (valueCount == 0 || valueCount > MaxTagBytes) continue;

            long dataOffset = valueCount <= 4 ? entryPos + 8L : valueOffset;
            if (dataOffset < 0 || dataOffset + valueCount > tiff.Length) continue;

            string? text = type switch
            {
                2 => DecodeUtf8OrLatin1(tiff.AsSpan((int)dataOffset, (int)valueCount)),
                7 => ReadUserComment(tiff, (int)dataOffset, (int)valueCount, littleEndian),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(text)) continue;
            text = text.Trim('\0', ' ', '\r', '\n');
            if (text.Length == 0) continue;
            if (!result.ContainsKey(name)) result[name] = text;
        }
    }

    private static string DecodeUtf8OrLatin1(ReadOnlySpan<byte> bytes)
    {
        string utf8 = Encoding.UTF8.GetString(bytes);
        return utf8.Contains('\uFFFD') ? Encoding.Latin1.GetString(bytes) : utf8;
    }

    private static string ReadUserComment(byte[] data, int offset, int count, bool littleEndian)
    {
        var full = data.AsSpan(offset, count);
        if (count <= 8) return DecodeUtf8OrLatin1(full);

        string prefix = Encoding.ASCII.GetString(data, offset, 8);
        var payload = data.AsSpan(offset + 8, count - 8);

        if (prefix.StartsWith("UNICODE", StringComparison.Ordinal))
        {
            bool le = littleEndian;
            if (payload.Length >= 2)
            {
                if (payload[0] == 0xFF && payload[1] == 0xFE) { le = true; payload = payload[2..]; }
                else if (payload[0] == 0xFE && payload[1] == 0xFF) { le = false; payload = payload[2..]; }
            }
            return (le ? Encoding.Unicode.GetString(payload) : Encoding.BigEndianUnicode.GetString(payload)).Trim('\0');
        }

        if (prefix.StartsWith("ASCII", StringComparison.Ordinal))
            return DecodeUtf8OrLatin1(payload).Trim('\0');

        // 没有标准 8 字节前缀时解码完整内容，避免截掉前 8 个字符
        return DecodeUtf8OrLatin1(full).Trim('\0');
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool littleEndian)
        => littleEndian
            ? (ushort)(data[offset] | (data[offset + 1] << 8))
            : (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
        => littleEndian
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
