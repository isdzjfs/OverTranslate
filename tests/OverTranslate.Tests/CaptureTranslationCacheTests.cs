using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class CaptureTranslationCacheTests
{
    private static readonly Func<List<OcrTextBlock>, Task<List<TranslatedBlock>>> Translate = blocks =>
        Task.FromResult(blocks.Select(block => new TranslatedBlock(
            block.Text, "translated:" + block.Text, block.Bounds, block.Lines, block.RenderGlyphHeight)
        {
            RunsAcross = block.RunsAcross
        }).ToList());

    [Fact]
    public async Task RepeatedTextSkipsTheProviderButUsesTheNewCaptureGeometry()
    {
        var cache = new CaptureTranslationCache();
        var calls = 0;
        async Task<List<TranslatedBlock>> Counted(List<OcrTextBlock> blocks)
        {
            calls++;
            return await Translate(blocks);
        }

        var first = await cache.TranslateAsync(
            [new OcrTextBlock("hello", new Rect(1, 2, 30, 10))],
            TranslationProvider.Google, "EN", "ZH-HANS", Counted);
        var secondBounds = new Rect(50, 60, 100, 20);
        var second = await cache.TranslateAsync(
            [new OcrTextBlock("hello", secondBounds) { RunsAcross = true }],
            TranslationProvider.Google, "EN", "ZH-HANS", Counted);

        Assert.Equal(1, calls);
        Assert.Equal((0, 1), (first.Hits, first.Requests));
        Assert.Equal((1, 0), (second.Hits, second.Requests));
        Assert.Equal("translated:hello", Assert.Single(second.Blocks).TranslatedText);
        Assert.Equal(secondBounds, second.Blocks[0].Bounds);
        Assert.True(second.Blocks[0].RunsAcross);
    }

    [Fact]
    public async Task DuplicateBlocksInOneCaptureIssueOneRequest()
    {
        var cache = new CaptureTranslationCache();
        var sent = new List<string>();
        async Task<List<TranslatedBlock>> Counted(List<OcrTextBlock> blocks)
        {
            sent.AddRange(blocks.Select(block => block.Text));
            return await Translate(blocks);
        }

        var result = await cache.TranslateAsync(
            [
                new OcrTextBlock("same", new Rect(0, 0, 10, 10)),
                new OcrTextBlock("same", new Rect(0, 20, 10, 10)),
                new OcrTextBlock("other", new Rect(0, 40, 10, 10))
            ],
            TranslationProvider.Google, "EN", "ZH-HANS", Counted);

        Assert.Equal(["same", "other"], sent);
        Assert.Equal(2, result.Requests);
        Assert.Equal(["translated:same", "translated:same", "translated:other"],
            result.Blocks.Select(block => block.TranslatedText));
        Assert.Equal(new Rect(0, 20, 10, 10), result.Blocks[1].Bounds);
    }

    [Fact]
    public async Task ProviderAndLanguagePairArePartOfTheKey()
    {
        var cache = new CaptureTranslationCache();
        var input = new List<OcrTextBlock> { new("hello", new Rect(0, 0, 10, 10)) };
        var calls = 0;
        async Task<List<TranslatedBlock>> Counted(List<OcrTextBlock> blocks)
        {
            calls++;
            return await Translate(blocks);
        }

        await cache.TranslateAsync(input, TranslationProvider.Google, "EN", "ZH-HANS", Counted);
        await cache.TranslateAsync(input, TranslationProvider.Google, "JA", "ZH-HANS", Counted);
        await cache.TranslateAsync(input, TranslationProvider.Google, "EN", "ZH-HANT", Counted);
        await cache.TranslateAsync(input, TranslationProvider.Google2, "EN", "ZH-HANS", Counted);

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task FailureIsNotCached()
    {
        var cache = new CaptureTranslationCache();
        var input = new List<OcrTextBlock> { new("hello", new Rect(0, 0, 10, 10)) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.TranslateAsync(
            input, TranslationProvider.Google, "EN", "ZH-HANS",
            _ => Task.FromException<List<TranslatedBlock>>(new InvalidOperationException("failed"))));

        var retried = await cache.TranslateAsync(
            input, TranslationProvider.Google, "EN", "ZH-HANS", Translate);
        Assert.Equal(1, retried.Requests);
        Assert.Equal("translated:hello", Assert.Single(retried.Blocks).TranslatedText);
    }
}
