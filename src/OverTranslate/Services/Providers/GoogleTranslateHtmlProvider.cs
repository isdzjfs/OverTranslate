using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

/// <summary>
/// Google2's translateHtml protocol, also used by the local ReVanced translation patch.
/// The API key is the public web-client key bundled with that protocol, not a user credential.
/// </summary>
public sealed class GoogleTranslateHtmlProvider(HttpClient http) : ITranslationProvider
{
    private const string Endpoint = "https://translate-pa.googleapis.com/v1/translateHtml";
    private const string WebClientKey = "AIzaSyATBXajvzQLTDHEQbcpq0Ihe0vWDHmO520";
    private const int MaxItemsPerRequest = 50;
    private const int MaxCharactersPerRequest = 12000;
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly record struct Item(int BlockIndex, string PlainText, string EncodedText);
    private readonly record struct Translation(string Text, string DetectedLanguage);

    public bool RequiresApiKey => false;

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0) return ([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var items = new List<Item>();
        var chunksByBlock = new List<TranslationRequestChunk>[blocks.Count];
        var answersByBlock = new List<string>[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            chunksByBlock[i] = [];
            answersByBlock[i] = [];
            if (string.IsNullOrWhiteSpace(blocks[i].Text)) continue;

            foreach (var chunk in TranslationRequestChunks.Split(blocks[i].Text))
            {
                var plain = chunk.Text.Replace('\r', ' ').Replace('\n', ' ');
                chunksByBlock[i].Add(chunk);
                items.Add(new Item(i, plain, EscapeHtml(plain)));
            }
        }

        var from = GTranslateProvider.MapSourceToGTranslate(sourceLang) ?? "auto";
        var to = GTranslateProvider.MapToGTranslate(targetLang);
        for (var start = 0; start < items.Count;)
        {
            var end = start;
            var characters = 0;
            while (end < items.Count && end - start < MaxItemsPerRequest)
            {
                // The public web endpoint has a batch budget; escaped HTML is what it receives.
                var next = items[end].EncodedText.Length + 32;
                if (end > start && characters + next > MaxCharactersPerRequest) break;
                if (next > MaxCharactersPerRequest)
                    throw new InvalidOperationException("Google2 input exceeds the request limit.");
                characters += next;
                end++;
            }

            var batch = items.GetRange(start, end - start);
            var translated = await TranslateBatchWithMixedSourceRetryAsync(batch, from, to, cancellationToken);
            for (var i = 0; i < batch.Count; i++)
                answersByBlock[batch[i].BlockIndex].Add(translated[i].Text);
            start = end;
        }

        var result = new List<TranslatedBlock>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var translation = chunksByBlock[i].Count == 0
                ? block.Text
                : TranslationRequestChunks.Join(chunksByBlock[i], answersByBlock[i]);
            result.Add(new TranslatedBlock(block.Text, translation, block.Bounds, block.Lines, block.RenderGlyphHeight)
                { RunsAcross = block.RunsAcross });
        }

        // A batch may contain different source languages. Its per-item detections are useful for
        // the retry above but cannot honestly be returned as one detected language for the block list.
        return (result, LanguageData.IsAutomaticSource(sourceLang) ? "" : sourceLang.ToUpperInvariant());
    }

    private async Task<List<Translation>> TranslateBatchWithMixedSourceRetryAsync(
        IReadOnlyList<Item> items, string from, string to, CancellationToken cancellationToken)
    {
        var translations = await TranslateBatchAsync(items, from, to, cancellationToken);
        if (from != "auto") return translations;

        var candidates = new List<(int Index, string Probe)>();
        for (var i = 0; i < items.Count; i++)
        {
            var translation = translations[i];
            if (!SameLanguage(translation.DetectedLanguage, to)) continue;
            var probe = FindLatinProse(items[i].PlainText);
            if (probe is not null && translation.Text.Contains(probe, StringComparison.OrdinalIgnoreCase))
                candidates.Add((i, probe));
        }
        if (candidates.Count == 0) return translations;

        // Google's auto detector can choose the Chinese labels in a mostly English sentence and
        // leave its English clause untouched. Probe that clause alone before choosing a source;
        // Latin letters also belong to French, Spanish and many other languages.
        try
        {
            var probeItems = candidates.Select(candidate =>
                new Item(items[candidate.Index].BlockIndex, candidate.Probe, EscapeHtml(candidate.Probe))).ToList();
            var probeResults = await TranslateBatchAsync(probeItems, "auto", to, cancellationToken);
            var retryGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < candidates.Count; i++)
            {
                var source = probeResults[i].DetectedLanguage;
                if (source.Length == 0 || SameLanguage(source, to)) continue;
                if (!retryGroups.TryGetValue(source, out var indexes))
                    retryGroups[source] = indexes = [];
                indexes.Add(candidates[i].Index);
            }

            foreach (var (source, indexes) in retryGroups)
            {
                var retryItems = indexes.Select(index => items[index]).ToList();
                var retried = await TranslateBatchAsync(retryItems, source, to, cancellationToken);
                for (var i = 0; i < indexes.Count; i++)
                    translations[indexes[i]] = retried[i];
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            // A failed optional retry must not turn the successful first response into an error.
        }
        return translations;
    }

    private static bool SameLanguage(string first, string second) =>
        first.Length > 0 && first.Split('-')[0].Equals(second.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private static string? FindLatinProse(string text)
    {
        var hanCount = text.Count(c => c is >= '\u3400' and <= '\u9fff');
        if (hanCount < 2) return null;

        string? best = null;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var allowed = i < text.Length &&
                (text[i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                    ' ' or '\t' or '\n' or '\r' or '\'' or '\u2019' or '-' or '.' or ',' or '!' or '?');
            if (allowed)
            {
                if (start < 0) start = i;
                continue;
            }
            if (start < 0) continue;
            var span = text[start..i].Trim(' ', '\t', '\n', '\r', '\'', '\u2019', '-', '.', ',', '!', '?');
            if (span.Length > (best?.Length ?? 0)) best = span;
            start = -1;
        }

        if (best is null) return null;
        var letters = best.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        var words = best.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(word => word.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') >= 2);
        return letters >= 25 && letters > hanCount * 2 && words >= 5 ? best : null;
    }

    private async Task<List<Translation>> TranslateBatchAsync(
        IReadOnlyList<Item> items, string from, string to, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new object[]
        {
            new object[] { items.Select(item => item.EncodedText).ToArray(), from, to },
            "wt_lib"
        }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Goog-API-Key", WebClientKey);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Mobile Safari/537.36");
        request.Content = new StringContent(payload, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json+protobuf");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var readBuffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(readBuffer, cancellationToken)) != 0)
        {
            if (buffer.Length + bytesRead > MaxResponseBytes)
                throw new InvalidOperationException("Google2 response exceeds the size limit.");
            buffer.Write(readBuffer, 0, bytesRead);
        }
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0 ||
            document.RootElement[0].ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Google2 returned an invalid translation response.");

        var array = document.RootElement[0];
        if (array.GetArrayLength() != items.Count)
            throw new InvalidOperationException("Google2 returned an unexpected number of translations.");

        var detected = document.RootElement.GetArrayLength() > 1 &&
            document.RootElement[1].ValueKind == JsonValueKind.Array &&
            document.RootElement[1].GetArrayLength() == items.Count
            ? document.RootElement[1] : default;
        var translations = new List<Translation>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var value = array[i];
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Google2 returned an invalid translation value.");
            var language = detected.ValueKind == JsonValueKind.Array &&
                detected[i].ValueKind == JsonValueKind.String ? detected[i].GetString()! : "";
            translations.Add(new Translation(WebUtility.HtmlDecode(value.GetString()!), language));
        }
        return translations;
    }

    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
