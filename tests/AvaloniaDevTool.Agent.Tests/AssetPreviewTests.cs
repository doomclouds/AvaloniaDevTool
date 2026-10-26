using System.Buffers.Binary;
using System.Text;
using Avalonia.Fonts.Inter;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class AssetPreviewTests
{
    [Fact]
    public void Escaped_text_preview_stays_within_rpc_frame_budget()
    {
        var body = new byte[1000000];
        body.AsSpan(700000).Fill((byte)'a');
        using var bytes = new MemoryStream(body);
        var preview = AssetInspectionService.ReadPreview(new Uri("avares://fixture/control.txt"), bytes);
        Assert.True(preview.IsTruncated);
        Assert.True(preview.Text!.Length < 1000000);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(preview, ProtocolJson.Options).Length <
                    RpcPeer.MaximumFrameBytes);
        Assert.Equal(1000000, preview.TotalByteLength);
        Assert.Null(new AssetImageMetadata("invalid", int.MaxValue, int.MaxValue).DecodedPixelBytes);
    }

    [Fact]
    public void Text_preview_reports_total_and_preserves_complete_utf8_characters_at_boundary()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', AssetInspectionService.PreviewLimit - 1) + "资产尾部");
        using var stream = new MemoryStream(body);
        var preview = AssetInspectionService.ReadPreview(new Uri("avares://fixture/large.json"), stream);
        Assert.True(preview.IsTruncated);
        Assert.Equal(body.Length, preview.TotalByteLength);
        Assert.Equal(AssetInspectionService.PreviewLimit, preview.ByteLength);
        Assert.Equal(new string('x', AssetInspectionService.PreviewLimit - 1), preview.Text);
        Assert.Null(preview.Base64);
    }

    [Fact]
    public void Png_dimensions_dpi_and_decoded_memory_are_separate_from_encoded_size()
    {
        var bytes = new byte[54];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 400);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 200);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(33), 9);
        "pHYs"u8.CopyTo(bytes.AsSpan(37));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(41), 3780);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(45), 3780);
        bytes[49] = 1;
        using var stream = new MemoryStream(bytes);
        var preview = AssetInspectionService.ReadPreview(new Uri("avares://fixture/image.png"), stream);
        Assert.Equal(new AssetImageMetadata("PNG", 400, 200, 96.012, 96.012), preview.Image);
        Assert.NotNull(preview.Image);
        Assert.Equal(320000, preview.Image.DecodedPixelBytes);
        Assert.Equal(bytes.Length, preview.TotalByteLength);
        Assert.NotNull(preview.Base64);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 100000);
        stream.Position = 0;
        var bomb = AssetInspectionService.ReadPreview(new Uri("avares://fixture/image.png"), stream);
        Assert.Null(bomb.Base64);
        Assert.Contains("16 Mi", bomb.PreviewNotice);
    }

    [AvaloniaFact]
    public void Font_preview_uses_official_embedded_font_bytes_and_invalid_font_is_never_text()
    {
        _ = typeof(InterFontCollection).Assembly;
        var asset = AssetInspectionService.List()
            .First(item => item.AssemblyName == "Avalonia.Fonts.Inter" && item.Extension == ".ttf");
        var preview = AssetInspectionService.Open(asset.Uri);
        Assert.Equal("font/ttf", preview.MediaType);
        Assert.Null(preview.Text);
        Assert.NotNull(preview.Font?.FamilyName);
        Assert.Contains("Inter", preview.Font.FamilyName);
        Assert.NotNull(preview.Base64);
        Assert.False(preview.IsTruncated);
        using var invalid = new MemoryStream("invalid binary font"u8.ToArray());
        var malformed = AssetInspectionService.ReadPreview(new Uri("avares://fixture/bad.ttf"), invalid);
        Assert.Null(malformed.Base64);
        Assert.Null(malformed.Text);
        Assert.Null(malformed.Font);
        Assert.Contains("字体格式无效", malformed.PreviewNotice);
    }

    [Fact]
    public void Unknown_length_stream_does_not_report_preview_size_as_full_size()
    {
        using var stream = new NonSeekStream(new byte[AssetInspectionService.PreviewLimit + 500]);
        var preview = AssetInspectionService.ReadPreview(new Uri("avares://fixture/unknown.bin"), stream);
        Assert.True(preview.IsTruncated);
        Assert.Null(preview.TotalByteLength);
        Assert.Equal(AssetInspectionService.PreviewLimit, preview.ByteLength);
    }

    private sealed class NonSeekStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
