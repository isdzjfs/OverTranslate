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

    private readonly record struct Item(int BlockIndex, string EncodedText);

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
                var encoded = EscapeHtml(chunk.Text.Replace('\r', ' ').Replace('\n', ' '));
                chunksByBlock[i].Add(chunk);
                items.Add(new Item(i, encoded));
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
            var translated = await TranslateBatchAsync(batch, from, to, cancellationToken);
            for (var i = 0; i < batch.Count; i++)
                answersByBlock[batch[i].BlockIndex].Add(translated[i]);
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

        // translateHtml returns translated strings only. It does not report a detected language.
        return (result, LanguageData.IsAutomaticSource(sourceLang) ? "" : sourceLang.ToUpperInvariant());
    }

    private async Task<List<string>> TranslateBatchAsync(
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

        var translations = new List<string>(items.Count);
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Google2 returned an invalid translation value.");
            translations.Add(WebUtility.HtmlDecode(value.GetString()!));
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
