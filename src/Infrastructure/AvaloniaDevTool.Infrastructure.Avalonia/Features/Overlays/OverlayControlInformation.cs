using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Explicit framework API allowlist. Never reads text contents or arbitrary source getters.</summary>
internal static class OverlayControlInformation
{
    internal static OverlayControlInfo? Capture(Control target, Func<object, long>? observe)
    {
        var fields = new List<OverlayInfoField>();
        if (target is TextBlock block)
        {
            AddFont(block);
            Add("Wrapping", block.GetValue(TextBlock.TextWrappingProperty).ToString());
            Add("Trimming", Trimming(block.GetValue(TextBlock.TextTrimmingProperty)));
            Add("Alignment", block.GetValue(TextBlock.TextAlignmentProperty).ToString());
            Add("MaxLines", block.GetValue(TextBlock.MaxLinesProperty));
            AddNumber("LineHeightDip", block.GetValue(TextBlock.LineHeightProperty));
            AddNumber("LineSpacingDip", block.GetValue(TextBlock.LineSpacingProperty));
            AddNumber("LetterSpacingDip", block.GetValue(TextBlock.LetterSpacingProperty));
            Add("TextLength", block.GetValue(TextBlock.TextProperty)?.Length ?? 0);
            // The public layout getter can invoke protected virtual layout creation in a subclass.
            AddLayout(block.GetType() == typeof(TextBlock) && block.IsMeasureValid ? () => block.TextLayout : null,
                "TextBlock.TextLayout");
            return new("TextBlock", fields);
        }

        if (target is TextBox box)
        {
            AddFont(box);
            Add("Wrapping", box.GetValue(TextBox.TextWrappingProperty).ToString());
            Add("Alignment", box.GetValue(TextBox.TextAlignmentProperty).ToString());
            AddNumber("LineHeightDip", box.GetValue(TextBox.LineHeightProperty));
            var password = box.GetValue(TextBox.PasswordCharProperty) != '\0';
            Add("PasswordProtected", password ? "true" : "false");
            if (password)
            {
                Unknown("TextLength", "redacted_password");
                Unknown("Layout", "redacted_password");
            }
            else
            {
                Add("TextLength", box.GetValue(TextBox.TextProperty)?.Length ?? 0);
                var presenter = box.GetVisualDescendants().Take(2048).OfType<TextPresenter>()
                    .FirstOrDefault(item => item.GetType() == typeof(TextPresenter) &&
                                            ReferenceEquals(item.TemplatedParent, box) &&
                                            item.Name == "PART_TextPresenter");
                AddLayout(presenter is { IsMeasureValid: true } ? () => presenter.TextLayout : null,
                    "PART_TextPresenter.TextLayout");
            }

            return new("TextBox", fields);
        }

        if (target is Image image)
        {
            var source = image.GetValue(Image.SourceProperty);
            Add("SourceType", source?.GetType().FullName ?? "none");
            Add("SourceObjectId",
                source is null || observe is null ? "unknown" : observe(source).ToString(CultureInfo.InvariantCulture));
            Add("Stretch", image.GetValue(Image.StretchProperty).ToString());
            Add("StretchDirection", image.GetValue(Image.StretchDirectionProperty).ToString());
            // Bitmap metadata is framework code. IImage.Size on an arbitrary implementation is not safe.
            if (source is Bitmap bitmap && (source.GetType() == typeof(Bitmap) ||
                                            source.GetType() == typeof(WriteableBitmap) ||
                                            source.GetType() == typeof(RenderTargetBitmap)))
            {
                try
                {
                    var pixels = bitmap.PixelSize;
                    var dpi = bitmap.Dpi;
                    var size = bitmap.Size;
                    Add("PixelSize", $"{pixels.Width} × {pixels.Height} px");
                    Add("Dpi", Format($"{dpi.X:0.###} × {dpi.Y:0.###} dpi"));
                    Add("SourceSizeDip", Format($"{size.Width:0.###} × {size.Height:0.###} DIP"));
                    Add("Metadata", "decoded_bitmap");
                }
                catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
                {
                    Unknown("PixelSize", "bitmap_unavailable");
                    Unknown("Dpi", "bitmap_unavailable");
                    Unknown("SourceSizeDip", "bitmap_unavailable");
                    Unknown("Metadata", "bitmap_unavailable");
                }
            }
            else
            {
                Unknown("PixelSize", source is null ? "no_source" : "unsupported_source_type");
                Unknown("Dpi", source is null ? "no_source" : "unsupported_source_type");
                Unknown("SourceSizeDip", source is null ? "no_source" : "unsupported_source_type");
            }

            Unknown("SourceUri", "not_exposed_by_image_api");
            return new("Image", fields);
        }

        return null;

        void Add(string name, object value)
        {
            var text = value switch
            {
                string known => known,
                int number => number.ToString(CultureInfo.InvariantCulture),
                _ => "unknown"
            };
            fields.Add(new(name, Bound(text), text.Length > 120 ? "truncated" : "known"));
        }

        void Unknown(string name, string status) => fields.Add(new(name, "unknown", status));

        void AddNumber(string name, double number)
        {
            if (double.IsFinite(number))
                Add(name, number.ToString("0.###", CultureInfo.InvariantCulture));
            else
                Unknown(name, "automatic_or_unavailable");
        }

        void AddFont(Control control)
        {
            Add("FontFamily", control.GetValue(TextElement.FontFamilyProperty).Name);
            AddNumber("FontSizeDip", control.GetValue(TextElement.FontSizeProperty));
            Add("FontWeight", control.GetValue(TextElement.FontWeightProperty).ToString());
            Add("FontStyle", control.GetValue(TextElement.FontStyleProperty).ToString());
            Add("FontStretch", control.GetValue(TextElement.FontStretchProperty).ToString());
            Add("FlowDirection", control.GetValue(Visual.FlowDirectionProperty).ToString());
        }

        void AddLayout(Func<TextLayout>? read, string source)
        {
            if (read is null)
            {
                Unknown("Layout", "unmeasured_or_custom_layout");
                return;
            }

            try
            {
                var layout = read();
                Add("LayoutSource", source);
                Add("LineCount", layout.TextLines.Count);
                AddNumber("LayoutWidthDip", layout.Width);
                AddNumber("LayoutHeightDip", layout.Height);
            }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
            {
                Unknown("Layout", "layout_unavailable");
            }
        }
    }

    internal static string Summary(OverlayControlInfo? info)
    {
        if (info is null)
            return "";
        var selected = info.Kind == "Image"
            ? new[] { "PixelSize", "Dpi", "SourceSizeDip", "Stretch" }
            : new[]
            {
                "FontSizeDip", "Wrapping", "LineCount", "LayoutWidthDip", "LayoutHeightDip", "PasswordProtected",
                "Layout"
            };
        return "\n" + string.Join("\n",
            info.Fields.Where(field => selected.Contains(field.Name))
                .Select(field => $"{field.Name}={field.Value}"));
    }

    internal static string Bound(string text) =>
        new(text.Take(120).Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Trimming(TextTrimming value) => ReferenceEquals(value, TextTrimming.None) ? "None" :
        ReferenceEquals(value, TextTrimming.CharacterEllipsis) ? "CharacterEllipsis" :
        ReferenceEquals(value, TextTrimming.WordEllipsis) ? "WordEllipsis" : "unknown_custom_trimming";
}
