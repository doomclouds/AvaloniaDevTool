using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class InspectionIntegrationTests
{
    [AvaloniaFact]
    public void Style_snapshot_reports_style_frames_and_live_binding_description()
    {
        var model = new MessageModel { Text = "Bound value" };
        var text = new TextBlock { Name = "DiagnosticText" };
        text.Classes.Add("diagnostic");
        text.Bind(TextBlock.TextProperty, new Binding(nameof(MessageModel.Text)) { Source = model });
        var window = new Window { Content = text };
        window.Styles.Add(new Style(selector => selector.OfType<TextBlock>().Class("diagnostic"))
        { Setters = { new Setter(TextBlock.FontSizeProperty, 31d) } });
        window.Show();
        try
        {
            Assert.Equal(31, text.FontSize);
            var result = Avalonia12DiagnosticsAdapter.Inspect(text);
            Assert.Contains(result.Frames, frame => frame.IsActive && frame.Setters.Any(setter => setter.PropertyName == "FontSize"));
            Assert.Contains(result.Bindings, binding => binding.PropertyName == "Text" && binding.Expression.Contains("Text", StringComparison.Ordinal));
            Assert.Equal("Bound value", text.Text);
            model.Text = "Still bound";
            Assert.Equal("Still bound", text.Text);
            Assert.Contains("PrivateApi", result.Adapter);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Invalid_binding_exposes_error_state_without_replacing_the_binding()
    {
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, new Binding("MissingProperty") { Source = new object() });
        var window = new Window { Content = text };
        window.Show();
        try
        {
            var result = Avalonia12DiagnosticsAdapter.Inspect(text);
            Assert.Contains(result.Bindings, binding => binding.PropertyName == "Text" &&
                binding.ErrorType == "Error" && binding.Expression.Contains("MissingProperty", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Resource_edit_updates_dynamic_resource_and_dispose_restores_original()
    {
        var application = Application.Current!;
        var key = "InspectionBrush-" + Guid.NewGuid().ToString("N");
        var original = new SolidColorBrush(Colors.Red);
        application.Resources[key] = original;
        var border = new Border { Width = 50, Height = 50 };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable(key));
        var window = new Window { Content = border };
        window.Show();
        try
        {
            var objects = new WeakObjectRegistry();
            using (var service = new ResourceInspectionService(application, objects))
            {
                var dictionary = service.GetTrees(new ResourcesRequest(objects.GetId(border)))
                    .Single(root => root.Name == "Application.Resources");
                Assert.Contains(service.GetEntries(dictionary.Id), entry => entry.Key == key && entry.CanEdit);
                service.Set(new SetResourceRequest(dictionary.Id, key, "#00ff00"));
                Assert.Equal(Colors.Lime, Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
                Assert.True(service.GetEntries(dictionary.Id).Single(entry => entry.Key == key).IsOverridden);
            }
            Assert.Same(original, application.Resources[key]);
            Assert.Same(original, border.Background);
        }
        finally { window.Close(); application.Resources.Remove(key); }
    }

    [AvaloniaFact]
    public void Resource_reset_preserves_a_newer_application_write()
    {
        var application = Application.Current!;
        var key = "InspectionValue-" + Guid.NewGuid().ToString("N");
        application.Resources[key] = 12d;
        try
        {
            using var service = new ResourceInspectionService(application, new WeakObjectRegistry());
            var dictionary = service.GetTrees(new ResourcesRequest()).Single(root => root.Name == "Application.Resources");
            service.Set(new SetResourceRequest(dictionary.Id, key, "24"));
            application.Resources[key] = 48d;
            var error = Assert.Throws<RpcException>(() => service.Reset(new ResetResourceRequest(dictionary.Id, key)));
            Assert.Equal("resource_conflict", error.Code);
            Assert.Equal(48d, application.Resources[key]);
        }
        finally { application.Resources.Remove(key); }
    }

    [AvaloniaFact]
    public void Resource_dictionary_scope_preserves_theme_and_merged_structure()
    {
        var application = Application.Current!;
        var merged = new ResourceDictionary { ["MergedMarker"] = "merged" };
        var dark = new ResourceDictionary { ["DarkMarker"] = "dark" };
        var local = new ResourceDictionary
        {
            MergedDictionaries = { merged },
            ThemeDictionaries = { [ThemeVariant.Dark] = dark }
        };
        var control = new Border { Name = "ResourceOwner", Resources = local };
        var objects = new WeakObjectRegistry();
        using var scoped = new ResourceInspectionService(application, objects);
        var root = scoped.GetTrees(new ResourcesRequest(objects.GetId(control))).Single(node => node.Scope == "Element");
        Assert.Contains(root.Children, child => child.Name.StartsWith("Merged", StringComparison.Ordinal));
        Assert.Contains(root.Children, child => child.Theme == "Dark");
    }

    [AvaloniaFact]
    public void Asset_preview_reads_embedded_text_and_rejects_other_uri_schemes()
    {
        var assets = AssetInspectionService.List();
        var asset = Assert.Single(assets, item => item.Uri.EndsWith("/Assets/inspection-probe.txt", StringComparison.Ordinal));
        var result = AssetInspectionService.Open(asset.Uri);
        Assert.Contains("AvaloniaDevTool asset probe", result.Text);
        Assert.False(result.IsTruncated);
        Assert.Null(result.Base64);
        var error = Assert.Throws<RpcException>(() => AssetInspectionService.Open("file:///C:/Windows/win.ini"));
        Assert.Equal("unsupported_uri", error.Code);
    }

    private sealed class MessageModel : INotifyPropertyChanged
    {
        private string _text = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Text
        {
            get => _text;
            set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
        }
    }
}

