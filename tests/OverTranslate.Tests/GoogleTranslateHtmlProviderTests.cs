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

    [Fact]
    public async Task Auto_source_retries_a_mixed_sentence_when_google_leaves_its_long_latin_clause_untranslated()
    {
        const string source = "The suggestion chips of the player's Gemini bottom sheet (总结视频 / 推荐相关内容 / server &#x20;";
        using var handler = new MixedLanguageHandler();
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);

        var (translated, _) = await provider.TranslateAsync(
            [new OcrTextBlock(source, new System.Windows.Rect())], "AUTO", "ZH-HANS", "");

        Assert.Equal("播放器的 Gemini 底部面板的建议选项（总结视频 / 推荐相关内容 / 服务器 &#x20;",
            translated.Single().TranslatedText);
        Assert.Equal(new[] { "auto", "auto", "en" }, handler.Sources);
        Assert.Equal(source.Replace("&", "&amp;").Replace("'", "&apos;"), handler.Texts[0]);
        Assert.Equal("The suggestion chips of the player's Gemini bottom sheet".Replace("'", "&apos;"),
            handler.Texts[1]);
        Assert.Equal(handler.Texts[0], handler.Texts[2]);
    }

    [Fact]
    public async Task Auto_source_does_not_retry_chinese_prose_with_a_short_english_term()
    {
        using var handler = new RecordingHandler("[[\"这个 API 要怎么用\"],[\"zh-CN\"]]");
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);

        var (translated, _) = await provider.TranslateAsync(
            [new OcrTextBlock("这个 API 要怎么用", new System.Windows.Rect())], "AUTO", "ZH-HANS", "");

        Assert.Equal("这个 API 要怎么用", translated.Single().TranslatedText);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Auto_source_uses_the_probe_detection_for_other_latin_languages()
    {
        using var handler = new MixedLanguageHandler("fr");
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);

        await provider.TranslateAsync(
            [new OcrTextBlock("Les suggestions de la fiche Gemini du lecteur (总结视频 / 推荐相关内容)",
                new System.Windows.Rect())], "AUTO", "ZH-HANT", "");

        Assert.Equal(new[] { "auto", "auto", "fr" }, handler.Sources);
    }

    [Fact]
    public async Task Failed_optional_retry_keeps_the_successful_first_response()
    {
        const string source = "The suggestion chips of the player's Gemini bottom sheet (总结视频 / 推荐相关内容)";
        using var handler = new MixedLanguageHandler(failRetry: true);
        using var http = new HttpClient(handler);
        var provider = new GoogleTranslateHtmlProvider(http);

        var (translated, _) = await provider.TranslateAsync(
            [new OcrTextBlock(source, new System.Windows.Rect())], "AUTO", "ZH-HANS", "");

        Assert.Equal(source, translated.Single().TranslatedText);
        Assert.Equal(new[] { "auto", "auto", "en" }, handler.Sources);
    }

    private sealed class MixedLanguageHandler(string probeLanguage = "en", bool failRetry = false)
        : HttpMessageHandler
    {
        public List<string> Sources { get; } = [];
        public List<string> Texts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var source = body.RootElement[0][1].GetString()!;
            var text = body.RootElement[0][0][0].GetString()!;
            Sources.Add(source);
            Texts.Add(text);

            if (failRetry && source == probeLanguage)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);

            var response = source == probeLanguage
                ? new object[] { new[] { "播放器的 Gemini 底部面板的建议选项（总结视频 / 推荐相关内容 / 服务器 &amp;#x20;" } }
                : text.Contains("总结视频", StringComparison.Ordinal)
                    ? new object[] { new[] { text }, new[] { "zh-CN" } }
                    : new object[] { new[] { "播放器的建议选项" }, new[] { probeLanguage } };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            };
        }
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
