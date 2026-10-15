using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using Xunit;

namespace OverTranslate.Tests;

public class YoudaoScreenshotProviderTests
{
    // The desktop response uses context for source text and [height, width] for image_size.
    private const string Response = """
        {"errorCode":"0","image_size":[100,400],"lanFrom":"en","resRegions":[
          {"context":"Hello world","tranContent":"你好，世界","boundingBox":"10,20,120,16",
           "transBoundingBox":"200,50,190,40","dir":"h","lineheight":18,
           "lines":[{"text":"Hello world","boundingBox":"10,20,120,16","textHeight":14}]},
          {"context":"Second line","tranContent":"第二行","boundingBox":"10,60,100,16"}
        ]}
        """;

    [Fact]
    public void Signature_matches_the_independent_Python_client_vector()
    {
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        Assert.Equal("9b02269057cb44a209d184ee3c8186fd",
            YoudaoScreenshotProvider.Sign(bytes, "1792075506267"));
    }

    [Theory]
    [InlineData("AUTO", "auto")]
    [InlineData("EN-US", "en")]
    [InlineData("ZH", "zh-CHS")]
    [InlineData("ZH-HANS", "zh-CHS")]
    [InlineData("ZH-HANT", "zh-CHT")]
    [InlineData("JA", "ja")]
    [InlineData("KO", "ko")]
    public void Maps_application_language_codes(string code, string expected) =>
        Assert.Equal(expected, YoudaoScreenshotProvider.ToLanguage(code));

