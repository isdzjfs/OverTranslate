using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Providers;
using OverTranslate.Views.Overlay;

namespace LayoutProbe;

/// <summary>Replays a saved Youdao response through the real overlay without opening a window.</summary>
internal static class YoudaoFontLayout
{
    public static void Report(string responsePath, string imagePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var bitmap = new System.Drawing.Bitmap(imagePath);
        using var json = JsonDocument.Parse(File.ReadAllText(responsePath));
        var result = YoudaoScreenshotProvider.ParseResponse(json.RootElement, bitmap.Width, bitmap.Height);
        result = YoudaoScreenshotProvider.MatchSourceFont(bitmap, result);
        var blocks = OverlayPlacement.Place(result.Blocks.Select(block => block with
        {
            BackgroundColor = Colors.Black,
            TextColor = Colors.White,
        }).ToList(), CaptureLayoutMode.General, false);
        var window = new OverlayWindow(blocks, result.SourceBlocks,
            0, 0, bitmap.Width, bitmap.Height, "AUTO", "ZH-HANS", false);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var bounds = (System.Drawing.Rectangle)typeof(OverlayWindow).GetField("_physBounds", fields)!.GetValue(window)!;
        const int margin = 24;
        window.Width = bitmap.Width + margin * 2;
        window.Height = bitmap.Height + margin * 2;
        typeof(OverlayWindow).GetField("_isLoaded", fields)!.SetValue(window, true);
        try
        {
            window.UpdateBlocks(blocks, result.SourceBlocks,
                bounds.Left + margin, bounds.Top + margin,
                bitmap.Width, bitmap.Height, "AUTO", "ZH-HANS", false);
            var root = (Grid)window.Content;
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri(Path.GetFullPath(imagePath));
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();
            root.Children.Insert(0, new Image
            {
                Source = source,
                Width = bitmap.Width, Height = bitmap.Height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(margin),
            });
            root.Background = Brushes.Black;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();

            var canvas = (Canvas)window.FindName("BubbleTextCanvas");
            var report = canvas.Children.Cast<Border>().Select((border, index) =>
            {
                var text = (TextBlock)border.Child;
                var block = blocks[index];
                return new
                {
                    Index = index,
                    Source = block.OriginalText,
                    Translation = block.TranslatedText,
                    SourceLines = block.SourceLineBounds?.Count ?? 0,
                    block.RenderGlyphHeight,
                    block.FontGlyphHeight,
                    FontSize = text.FontSize,
                    BorderWidth = border.Width,
                    BorderHeight = border.Height,
                    Wrapped = text.TextWrapping == TextWrapping.Wrap,
                };
            }).ToArray();
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(outputDirectory, "layout.json"), JsonSerializer.Serialize(report, options));
            foreach (var item in report)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{item.Index}] lines={item.SourceLines} glyph={item.RenderGlyphHeight:0.##} fontGlyph={item.FontGlyphHeight:0.##} font={item.FontSize:0.##} wrapped={item.Wrapped}"));

            var rendered = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var stream = File.Create(Path.Combine(outputDirectory, "overlay.png"));
            encoder.Save(stream);
        }
        finally { window.Close(); }
    }
}
