using System.Drawing;
using System.Drawing.Text;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using RapidOcrNet;
using Xunit;

namespace OverTranslate.Tests;

public class AutomaticRussianOcrTests
{
    [Theory]
    [InlineData("Привет, мир!")]
    [InlineData("Добро пожаловать в Россию")]
    [InlineData("Съешь ещё этих мягких булок")]
    public async Task Automatic_recognizes_Russian_from_pixels(string expected)
    {
        using var image = Draw((expected, "Segoe UI"));
        using var engine = new OcrService();
        var blocks = await engine.RecognizeAsync(image, "AUTO");
        var text = string.Join(" ", blocks.Select(block => block.Text));
        Assert.Contains(Letters(expected), Letters(text));
    }

    [Fact]
    public async Task Automatic_reads_Russian_Chinese_English_and_Japanese_in_one_capture()
    {
        using var image = Draw(
            ("Привет, мир!", "Segoe UI"),
            ("Hello world", "Segoe UI"),
            ("欢迎使用自动翻译", "Microsoft YaHei"),
            ("日本語テスト", "Yu Gothic UI"));
        using var engine = new OcrService();
        var blocks = await engine.RecognizeAsync(image, "AUTO");
        var text = Letters(string.Join(" ", blocks.Select(block => block.Text)));
        Assert.Contains(Letters("Привет, мир"), text);
        Assert.Contains(Letters("Hello world"), text);
        Assert.Contains("欢迎使用自动翻译", text);
        Assert.Contains("日本語テスト", text);
    }

    [Fact]
    public async Task Explicit_Russian_uses_the_new_model()
    {
        using var image = Draw(("Привет, мир!", "Segoe UI"));
        using var engine = new OcrService();
        var blocks = await engine.RecognizeAsync(image, "RU");
        Assert.Contains(Letters("Привет, мир"), Letters(string.Join(" ", blocks.Select(block => block.Text))));
        Assert.Contains(LanguageData.OcrSourceLanguages, language => language.Code == "RU");
    }

    [Fact]
    public async Task Automatic_preserves_English_interface_labels()
    {
        string[] labels = ["Settings", "Preferences", "Hello world", "Cancel", "HOME", "PAUSE", "START", "Network"];
        using var image = Draw(labels.Select(label => (label, "Segoe UI")).ToArray());
        using var engine = new OcrService();
        var blocks = await engine.RecognizeAsync(image, "AUTO");
        var text = Letters(string.Join(" ", blocks.Select(block => block.Text)));
        foreach (var label in labels) Assert.Contains(Letters(label), text);
        Assert.DoesNotContain(text, character => character is >= '\u0400' and <= '\u04ff');
    }

    [Theory]
    [InlineData("Hello world", 0.98f, "Нello world", 0.99f, false)]
    [InlineData("Hello", 0.95f, "Привет", 0.70f, false)]
    [InlineData("Settings", 0.99f, "Ѕettings", 0.99f, false)]
    [InlineData("Hpивет", 0.75f, "Привет", 0.98f, true)]
    [InlineData(", !", 0.99f, "Привет, мир!", 0.97f, true)]
    public void Cyrillic_requires_script_evidence_and_higher_confidence(
        string generalText, float generalScore, string russianText, float russianScore, bool useRussian)
    {
        var general = Line(generalText, generalScore);
        var russian = Line(russianText, russianScore);
        Assert.Same(useRussian ? russian : general, AutomaticOcrRecognition.Choose(general, russian));
    }

    [Fact]
    public void Confident_Chinese_and_Japanese_do_not_need_a_second_read()
    {
        Assert.False(AutomaticOcrRecognition.NeedsCyrillicReading(Line("日本語テスト", 0.98f)));
        Assert.False(AutomaticOcrRecognition.NeedsCyrillicReading(Line("自动翻译", 0.98f)));
        Assert.True(AutomaticOcrRecognition.NeedsCyrillicReading(Line("Hpивет", 0.98f)));
    }

    [Fact]
    public void Cyrillic_accents_survive_existing_text_cleanup() =>
        Assert.Equal("Съешь ещё, йогурт", OnnxOcrEngine.FoldLatinDiacritics("Съешь ещё, йогурт"));

    private static string Letters(string text) => new(text.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
    private static TextLine Line(string text, float score) => new()
    {
        Chars = text.Select(character => character.ToString()).ToArray(),
        CharScores = Enumerable.Repeat(score, text.Length).ToArray(),
    };

    private static Bitmap Draw(params (string Text, string Font)[] lines)
    {
        var bitmap = new Bitmap(1000, lines.Length * 80 + 30);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        for (var i = 0; i < lines.Length; i++)
        {
            using var font = new Font(lines[i].Font, 36, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.DrawString(lines[i].Text, font, Brushes.Black, new PointF(20, 15 + i * 80));
        }
        return bitmap;
    }
}
