using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class GoogleTranslateHtmlProviderTests
{
    [Fact]
    public async Task Uses_Revanced_Google2_request_contract_and_preserves_block_order()
    {
        using var handler = new RecordingHandler("[[\"第一句\",\"第二句 &amp; 更多\"]]");
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);
        var blocks = new List<OcrTextBlock>
        {
            new("One <two> & three", new System.Windows.Rect(1, 2, 3, 4)) { RunsAcross = true },
            new("Second line", new System.Windows.Rect(5, 6, 7, 8))
        };

        var (translated, detected) = await provider.TranslateAsync(blocks, "auto", "ZH-HANT", "");

        Assert.Equal("", detected);
        Assert.Equal(new[] { "第一句", "第二句 & 更多" }, translated.Select(item => item.TranslatedText).ToArray());
        Assert.Equal(blocks[0].Bounds, translated[0].Bounds);
        Assert.True(translated[0].RunsAcross);
        Assert.Equal("https://translate-pa.googleapis.com/v1/translateHtml", handler.Url);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("application/json+protobuf", handler.ContentType);
        Assert.Equal("application/json", handler.Accept);
        Assert.True(handler.HasWebClientKey);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("wt_lib", body.RootElement[1].GetString());
        Assert.Equal("auto", body.RootElement[0][1].GetString());
        Assert.Equal("zh-TW", body.RootElement[0][2].GetString());
        Assert.Equal("One &lt;two&gt; &amp; three", body.RootElement[0][0][0].GetString());
        Assert.Equal("Second line", body.RootElement[0][0][1].GetString());
    }

    [Fact]
    public async Task Explicit_source_is_returned_and_invalid_count_fails()
    {
        using var handler = new RecordingHandler("[[\"only one\"]]");
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);
        var blocks = new List<OcrTextBlock>
        {
            new("First", new System.Windows.Rect()),
            new("Second", new System.Windows.Rect())
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranslateAsync(blocks, "EN", "ZH-HANS", ""));
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("en", body.RootElement[0][1].GetString());
        Assert.Equal("zh-CN", body.RootElement[0][2].GetString());
    }

    [Fact]
    public async Task Propagates_http_failure_without_using_another_provider()
    {
        using var handler = new RecordingHandler("", HttpStatusCode.TooManyRequests);
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.TranslateAsync(
            [new OcrTextBlock("Hello", new System.Windows.Rect())], "EN", "ZH-HANS", ""));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Batches_many_blocks_without_losing_their_order()
    {
        using var handler = new EchoHandler();
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);
        var blocks = Enumerable.Range(0, 120)
            .Select(index => new OcrTextBlock($"Line {index}", new System.Windows.Rect()))
            .ToList();

        var (translated, detected) = await provider.TranslateAsync(blocks, "EN", "ZH-HANS", "");

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal("EN", detected);
        Assert.Equal(blocks.Select(block => $"译: {block.Text}"),
            translated.Select(block => block.TranslatedText));
    }

    private sealed class EchoHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var texts = body.RootElement[0][0].EnumerateArray()
                .Select(value => $"译: {value.GetString()}")
                .ToArray();
            var response = JsonSerializer.Serialize(new object[] { texts });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Method { get; private set; }
        public string? ContentType { get; private set; }
        public string? Accept { get; private set; }
        public bool HasWebClientKey { get; private set; }
        public string? Body { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Url = request.RequestUri?.ToString();
            Method = request.Method.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Accept = request.Headers.Accept.Single().MediaType;
            HasWebClientKey = request.Headers.TryGetValues("X-Goog-API-Key", out var keys) &&
                keys.Single().StartsWith("AIza", StringComparison.Ordinal);
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
