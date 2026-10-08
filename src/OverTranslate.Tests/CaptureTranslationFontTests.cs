using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class CaptureTranslationFontTests
{
    [Fact]
    public void EmptySelectionKeepsTheShippedFontStack()
    {
        Assert.Equal(
            CaptureTranslationFont.DefaultFamilySource,
            CaptureTranslationFont.SourceFor(""));
    }

    [Fact]
    public void SelectedFontComesFirstAndRetainsGlyphFallbacks()
    {
        Assert.Equal(
            "Microsoft YaHei UI, Microsoft JhengHei, Segoe UI, Sans-Serif",
            CaptureTranslationFont.SourceFor(" Microsoft YaHei UI "));
    }

    [Theory]
    [InlineData("Microsoft JhengHei", "Microsoft JhengHei, Segoe UI, Sans-Serif")]
    [InlineData("Segoe UI", "Segoe UI, Microsoft JhengHei, Sans-Serif")]
    public void SelectingAnExistingFallbackDoesNotDuplicateIt(string selected, string expected)
    {
        Assert.Equal(expected, CaptureTranslationFont.SourceFor(selected));
    }

    [Fact]
    public void CustomFontRetainsTheTargetsNativeGlyphFallback()
    {
        Assert.Equal("Microsoft YaHei, Segoe UI, Sans-Serif", CaptureTranslationFont.SourceFor("", "ZH-HANS"));
        Assert.Equal("Consolas, Yu Gothic UI, Segoe UI, Sans-Serif", CaptureTranslationFont.SourceFor("Consolas", "JA"));
    }
}
