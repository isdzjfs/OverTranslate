using System.Net.Http;
using OverTranslate.Translation;
using OverTranslate.Translation.Bing;
using OverTranslate.Translation.Google;
using OverTranslate.Translation.Lookup;
using OverTranslate.Translation.Microsoft;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services.Providers;

namespace OverTranslate.Services;

public record TranslatedBlock(
    string OriginalText,
    string TranslatedText,
    System.Windows.Rect Bounds,
    IReadOnlyList<System.Windows.Rect>? SourceLineBounds = null,
    double? RenderGlyphHeight = null,
    System.Windows.Media.Color BackgroundColor = default,
    System.Windows.Media.Color TextColor = default,

    // Set by placement, read by the overlay. Default until something decides otherwise, so the
    // realtime path and the translation providers carry it without knowing it is there.
    OverlayLayoutIntent LayoutIntent = OverlayLayoutIntent.Default)
{
    /// <summary>
    /// Carried over from the block this was read from — see <see cref="OcrTextBlock.RunsAcross"/>.
    /// </summary>
    /// <remarks>
    /// A property rather than a constructor parameter because every provider builds this record
    /// from the same five fields and none of them has any business deciding this one. They copy it
    /// across unread, which is all a translator can honestly do with it.
    /// </remarks>
    public bool RunsAcross { get; init; }

    /// <summary>
    /// Carried over from the block this was read from — see <see cref="OcrTextBlock.FontGlyphHeight"/>.
    /// </summary>
    public double? FontGlyphHeight { get; init; }

    /// <summary>
    /// Carried over from the block this was read from — see <see cref="OcrTextBlock.Tilt"/>.
    /// </summary>
    internal Ocr.TiltedText? Tilt { get; init; }

    /// <summary>
    /// True when no engine produced a translation and <see cref="TranslatedText"/> is only the
    /// original text standing in for one.
    /// </summary>
    /// <remarks>
    /// Said outright rather than left to be inferred from the two texts being equal: a number, a
    /// name or "OK" translates to itself, and a caller that retried those would retry forever.
    /// </remarks>
    public bool Untranslated { get; init; }
}

public class TranslationService
{
    private static readonly HttpClient Http = EngineHttp.CreateClient(TranslationTiming.Request);
    private static readonly HttpClient DeepLHttp = new() { Timeout = TranslationTiming.Request };

    private readonly EngineProvider _google;
    private readonly EngineProvider _googleWeb;
    private readonly EngineProvider _googleRpc;
    private readonly EngineProvider _bing;
    private readonly EngineProvider _microsoft;
    private readonly DeepLProvider _deepL = new(DeepLHttp);
    private readonly OpenAiCompatibleProvider _openAi = new();
    private readonly DictionaryLookupProvider _googleDictionary;
    private readonly DictionaryLookupProvider _bingDictionary;
    private readonly DictionaryLookupProvider _microsoftDictionary;

    public TranslationService(HttpClient? http = null, Func<string>? loadGoogleKey = null,
        Action<string>? saveGoogleKey = null)
    {
        var client = http ?? Http;
        _google = new(new GoogleChromeTranslator(client,
            loadGoogleKey ?? (() => SettingsService.Instance.Current.GoogleChromeApiKey),
            saveGoogleKey ?? (key =>
            {
                SettingsService.Instance.Current.GoogleChromeApiKey = key;
                SettingsService.Instance.Save();
            })));
        _googleWeb = new(new GoogleWebTranslator(client));
        _googleRpc = new(new GoogleRpcTranslator(client));
        _bing = new(new BingTranslator(client));
        _microsoft = new(new MicrosoftTranslator(client));
        _googleDictionary = new(new GoogleDictionary(client), "Google Web");
        _bingDictionary = new(new BingDictionary(client), "Bing");
        _microsoftDictionary = new(new MicrosoftDictionary(client), "Microsoft");
    }

    private static TranslationProvider Saved => SettingsService.Instance.Current.Provider;

    // Keep the concrete endpoint the user selected. Failures never start another engine.
    private ITranslationProvider Selected(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google or TranslationProvider.GoogleChrome => _google,
        TranslationProvider.GoogleWeb => _googleWeb,
        TranslationProvider.Google2 => _googleRpc,
        TranslationProvider.Bing => _bing,
        TranslationProvider.Microsoft => _microsoft,
        TranslationProvider.DeepL => _deepL,
        TranslationProvider.OpenAI => _openAi,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private DictionaryLookupProvider? DictionaryProvider(TranslationProvider provider) => provider switch
    {
        TranslationProvider.GoogleWeb => _googleDictionary,
        TranslationProvider.Bing => _bingDictionary,
        TranslationProvider.Microsoft => _microsoftDictionary,
        _ => null,
    };

    public bool RequiresApiKey => Selected(Saved).RequiresApiKey;
    public bool ProviderRequiresApiKey(TranslationProvider provider) => Selected(provider).RequiresApiKey;

    public Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default, TranslationProvider? engine = null) =>
        Selected(engine ?? Saved).TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken);

    public async Task<DictionaryLookupData?> LookupDictionaryAsync(
        string text, string sourceLang, string targetLang,
        CancellationToken cancellationToken = default, TranslationProvider? engine = null)
    {
        if (!DictionaryLookupEligibility.IsEligible(text)) return null;
        var lookupText = text.Trim();
        var step = DictionaryLookupPlan.Build(engine ?? Saved, sourceLang, targetLang);
        if (step is null) return null;
        var provider = DictionaryProvider(step.Provider);
        if (provider is null) return null;
        var requestText = step.ConvertSourceToSimplified
            ? DictionarySimplifiedChineseConverter.Convert(lookupText) : lookupText;
        var result = await provider.LookupDictionaryAsync(
            requestText, step.SourceLanguage, step.TargetLanguage, cancellationToken);
        return result is null ? null : PrepareDictionaryResult(result, lookupText, step.ConvertToTraditional);
    }

    internal static DictionaryLookupData PrepareDictionaryResult(
        DictionaryLookupData result, string originalText, bool convertToTraditional)
    {
        var prepared = convertToTraditional ? DictionaryTraditionalChineseConverter.Convert(result) : result;
        return prepared with { Headword = originalText };
    }
}