    [Fact]
    public async Task Service_uploads_the_signed_JPEG_once_and_returns_source_geometry()
    {
        using var bitmap = new Bitmap(400, 100);
        using var handler = new RecordingHandler(async (request, cancellation) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("ocrtran.youdao.com", request.RequestUri!.Host);
            Assert.Equal("/ocr/imgtranocr", request.RequestUri.AbsolutePath);
            var query = Query(request.RequestUri);
            Assert.Equal("auto", query["from"]);
            Assert.Equal("zh-CHT", query["to"]);
            Assert.Equal("false", query["isRender"]);
            Assert.Equal("test-install-id", query["id"]);
            Assert.Equal(query["id"], query["condition_id"]);
            Assert.Equal("deskdict", query["clientele"]);
            Assert.Equal("DESKDICT_VENDOR=cidian_youdao_com", Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Contains("Youdao Desktop Dict", request.Headers.UserAgent.ToString());
            var body = Assert.IsType<MultipartFormDataContent>(request.Content);
            var part = Assert.Single(body);
            Assert.Equal("multipartFile", part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("screenTrans", part.Headers.ContentDisposition.FileName!.Trim('"'));
            Assert.Equal("multipart/form-data", part.Headers.ContentType!.MediaType);
            var jpeg = await part.ReadAsByteArrayAsync(cancellation);
            Assert.Equal(new byte[] { 0xff, 0xd8 }, jpeg[..2]);
            using var stream = new MemoryStream(jpeg);
            using var decoded = new Bitmap(stream);
            Assert.Equal(bitmap.Size, decoded.Size);
            Assert.Equal(YoudaoScreenshotProvider.Sign(jpeg, query["salt"]), query["sign"]);
            return Json(Response);
        });
        using var http = new HttpClient(handler);
        var service = new TranslationService(http);
        Assert.False(service.ProviderRequiresApiKey(TranslationProvider.Youdao));

        var result = await service.TranslateYoudaoScreenshotAsync(bitmap, "AUTO", "ZH-HANT", "test-install-id");

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("EN", result.DetectedLang);
        Assert.Equal(new[] { "Hello world", "Second line" }, result.SourceBlocks.Select(block => block.Text));
        Assert.Equal(new[] { "你好，世界", "第二行" }, result.Blocks.Select(block => block.TranslatedText));
        Assert.Equal(new Rect(10, 20, 120, 16), result.Blocks[0].Bounds);
        Assert.Equal(result.SourceBlocks[0].Bounds, result.Blocks[0].Bounds);
        Assert.Equal(16 * 0.82, result.Blocks[0].RenderGlyphHeight!.Value, precision: 6);
        Assert.Equal(new[] { new Rect(10, 20, 120, 16) }, result.Blocks[0].SourceLineBounds);
    }

    [Fact]
    public void Scales_height_width_metadata_to_the_uploaded_crop()
    {
        var result = Parse(Response, 800, 300);
        Assert.Equal(new Rect(20, 60, 240, 48), result.Blocks[0].Bounds);
        Assert.Equal(48 * 0.82, result.Blocks[0].RenderGlyphHeight!.Value, precision: 6);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("srcContent")]
    public void Accepts_script_source_field_aliases(string key)
    {
        var result = Parse($$"""{"errorCode":0,"resRegions":[{"{{key}}":"Hello","tranContent":"你好","boundingBox":"1,2,30,10"}]}""");
        Assert.Equal("Hello", Assert.Single(result.SourceBlocks).Text);
    }

    [Fact]
    public void Can_recover_source_text_and_bounds_from_lines()
    {
        var result = Parse("""
            {"errorCode":0,"resRegions":[{"tranContent":"两行","lines":[
              {"text":"First","boundingBox":"1,2,30,10"},
              {"text":"Second","boundingBox":"1,22,40,10"}]}]}
            """);
        var block = Assert.Single(result.Blocks);
        Assert.Equal("First\nSecond", block.OriginalText);
        Assert.Equal(new Rect(1, 2, 40, 30), block.Bounds);
        Assert.Equal(2, block.SourceLineBounds!.Count);
    }

    // isRender=false responses put OCR lines beside resRegions. The middle paragraph used to
    // shrink to 11px because its two source lines were discarded and it was fitted as one line.
    [Fact]
    public void Top_level_lines_belong_to_their_own_paragraph()
    {
        var result = Parse(ParagraphResponse, 500, 160);
        Assert.Equal(new[] { 1, 2, 2 }, result.Blocks.Select(block => block.SourceLineBounds!.Count));
        Assert.Equal(new Rect(10, 40, 400, 24), result.Blocks[1].SourceLineBounds![0]);
        Assert.Equal(new Rect(10, 66, 270, 24), result.Blocks[1].SourceLineBounds![1]);
        var placed = OverTranslate.Layout.OverlayPlacement.Place(result.Blocks,
            OverTranslate.Services.Ocr.CaptureLayoutMode.General, false);
        Assert.Equal(OverTranslate.Layout.OverlayLayoutIntent.GroupReflow, placed[1].LayoutIntent);
        Assert.Equal(OverTranslate.Layout.OverlayLayoutIntent.GroupReflow, placed[2].LayoutIntent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_pixels_keep_paragraph_fonts_consistent_despite_server_font_choices(bool darkText)
    {
        using var image = new Bitmap(500, 160);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(darkText ? Color.White : Color.Black);
            // Known 11px glyph bodies surrounded by the padded OCR boxes in ParagraphResponse.
            // Separate stems leave enough background to measure either light or dark lettering.
            var brush = darkText ? Brushes.Black : Brushes.White;
            foreach (var top in new[] { 4, 40, 66, 103, 127 })
                for (var x = 20; x < 250; x += 8)
                    graphics.FillRectangle(brush, x, top + 5, 3, 11);
        }
        var result = YoudaoScreenshotProvider.MatchSourceFont(image, Parse(ParagraphResponse, 500, 160));
        Assert.All(result.Blocks, block =>
        {
            Assert.Equal(11, block.RenderGlyphHeight);
            Assert.Equal(11, block.FontGlyphHeight);
        });
        Assert.All(result.SourceBlocks, block => Assert.Equal(11, block.FontGlyphHeight));
    }

    [Fact]
    public void A_line_count_is_used_when_individual_source_boxes_are_missing()
    {
        var result = Parse("""
            {"errorCode":0,"resRegions":[{"context":"A whole paragraph","tranContent":"完整的段落",
            "boundingBox":"1,2,300,40","linesCount":2,"lineheight":120}]}
            """);
        var block = Assert.Single(result.Blocks);
        Assert.Equal(2, block.SourceLineBounds!.Count);
        Assert.Equal(20 * 0.82, block.RenderGlyphHeight!.Value, precision: 6);
    }

    [Fact]
    public void Page_lines_are_not_attached_to_two_overlapping_regions()
    {
        var result = Parse("""
            {"errorCode":0,"resRegions":[
              {"context":"Upper","tranContent":"上方","boundingBox":"0,0,300,25"},
              {"context":"Lower","tranContent":"下方","boundingBox":"0,20,300,50"}],
            "lines":[{"text":"Lower","boundingBox":"0,21,300,20"}]}
            """);
        Assert.Equal(new Rect(0, 0, 300, 25), result.Blocks[0].SourceLineBounds![0]);
        Assert.Equal(new Rect(0, 21, 300, 20), result.Blocks[1].SourceLineBounds![0]);
    }

    private const string ParagraphResponse = """
        {"errorCode":0,"image_size":[160,500],"resRegions":[
          {"context":"First paragraph","tranContent":"第一段文字","boundingBox":"10,4,400,24","lineheight":22,"linesCount":1},
          {"context":"A longer paragraph with two original lines","tranContent":"第二段较长的译文，应该保持原来的文字大小并正常换行。","boundingBox":"10,40,400,50","lineheight":8,"linesCount":2},
          {"context":"Another paragraph","tranContent":"第三段文字","boundingBox":"10,103,400,48","lineheight":90,"linesCount":2}],
        "lines":[
          {"text":"First paragraph","boundingBox":"10,4,400,24","textHeight":38},
          {"text":"A longer paragraph","boundingBox":"10,40,400,24","textHeight":8},
          {"text":"with two original lines","boundingBox":"10,66,270,24","textHeight":32},
          {"text":"Another","boundingBox":"10,103,400,24","textHeight":50},
          {"text":"paragraph","boundingBox":"10,127,270,24","textHeight":12}]}
        """;

    [Fact]
    public void Keeps_horizontal_labels_distinct_on_a_vertical_capture()
    {
        using var json = JsonDocument.Parse(Response);
        var result = YoudaoScreenshotProvider.ParseResponse(json.RootElement, 400, 100, verticalText: true);
        Assert.True(result.Blocks[0].RunsAcross);
        Assert.True(result.SourceBlocks[0].RunsAcross);
        Assert.False(result.Blocks[1].RunsAcross);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Missing_translation_is_not_silently_counted_as_success(bool skip, bool untranslated)
    {
        var result = Parse($$"""{"errorCode":0,"resRegions":[{"context":"Hello","skipTranslate":{{skip.ToString().ToLowerInvariant()}},"boundingBox":"1,2,30,10"}]}""");
        var block = Assert.Single(result.Blocks);
        Assert.Equal("Hello", block.TranslatedText);
        Assert.Equal(untranslated, block.Untranslated);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"errorCode\":0}")]
    [InlineData("{\"errorCode\":0,\"resRegions\":null}")]
    [InlineData("{\"errorCode\":0,\"resRegions\":[],\"image_size\":[\"x\",100]}")]
    [InlineData("{\"errorCode\":0,\"resRegions\":[{\"context\":\"Hello\",\"boundingBox\":\"1,2,-3,10\"}]}")]
    [InlineData("{\"errorCode\":0,\"resRegions\":[{\"context\":\"Hello\",\"boundingBox\":\"NaN,2,3,10\"}]}")]
    [InlineData("{\"errorCode\":0,\"resRegions\":[{\"context\":\"Hello\",\"boundingBox\":\"900,200,3,10\"}]}")]
    public void Rejects_invalid_responses_instead_of_claiming_there_was_no_text(string response) =>
        Assert.Throws<TranslationEngineException>(() => Parse(response));

    [Fact]
    public void Empty_recognition_is_a_valid_empty_result() =>
        Assert.Empty(Parse("{\"errorCode\":0,\"resRegions\":[]}").Blocks);

    [Fact]
    public void Clips_source_geometry_to_the_crop()
    {
        var result = Parse("""{"errorCode":0,"resRegions":[{"context":"Hello","tranContent":"你好","boundingBox":"-10,-5,50,20"}]}""");
        Assert.Equal(new Rect(0, 0, 40, 15), Assert.Single(result.Blocks).Bounds);
    }

    [Theory]
    [InlineData("{\"errorCode\":\"206\",\"message\":\"private screen content\"}", HttpStatusCode.OK)]
    [InlineData("private screen content", HttpStatusCode.TooManyRequests)]
    [InlineData("not json: private screen content", HttpStatusCode.OK)]
    public async Task Failures_never_retry_or_expose_the_response_text(string response, HttpStatusCode status)
    {
        using var bitmap = new Bitmap(400, 100);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(Json(response, status)));
        using var http = new HttpClient(handler);
        var service = new TranslationService(http);
        var error = await Assert.ThrowsAsync<TranslationEngineException>(() =>
            service.TranslateYoudaoScreenshotAsync(bitmap, "EN", "ZH-HANS", "test-install-id"));
        Assert.DoesNotContain("private screen content", error.ToString());
        Assert.Equal(1, handler.RequestCount);
        if (status != HttpStatusCode.OK) Assert.Equal(status, error.StatusCode);
        else if (response.StartsWith('{')) Assert.Contains("206", error.Message);
    }

    [Fact]
    public async Task Cancelling_in_flight_aborts_the_only_request()
    {
        using var bitmap = new Bitmap(400, 100);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler(async (_, cancellation) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return Json(Response);
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = new TranslationService(http).TranslateYoudaoScreenshotAsync(
            bitmap, "EN", "ZH-HANS", "test-install-id", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Request_timeout_is_reported_as_failure()
    {
        using var bitmap = new Bitmap(400, 100);
        using var handler = new RecordingHandler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return Json(Response);
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var error = await Assert.ThrowsAsync<TranslationEngineException>(() =>
            new TranslationService(http).TranslateYoudaoScreenshotAsync(bitmap, "EN", "ZH-HANS", "test-install-id"));
        Assert.Contains("timed out", error.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void Screenshot_choice_round_trips_without_changing_the_shared_text_provider()
    {
        var settings = SettingsService.Parse("""{"Provider":"DeepL","Capture":{"ImageProvider":"Youdao","YoudaoDeviceId":"saved-install-id"}}""");
        Assert.Equal(TranslationProvider.Youdao, settings.Capture.GetProvider(settings.Provider));
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
        Assert.Equal("saved-install-id", settings.Capture.YoudaoDeviceId);
        var restored = SettingsService.Parse(JsonSerializer.Serialize(settings));
        Assert.Equal(TranslationProvider.Youdao, restored.Capture.GetProvider(restored.Provider));
        Assert.Equal(settings.Capture.YoudaoDeviceId, restored.Capture.YoudaoDeviceId);
        Assert.Equal(TranslationProvider.Microsoft, SettingsService.Parse("{}").Capture.GetProvider(TranslationProvider.Microsoft));
        Assert.Equal(TranslationProvider.Google, SettingsService.Parse("""{"Capture":{"ImageProvider":"future-engine"}}""").Capture.GetProvider(TranslationProvider.Google));
        Assert.Contains(LanguageData.CaptureProviders, provider => provider.Provider == TranslationProvider.Youdao);
        Assert.DoesNotContain(LanguageData.Providers, provider => provider.Provider == TranslationProvider.Youdao);
    }

    private static ScreenshotTranslationResult Parse(string text, int width = 400, int height = 100)
    {
        using var json = JsonDocument.Parse(text);
        return YoudaoScreenshotProvider.ParseResponse(json.RootElement, width, height);
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return respond(request, cancellationToken);
        }
    }
}

public sealed class YoudaoLiveTheoryAttribute : TheoryAttribute
{
    public YoudaoLiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OVERTRANSLATE_TEST_YOUDAO") != "1")
            Skip = "Set OVERTRANSLATE_TEST_YOUDAO=1 to send a generated test image to Youdao.";
    }
}

public class YoudaoScreenshotLiveTests
{
    [YoudaoLiveTheory]
    [InlineData("EN", "ZH-HANS")]
    [InlineData("AUTO", "ZH-HANT")]
    public async Task Generated_English_image_returns_Chinese_translation_and_source_boxes(string source, string target)
    {
        using var bitmap = new Bitmap(900, 200);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 22))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("Hello world! Welcome to screenshot translation.", font, Brushes.Black, 20, 25);
            graphics.DrawString("The quick brown fox jumps over the lazy dog.", font, Brushes.Black, 20, 85);
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var result = await new TranslationService(http).TranslateYoudaoScreenshotAsync(
            bitmap, source, target, "overtranslate-test");
        Assert.NotEmpty(result.Blocks);
        Assert.Equal(result.SourceBlocks.Count, result.Blocks.Count);
        Assert.Contains(result.Blocks, block => block.TranslatedText.Any(character => character is >= '\u4e00' and <= '\u9fff'));
        Assert.All(result.Blocks, block =>
        {
            Assert.False(block.Untranslated);
            Assert.False(string.IsNullOrWhiteSpace(block.OriginalText));
            Assert.True(new Rect(0, 0, bitmap.Width, bitmap.Height).Contains(block.Bounds));
        });
    }
}
