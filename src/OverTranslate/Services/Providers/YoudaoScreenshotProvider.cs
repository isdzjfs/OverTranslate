using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services.Ocr;
using OverTranslate.Translation;

namespace OverTranslate.Services.Providers;

public sealed record ScreenshotTranslationResult(
    List<OcrTextBlock> SourceBlocks, List<TranslatedBlock> Blocks, string DetectedLang);

/// <summary>
/// Youdao's combined screenshot OCR/translation endpoint. The uploaded JPEG is signed, and its
/// source geometry is carried back to the capture overlay without a second OCR or text request.
/// </summary>
public sealed class YoudaoScreenshotProvider(HttpClient http)
{
    private const string Client = "deskdict";
    private const string SignKey = "VPaHE3kX_vl4BhgYiu2n";
    private const string Engine = "Youdao";

    public async Task<ScreenshotTranslationResult> TranslateAsync(
        Bitmap bitmap, string sourceLang, string targetLang, string deviceId,
        CancellationToken cancellationToken = default, bool verticalText = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Encoding may touch every pixel of a large crop. The caller owns bitmap until this task
        // completes, including when closing the capture cancels an in-flight request.
        var jpeg = await Task.Run(() => EncodeJpeg(bitmap), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var salt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var sign = Sign(jpeg, salt);
        var os = Environment.OSVersion.Version.ToString();
        var query = new Dictionary<string, string>
        {
            ["clientele"] = Client, ["salt"] = salt, ["sign"] = sign,
            ["from"] = ToLanguage(sourceLang), ["to"] = ToLanguage(targetLang),
            ["isRender"] = "false", ["funDesc"] = "photo_translate",
            ["client"] = Client, ["id"] = deviceId, ["vendor"] = "unknown",
            ["in"] = "YoudaoDictSetup", ["appVer"] = "11.3.22.0", ["abTest"] = "7",
            ["model"] = "PC", ["screen"] = "0*0", ["OsVersion"] = os,
            ["network"] = "none", ["mid"] = "windows" + os,
            ["condition_id"] = deviceId, ["product"] = Client,
        };
        var url = "https://ocrtran.youdao.com/ocr/imgtranocr?" + string.Join("&",
            query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.UserAgent.ParseAdd("Youdao Desktop Dict (Windows NT 10.0; WOW64)");
        request.Headers.TryAddWithoutValidation("Cookie", "DESKDICT_VENDOR=cidian_youdao_com");
        request.Headers.TryAddWithoutValidation("MIME-Version", "1.0");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.Accept.ParseAdd("*/*");

        using var multipart = new MultipartFormDataContent(Guid.NewGuid().ToString("N")[..16]);
        var image = new ByteArrayContent(jpeg);
        // This part's content type and filename deliberately follow the supplied desktop client.
        image.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        multipart.Add(image, "multipartFile", "screenTrans");
        request.Content = multipart;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new TranslationEngineException(Engine, $"HTTP {(int)response.StatusCode}.", response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = ParseResponse(json.RootElement, bitmap.Width, bitmap.Height, verticalText);
            return MatchSourceFont(bitmap, result, verticalText);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationEngineException(Engine, "Screenshot translation timed out.", inner: ex);
        }
        catch (JsonException)
        {
            // The response may contain the user's screen text. Keep raw JSON out of error logs.
            throw new TranslationEngineException(Engine, "Invalid screenshot translation response.");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationEngineException(Engine, "Screenshot request failed.", ex.StatusCode);
        }
    }

    internal static byte[] EncodeJpeg(Bitmap bitmap)
    {
        // Flatten transparent captures onto white; JPEG has no alpha channel.
        using var rgb = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(rgb))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.DrawImageUnscaled(bitmap, 0, 0);
        }
        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 78L);
        rgb.Save(stream, ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid), parameters);
        return stream.ToArray();
    }

    internal static string Sign(byte[] jpeg, string salt)
    {
        var base64 = Convert.ToBase64String(jpeg);
        var token = base64[..Math.Min(10, base64.Length)]
            + base64.Length.ToString(CultureInfo.InvariantCulture)
            + base64[Math.Max(0, base64.Length - 10)..];
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Client + token + salt + SignKey)))
            .ToLowerInvariant();
    }

    internal static string ToLanguage(string code) => code.ToUpperInvariant() switch
    {
        "AUTO" => "auto",
        "ZH" or "ZH-HANS" or "ZH-CN" or "ZH-CHS" => "zh-CHS",
        "ZH-HANT" or "ZH-TW" or "ZH-CHT" => "zh-CHT",
        _ => EngineLanguage.ToEngine(code),
    };

    internal static ScreenshotTranslationResult ParseResponse(
        JsonElement root, int width, int height, bool verticalText = false)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw InvalidResponse();
        var error = Text(root, "errorCode");
        if (error != "0")
            throw new TranslationEngineException(Engine,
                int.TryParse(error, out var errorCode) ? $"Screenshot errorCode={errorCode}." : "Missing error code.");
        if (!root.TryGetProperty("resRegions", out var regions) || regions.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();

        double scaleX = 1, scaleY = 1;
        if (root.TryGetProperty("image_size", out var size))
        {
            // The endpoint returns [height, width], unlike most imaging APIs.
            if (size.ValueKind != JsonValueKind.Array || size.GetArrayLength() != 2
                || size[0].ValueKind != JsonValueKind.Number || size[1].ValueKind != JsonValueKind.Number
                || !size[0].TryGetInt32(out var responseHeight) || responseHeight <= 0
                || !size[1].TryGetInt32(out var responseWidth) || responseWidth <= 0)
                throw InvalidResponse();
            scaleX = (double)width / responseWidth;
            scaleY = (double)height / responseHeight;
        }

        var canvas = new Rect(0, 0, width, height);
        var regionItems = regions.EnumerateArray().ToArray();
        if (regionItems.Any(region => region.ValueKind != JsonValueKind.Object)) throw InvalidResponse();
        var pageLines = MatchPageLines(root, regionItems, scaleX, scaleY, canvas);
        List<OcrTextBlock> sources = [];
        List<TranslatedBlock> translated = [];
        for (var regionIndex = 0; regionIndex < regionItems.Length; regionIndex++)
        {
            var region = regionItems[regionIndex];
            var source = FirstText(region, "context", "content", "srcContent");
            var lines = region.TryGetProperty("lines", out var sourceLines) && sourceLines.ValueKind == JsonValueKind.Array
                ? sourceLines.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.Object).ToArray() : [];
            // isRender=false commonly returns the source lines at the top level, not inside each
            // region. A paragraph without them looks like one line to the overlay's fitting code.
            if (lines.Length == 0) lines = pageLines[regionIndex].ToArray();
            if (string.IsNullOrWhiteSpace(source))
                source = string.Join("\n", lines.Select(line => FirstText(line, "text", "content"))
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            if (string.IsNullOrWhiteSpace(source)) continue;

            var lineBounds = lines.Select(line => ReadBounds(line, scaleX, scaleY, canvas))
                .Where(bounds => bounds.HasValue).Select(bounds => bounds!.Value).ToList();
            var bounds = ReadBounds(region, scaleX, scaleY, canvas);
            if (bounds is null && lineBounds.Count == 0) throw InvalidResponse();
            var sourceBounds = bounds ?? Rect.Empty;
            foreach (var line in lineBounds) sourceBounds.Union(line);

            var runsAcross = verticalText && Text(region, "dir").Equals("h", StringComparison.OrdinalIgnoreCase);
            var isColumn = verticalText && !runsAcross;
            if (lineBounds.Count == 0)
                lineBounds = SplitSourceLines(region, sourceBounds, isColumn);
            // lineheight is Youdao's rendering choice, not the original glyph height. Derive the
            // fallback from original line boxes, then refine it from screenshot pixels below.
            double? glyphHeight = Median(lineBounds.Select(line => isColumn ? line.Width : line.Height)) * 0.82;
            var sourceBlock = new OcrTextBlock(source, sourceBounds, lineBounds, glyphHeight)
                { RunsAcross = runsAcross };
            var target = Text(region, "tranContent");
            var skipTranslation = region.TryGetProperty("skipTranslate", out var skip) && skip.ValueKind == JsonValueKind.True;
            sources.Add(sourceBlock);
            translated.Add(new TranslatedBlock(source, string.IsNullOrWhiteSpace(target) ? source : target,
                sourceBounds, lineBounds, glyphHeight)
            {
                RunsAcross = runsAcross,
                Untranslated = string.IsNullOrWhiteSpace(target) && !skipTranslation,
            });
        }
        var detected = Text(root, "lanFrom").ToLowerInvariant() switch
        {
            "zh-cht" => "ZH-HANT",
            "zh-chs" => "ZH",
            var language => EngineLanguage.FromEngine(language),
        };
        return new(sources, translated, detected);
    }

    internal static ScreenshotTranslationResult MatchSourceFont(
        Bitmap bitmap, ScreenshotTranslationResult result, bool verticalText = false)
    {
        using var pixels = OnnxOcrEngine.ConvertToSkBitmap(bitmap);
        var sources = new List<OcrTextBlock>(result.SourceBlocks.Count);
        var translated = new List<TranslatedBlock>(result.Blocks.Count);
        for (var i = 0; i < result.SourceBlocks.Count; i++)
        {
            var source = result.SourceBlocks[i];
            // The ink measurer is for horizontal lines on a sufficiently flat background. When it
            // cannot measure reliably, retain the source-box estimate, never a translated lineheight.
            var ink = verticalText && !source.RunsAcross ? [] : source.Lines
                .Select(line => TextInkMetrics.Measure(pixels, line))
                .Where(height => height.HasValue).Select(height => height!.Value).ToArray();
            var height = ink.Length > 0 ? Median(ink) : source.RenderGlyphHeight;
            sources.Add(source with { RenderGlyphHeight = height, FontGlyphHeight = height });
            translated.Add(result.Blocks[i] with { RenderGlyphHeight = height, FontGlyphHeight = height });
        }
        return result with { SourceBlocks = sources, Blocks = translated };
    }

    private static List<JsonElement>[] MatchPageLines(
        JsonElement root, JsonElement[] regions, double scaleX, double scaleY, Rect canvas)
    {
        var matched = regions.Select(_ => new List<JsonElement>()).ToArray();
        if (!root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return matched;
        var regionBounds = regions.Select(region => ReadBounds(region, scaleX, scaleY, canvas)).ToArray();
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object || ReadBounds(line, scaleX, scaleY, canvas) is not { } bounds)
                continue;
            var best = -1;
            double bestArea = 0;
            for (var i = 0; i < regionBounds.Length; i++)
            {
                if (regionBounds[i] is not { } region) continue;
                var overlap = Rect.Intersect(bounds, region);
                var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                if (area > bestArea && area >= bounds.Width * bounds.Height * 0.5)
                {
                    best = i;
                    bestArea = area;
                }
            }
            if (best >= 0) matched[best].Add(line);
        }
        return matched;
    }

    private static List<Rect> SplitSourceLines(JsonElement region, Rect bounds, bool vertical)
    {
        var count = int.TryParse(Text(region, "linesCount"), out var value) && value > 1
            && value <= Math.Min(512, vertical ? bounds.Width : bounds.Height) ? value : 1;
        return Enumerable.Range(0, count).Select(index => vertical
            ? new Rect(bounds.X + bounds.Width * index / count, bounds.Y, bounds.Width / count, bounds.Height)
            : new Rect(bounds.X, bounds.Y + bounds.Height * index / count, bounds.Width, bounds.Height / count)).ToList();
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    private static Rect? ReadBounds(JsonElement item, double scaleX, double scaleY, Rect canvas)
    {
        var parts = Text(item, "boundingBox").Split(',');
        if (parts.Length != 4) return null;
        var values = new double[4];
        for (var i = 0; i < values.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !double.IsFinite(values[i])) return null;
        if (values[2] <= 0 || values[3] <= 0) return null;
        var scaled = new[] { values[0] * scaleX, values[1] * scaleY, values[2] * scaleX, values[3] * scaleY };
        if (scaled.Any(value => !double.IsFinite(value))) return null;
        var bounds = new Rect(scaled[0], scaled[1], scaled[2], scaled[3]);
        bounds.Intersect(canvas);
        return bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ? null : bounds;
    }

    private static string Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : "";

    private static string FirstText(JsonElement item, params string[] keys) =>
        keys.Select(key => Text(item, key)).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";

    private static TranslationEngineException InvalidResponse() => new(Engine, "Invalid screenshot translation geometry or regions.");
}
