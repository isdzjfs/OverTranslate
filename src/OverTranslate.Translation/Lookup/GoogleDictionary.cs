using System.Net;
using System.Text.Json;
using static OverTranslate.Translation.Lookup.LookupJson;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Google's dictionary: <c>translate_a/single</c>, the one-text endpoint the Web engine's
/// <c>translate_a/t</c> is the sibling of, asked for its dictionary part as well as the translation.
/// </summary>
/// <remarks>
/// <para>The request and the reading of the answer are GTranslate's
/// <c>GoogleTranslator.LookupDictionaryAsync</c> and <c>GoogleDictionaryParser</c>
/// (MIT, d4n3436/GTranslate), less what the dictionary card never showed (listed on
/// <see cref="DictionaryResult"/>, with how to ask for it again) and less the <c>tk</c> token:
/// measured 2026-09-27 with and without it, the answers were the same.</para>
///
/// <para>The parts asked for: <c>bd</c> translations by part of speech, and <c>t</c> with
/// <c>rm</c>, whose sentences carry the pronunciation (<c>src_translit</c>). GTranslate asked for
/// <c>t</c> without <c>rm</c>, and without it the reading is never there, so the dictionary card
/// never showed one. Measured 2026-09-27 on nine words: <c>rm</c> adds the reading for every
/// source language tried (食べる → Taberu, 电脑 → Diànnǎo, 사랑 → salang, дом → dom) and leaves
/// the entries exactly as they were.</para>
///
/// <para>The <c>client</c> name is used once; a failure is reported without retry — see
/// <see cref="Client"/>.</para>
/// </remarks>
public sealed class GoogleDictionary(HttpClient http) : DictionaryEngine(http)
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    /// <summary>The dictionary extension's <c>client</c> name.</summary>
    /// <remarks>
    /// <para>Google limits this endpoint per client name as well as per address. Measured
    /// 2026-09-27 on a machine that had worn it out: over IPv6, <c>gtx</c> — GTranslate's name, and
    /// the Web engine's — answered 429 while <c>dict-chrome-ex</c> or any other unreserved name
    /// answered 200 with the same entries and reading. Over IPv4 every name was refused, so a
    /// second name only helps where the limit is on the name. <c>t</c> and <c>webapp</c> are
    /// Google's own pages and want a <c>tk</c> token (403); no name at all is a 400.</para>
    ///
    /// <para><c>dict-chrome-ex</c> is Chrome's dictionary extension, whose everyday traffic
    /// these requests sit among, where a name only this application used could be singled out.
    /// </para>
    /// </remarks>
    private const string Client = "dict-chrome-ex";

    public override string Name => "GoogleDictionary";

    protected override async Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        var json = await AskAsync(Client, text, targetLanguage, sourceLanguage, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return Read(document.RootElement, text);
    }

    /// <remarks>One lookup message; errors are returned directly.</remarks>
    private async Task<string> AskAsync(
        string client, string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        var url = $"{Endpoint}?client={client}&sl={Uri.EscapeDataString(sourceLanguage)}&tl={Uri.EscapeDataString(targetLanguage)}" +
                  "&dt=t&dt=bd&dt=rm&dj=1&source=input";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]),
        };
        return await ReadStringAsync(request, cancellationToken);
    }

    internal static DictionaryResult Read(JsonElement root, string text)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException($"expected an object, found {root.ValueKind}");

        var groups = Items(root, "dict").Select(group => new DictionaryGroup(
            OptionalString(group, "pos"),
            Items(group, "entry")
                .Where(entry => OptionalString(entry, "word") is not null)
                .Select(entry => new DictionaryEntry(
                    OptionalString(entry, "word")!,
                    null,
                    Items(entry, "reverse_translation")
                        .Where(back => back.ValueKind == JsonValueKind.String)
                        .Select(back => back.GetString()!)
                        .ToList()))
                .ToList()))
            .ToList();

        var pronunciation = Items(root, "sentences")
            .Select(sentence => OptionalString(sentence, "src_translit"))
            .FirstOrDefault(reading => reading is not null);

        return new DictionaryResult(text, pronunciation, groups);
    }
}
