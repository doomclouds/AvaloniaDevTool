using System.Buffers.Binary;
using System.Text;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Reads bounded headers before an image or font reaches a native decoder.</summary>
internal static class AssetMetadataReader
{
    public static AssetImageMetadata? ReadImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
            double? dx = null, dy = null;
            for (var offset = 8; offset <= bytes.Length - 12;)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
                if (count > bytes.Length - offset - 12)
                    break;
                if (bytes.Slice(offset + 4, 4).SequenceEqual("pHYs"u8) && count == 9 && bytes[offset + 16] == 1)
                {
                    dx = BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 8)..]) * .0254;
                    dy = BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 12)..]) * .0254;
                }

                offset += (int)count + 12;
            }

            return new AssetImageMetadata("PNG", width, height, dx, dy);
        }

        if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return new AssetImageMetadata("GIF",
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        if (bytes.Length >= 26 && bytes[..2].SequenceEqual("BM"u8))
        {
            var height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]));
            return new AssetImageMetadata("BMP",
                BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]),
                height <= int.MaxValue ? (int)height : 0);
        }

        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216)
        {
            double? dx = null, dy = null;
            for (var offset = 2; offset < bytes.Length - 4;)
            {
                if (bytes[offset++] != 255)
                    return null;
                while (offset < bytes.Length && bytes[offset] == 255)
                    offset++;
                if (offset >= bytes.Length)
                    break;
                var marker = bytes[offset++];
                if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
                    continue;
                if (marker is 0xD9 or 0xDA || offset > bytes.Length - 2)
                    break;
                var count = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if (count < 2 || count > bytes.Length - offset)
                    break;
                if (marker == 0xE0 && count >= 16 && bytes.Slice(offset + 2, 5).SequenceEqual("JFIF\0"u8))
                {
                    var unit = bytes[offset + 9];
                    if (unit is 1 or 2)
                    {
                        dx = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 10)..]) * (unit == 2 ? 2.54 : 1);
                        dy = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 12)..]) * (unit == 2 ? 2.54 : 1);
                    }
                }

                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && count >= 7)
                    return new AssetImageMetadata("JPEG",
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]),
                        dx,
                        dy);
                offset += count;
            }
        }

        if (bytes.Length >= 30 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
                return new AssetImageMetadata("WebP",
                    1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16),
                    1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16));
            if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) &&
                bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 1, 0x2A }))
                return new AssetImageMetadata("WebP",
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3fff);
            if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && bytes[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
                return new AssetImageMetadata("WebP", (int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
            }
        }

        return null;
    }

    public static AssetFontMetadata? ReadFont(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
            return null;
        var signature = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (signature != 0x00010000 && signature != 0x4F54544F)
            return null;
        var format = signature == 0x4F54544F ? "OpenType" : "TrueType";
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (count == 0 || count > 256 || 12L + 16L * count > bytes.Length)
            return null;
        ReadOnlySpan<byte> names = [];
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.Slice(12 + i * 16, 16);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if ((long)offset + length > bytes.Length)
                return null;
            if (entry[..4].SequenceEqual("name"u8) && length >= 6)
                names = bytes.Slice((int)offset, (int)length);
        }

        if (names.IsEmpty)
            return new AssetFontMetadata(format, null);
        var records = BinaryPrimitives.ReadUInt16BigEndian(names[2..]);
        var storage = BinaryPrimitives.ReadUInt16BigEndian(names[4..]);
        if (6L + records * 12L > names.Length)
            return null;
        string? fallback = null;
        for (var j = 0; j < records; j++)
        {
            var record = names.Slice(6 + j * 12, 12);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var id = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            if (id is not (1 or 16))
                continue;
            var size = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var start = storage + BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (size == 0 || size > 1024 || start + size > names.Length)
                continue;
            var name = (platform is 0 or 3 ? Encoding.BigEndianUnicode : Encoding.Latin1)
                .GetString(names.Slice(start, size)).Trim('\0', ' ');
            if (name.Length == 0 || name.Any(char.IsControl))
                continue;
            if (platform is 0 or 3)
                return new AssetFontMetadata(format, name);
            fallback ??= name;
        }

        return new AssetFontMetadata(format, fallback);
    }
}
