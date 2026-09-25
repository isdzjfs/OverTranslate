using System.Drawing;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class CaptureOcrReuseTests
{
    [Fact]
    public void ReusesOnlyTheSameLockedCropAndRecognitionSettings()
    {
        using var first = new Bitmap(2, 2);
        using var second = new Bitmap(2, 2);
        var blocks = new List<OcrTextBlock> { new("hello", new Rect(0, 0, 2, 2)) };
        var reuse = new CaptureOcrReuse();

        reuse.Store(first, "EN", false, CaptureLayoutMode.General, blocks);

        Assert.True(reuse.TryGet(first, "EN", false, CaptureLayoutMode.General, out var reused));
        Assert.Same(blocks, reused);
        Assert.False(reuse.TryGet(second, "EN", false, CaptureLayoutMode.General, out _));
        Assert.False(reuse.TryGet(first, "JA", false, CaptureLayoutMode.General, out _));
        Assert.False(reuse.TryGet(first, "EN", true, CaptureLayoutMode.General, out _));
        Assert.False(reuse.TryGet(first, "EN", false, CaptureLayoutMode.Interface, out _));

        reuse.Clear();
        Assert.False(reuse.TryGet(first, "EN", false, CaptureLayoutMode.General, out _));
    }

    [Fact]
    public void EmptyRecognitionDoesNotBecomeAReusableAnswer()
    {
        using var bitmap = new Bitmap(2, 2);
        var reuse = new CaptureOcrReuse();
        reuse.Store(bitmap, "AUTO", false, CaptureLayoutMode.General, []);

        Assert.False(reuse.TryGet(bitmap, "AUTO", false, CaptureLayoutMode.General, out _));
    }
}
