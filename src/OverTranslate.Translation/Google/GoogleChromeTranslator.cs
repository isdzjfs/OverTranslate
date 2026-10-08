using System.Net;
using System.Text;
using System.Text.Json;
using NLog;

namespace OverTranslate.Translation.Google;

/// <summary>
/// 「Google 翻譯 (Google2)」: the endpoint behind Chrome's own "translate this page".
/// </summary>
/// <remarks>
/// <para><c>translate-pa.googleapis.com/v1/translateHtml</c> is what translates a whole web page —
/// every text node of it in one request — so a list of texts is the shape it was built for. That
/// it is Chrome's is read from the code, not guessed: Chromium's <c>translate_script.cc</c> loads
/// Google's translate element (<c>translate_a/element.js</c>), and the element's script calls this
/// path with its key. Google documents none of it.</para>
///
/// <para>A different model from 「Google 翻譯 (標準)」, not a faster route to the same one: only 3 of
/// 12 test sentences matched, and over 80 reviewed ones its translations were better in 40 and
/// worse in 3. An unfamiliar name next to a familiar phrase can vanish — 「millsage*1st Single」 comes back
/// 「首支單曲」 — or be read as a word, which reads as a finished translation and cannot be told
/// from one on this side. See <c>.ai/translation-service-analysis/google-comparison.md</c>.</para>
///
/// <para>The request shape follows <c>isdzjfs/OverTranslate</c>'s <c>GoogleTranslateHtmlProvider</c>,
/// which found it; the code is this library's own.</para>
///
/// <para>Its input is HTML, and that has two consequences. Markup characters must be escaped or
/// 「Press &lt;A&gt;」 loses its button, and whitespace is whitespace: a line break inside a
/// paragraph is a space to it, and a blank line between two paragraphs disappears entirely. So
/// paragraphs are sent as separate pieces and put back with their blank line between them, and
/// lone line breaks are sent as the spaces the endpoint would read them as anyway.</para>
///
/// <para>The key is not kept here. It is read from the translate element the first time it is
/// needed and handed to <c>saveKey</c> to keep, and read again on a later request after Google refuses it — see
/// <see cref="GoogleChromeKeySource"/>. A key that keeps working is never looked up a second
/// time.</para>
///
/// <para>Seen once and worth knowing: on the sentence that sends the other Google engines into a
/// repetition loop, this one does not loop — it cuts the last clause off instead, and a truncated
/// sentence looks like a finished one. Not something this side can detect.</para>
/// </remarks>
/// <param name="loadKey">The key kept from last time, or empty. Without it, kept in memory.</param>
/// <param name="saveKey">Told when a new key has been read, to keep it for next time.</param>
public sealed class GoogleChromeTranslator(
    HttpClient http, Func<string>? loadKey = null, Action<string>? saveKey = null) : BatchTranslator(http)
{
    private const string Path = "/v1/translateHtml";
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    // How long a failed or refused lookup is trusted before the scripts are asked again. Without it
    // a broken lookup would be 300 KB on every request, and realtime translation sends one every
    // few seconds; lookup failures are reported to the caller.
    internal static readonly TimeSpan LookupCooldown = TimeSpan.FromMinutes(10);

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SemaphoreSlim _lookup = new(1, 1);
    private string _key = "";
    private string _refusedKey = "";
    private long _lastLookup = long.MinValue;

    public override string Name => "Google (Chrome)";

    protected override int MaxItemsPerRequest => 50;

    // Successful mixed-language answers can still leave a foreign clause untranslated.
    protected override bool RescuesMixedScript => true;

    /// <remarks>Counted before escaping; escaping only lengthens the few texts with markup in them.</remarks>
    protected override int MaxCharactersPerRequest => 5000;

    private string StoredKey => loadKey?.Invoke() ?? _key;

    private protected override IReadOnlyList<TranslationRequestChunk> Split(string text) =>
        SplitParagraphs(text, joinLines: true);

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new object[]
        {
            new object[] { pieces.Select(Escape).ToArray(), sourceLanguage ?? "auto", targetLanguage },

            // What Chrome's own page translation sends: the translate element in library mode.
            // Other tags, and the request's two optional fields, were tried and changed nothing
            // except "ests", which translates only from English (google-comparison.md 9.3).
            "te_lib",
        });

        var key = StoredKey;
        if (key.Length == 0)
            key = await LookUpKeyAsync(_refusedKey, cancellationToken);

        var (status, body) = await PostAsync("translate-pa.googleapis.com", payload, key, cancellationToken);
        if (IsKeyRefusal(status, body))
        {
            // Report this failure immediately. A later user request can obtain a fresh key.
            _refusedKey = key;
            _key = "";
            saveKey?.Invoke("");
        }

        if ((int)status is < 200 or > 299)
            throw new TranslationEngineException(Name, $"HTTP {(int)status}", status);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 ||
            root[0].ValueKind != JsonValueKind.Array)
            throw new TranslationEngineException(Name, "unexpected answer");

        var translations = root[0];
        var languages = root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.Array &&
                        root[1].GetArrayLength() == translations.GetArrayLength()
            ? root[1]
            : default;

        var answers = new List<TextTranslation>(translations.GetArrayLength());
        for (var i = 0; i < translations.GetArrayLength(); i++)
        {
            var detected = languages.ValueKind == JsonValueKind.Array &&
                           languages[i].ValueKind == JsonValueKind.String
                ? LanguageCodes.FromGoogle(languages[i].GetString()!)
                : "";

            answers.Add(new TextTranslation(WebUtility.HtmlDecode(RequireString(translations[i])), detected));
        }

        return answers;
    }

    /// <summary>
    /// Tells a refused key from every other refusal. A key Google does not know is a 400 that says
    /// so — <c>API_KEY_INVALID</c>, and <c>API_KEY_EXPIRED</c> by the same pattern — and a key that
    /// is real but not allowed this endpoint, or none at all, is a 403.
    /// </summary>
    /// <remarks>
    /// A 400 that does not name the key is the request's fault, not the key's, and looking the
    /// key up again would not change its answer.
    /// </remarks>
    internal static bool IsKeyRefusal(HttpStatusCode status, string body) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ||
        status == HttpStatusCode.BadRequest && body.Contains("API_KEY_", StringComparison.Ordinal);

    /// <summary>
    /// A key other than <paramref name="refused"/>: one another request has already looked up, or a
    /// new one from the translate element.
    /// </summary>
    /// <remarks>
    /// One lookup at a time, because a screen goes out as several requests at once and all of them
    /// are refused together; the first looks the key up and the rest find it waiting.
    /// </remarks>
    private async Task<string> LookUpKeyAsync(string refused, CancellationToken cancellationToken)
    {
        await _lookup.WaitAsync(cancellationToken);
        try
        {
            var stored = StoredKey;
            if (stored.Length > 0 && stored != refused)
                return stored;

            if (_lastLookup != long.MinValue &&
                System.Diagnostics.Stopwatch.GetElapsedTime(_lastLookup) < LookupCooldown)
            {
                return stored.Length > 0
                    ? stored
                    : throw new TranslationEngineException(Name, "no key; the last lookup failed");
            }

            _lastLookup = System.Diagnostics.Stopwatch.GetTimestamp();

            IReadOnlyList<string> found;
            try
            {
                found = await GoogleChromeKeySource.FetchAsync(Http, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Info(ex, "{Engine}：讀不到翻譯元件裡的金鑰", Name);
                return stored.Length > 0
                    ? stored
                    : throw new TranslationEngineException(Name, "no key; the lookup failed", inner: ex);
            }

            // Prefer a key other than the one refused by the previous user request.
            var key = found.FirstOrDefault(k => k != refused) ?? found[0];
            if (key != stored)
            {
                Log.Info("{Engine}：已從翻譯元件取得金鑰", Name);
                _key = key;
                saveKey?.Invoke(key);
            }

            return key;
        }
        finally
        {
            _lookup.Release();
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string host, string payload, string key, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead ends HttpClient's timeout at the headers; keep the body bounded too.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Http.Timeout);
        var requestToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{Path}")
        {
            Content = new StringContent(payload, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new("application/json+protobuf");
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.TryAddWithoutValidation("X-Goog-API-Key", key);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(bytes, requestToken)) != 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
                throw new TranslationEngineException(Name, "response exceeds the size limit");
            buffer.Write(bytes, 0, count);
        }
        return (response.StatusCode, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
