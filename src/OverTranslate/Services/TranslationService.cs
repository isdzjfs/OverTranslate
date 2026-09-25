using System.Net.Http;
using GTranslate.Translators;
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
}

public class TranslationService
{
    // Shared HttpClient so a hung free endpoint fails fast instead of stalling the whole batch.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly GoogleTranslateHtmlProvider _google = new(Http);
    private readonly GTranslateProvider _google2   = new(new GoogleTranslator2(Http));
    private readonly GTranslateProvider _bing      = new(new BingTranslator(Http));
    private readonly GTranslateProvider _microsoft = new(new MicrosoftTranslator(Http));
    private readonly DeepLProvider      _deepL     = new();
    private readonly OpenAiCompatibleProvider _openAi = new();

    /// <summary>
    /// The engine a caller that has not said otherwise gets: whatever the user last chose in the
    /// places that share one preference — 設定, 文字翻譯 and the capture toolbar.
    /// </summary>
    private static TranslationProvider Saved => SettingsService.Instance.Current.Provider;

    private ITranslationProvider Selected(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google    => _google,
        TranslationProvider.Google2   => _google2,
        TranslationProvider.Bing      => _bing,
        TranslationProvider.Microsoft => _microsoft,
        TranslationProvider.DeepL     => _deepL,
        TranslationProvider.OpenAI    => _openAi,
        _                             => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private GTranslateProvider? DictionaryProvider(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Bing      => _bing,
        TranslationProvider.Microsoft => _microsoft,
        _                             => null,
    };

    public bool RequiresApiKey => Selected(Saved).RequiresApiKey;

    /// <summary>Whether a specific engine needs an API key, for a caller that chose its own.</summary>
    public bool ProviderRequiresApiKey(TranslationProvider provider) => Selected(provider).RequiresApiKey;

    /// <param name="engine">
    /// Which engine to send to, or null to use the shared preference. 即時翻譯 passes its own: that
    /// page keeps its settings to itself, so the engine it is running with is not necessarily the
    /// one saved, and reading the saved one here would quietly translate with something the user
    /// did not pick.
    /// </param>
    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default, TranslationProvider? engine = null)
    {
        var chosen   = engine ?? Saved;
        var provider = Selected(chosen);
        var result   = await provider.TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken);
        return result;
    }

    /// <summary>
    /// Looks up rich dictionary data only when the caller explicitly asks for it. Normal translation,
    /// screenshot translation and realtime translation keep their existing request count and latency.
    /// </summary>
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
            ? DictionarySimplifiedChineseConverter.Convert(lookupText)
            : lookupText;
        var result = await provider.LookupDictionaryAsync(
            requestText, step.SourceLanguage, step.TargetLanguage, cancellationToken);
        return result is null ? null : PrepareDictionaryResult(result, lookupText, step.ConvertToTraditional);
    }

    internal static DictionaryLookupData PrepareDictionaryResult(
        DictionaryLookupData result, string originalText, bool convertToTraditional)
    {
        var prepared = convertToTraditional
            ? DictionaryTraditionalChineseConverter.Convert(result)
            : result;
        return prepared with { Headword = originalText };
    }
}
