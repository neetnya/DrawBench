using System.Text;

namespace AIArtToolBox.Modules.Metadata.Services;

/// <summary>
/// 极简 TIFF/EXIF 读取器：只读 IFD0 里的 ASCII / UNDEFINED 文本（ImageDescription、UserComment 等）。
/// 够用来抓 NovelAI / ComfyUI 写进 WebP、JPEG 里的参数。
/// </summary>
public static class ExifTiffReader
{
    private const ushort TagImageDescription = 0x010E;
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagSoftware = 0x0131;
    private const ushort TagDateTime = 0x0132;
    private const ushort TagArtist = 0x013B;
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
            if (ifdOffset + 2 > tiff.Length) return result;

            int count = ReadUInt16(tiff, (int)ifdOffset, littleEndian);
            int pos = (int)ifdOffset + 2;

            for (int i = 0; i < count; i++, pos += 12)
            {
                if (pos + 12 > tiff.Length) break;

                ushort tag = ReadUInt16(tiff, pos, littleEndian);
                ushort type = ReadUInt16(tiff, pos + 2, littleEndian);
                uint valueCount = ReadUInt32(tiff, pos + 4, littleEndian);
                uint valueOffset = ReadUInt32(tiff, pos + 8, littleEndian);

                if (!Names.TryGetValue(tag, out string? name)) continue;
                if (valueCount == 0 || valueCount > 65536) continue;

                int dataOffset = valueCount <= 4 ? pos + 8 : (int)valueOffset;
                if (dataOffset < 0 || dataOffset + (long)valueCount > tiff.Length) continue;

                string? text = type switch
                {
                    2 => ReadAscii(tiff, dataOffset, (int)valueCount),
                    7 => ReadUserComment(tiff, dataOffset, (int)valueCount, littleEndian),
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(text)) continue;
                text = text.Trim('\0', ' ', '\r', '\n');
                if (text.Length == 0) continue;
                if (!result.ContainsKey(name)) result[name] = text;
            }
        }
        catch
        {
            // 坏掉的 EXIF 不影响主流程
        }

        return result;
    }

    private static string ReadAscii(byte[] data, int offset, int count)
        => Encoding.Latin1.GetString(data, offset, count);

    private static string ReadUserComment(byte[] data, int offset, int count, bool littleEndian)
    {
        if (count <= 8) return Encoding.Latin1.GetString(data, offset, count);

        string prefix = Encoding.ASCII.GetString(data, offset, 8);
        var payload = new ReadOnlySpan<byte>(data, offset + 8, count - 8);

        if (prefix.StartsWith("UNICODE", StringComparison.Ordinal))
        {
            bool le = littleEndian;
            if (payload.Length >= 2)
            {
                if (payload[0] == 0xFF && payload[1] == 0xFE) { le = true; payload = payload[2..]; }
                else if (payload[0] == 0xFE && payload[1] == 0xFF) { le = false; payload = payload[2..]; }
            }
            return le
                ? Encoding.Unicode.GetString(payload)
                : Encoding.BigEndianUnicode.GetString(payload);
        }

        if (prefix.StartsWith("ASCII", StringComparison.Ordinal))
            return Encoding.ASCII.GetString(payload).Trim('\0');

        // 没有标准前缀：先当 UTF-8 试
        string guess = Encoding.UTF8.GetString(payload).Trim('\0');
        return guess.Contains('\uFFFD') ? Encoding.Latin1.GetString(payload).Trim('\0') : guess;
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
