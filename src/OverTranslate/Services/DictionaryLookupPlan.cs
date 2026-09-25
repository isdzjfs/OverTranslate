using OverTranslate.Models;

namespace OverTranslate.Services;

internal sealed record DictionaryLookupStep(
    TranslationProvider Provider,
    string SourceLanguage,
    string TargetLanguage,
    bool ConvertSourceToSimplified,
    bool ConvertToTraditional);

internal static class DictionaryLookupPlan
{
    internal static DictionaryLookupStep? Build(
        TranslationProvider selectedProvider, string sourceLanguage, string targetLanguage)
    {
        // A provider without dictionary support leaves the optional dictionary panel empty.
        // Looking up another provider here would silently send the user's text elsewhere.
        if (selectedProvider is not (TranslationProvider.Google or TranslationProvider.Microsoft or TranslationProvider.Bing))
            return null;

        var convertSource = sourceLanguage.Equals("ZH-HANT", StringComparison.OrdinalIgnoreCase) &&
            (selectedProvider is TranslationProvider.Microsoft or TranslationProvider.Bing);
        var convertTarget = targetLanguage.Equals("ZH-HANT", StringComparison.OrdinalIgnoreCase) &&
            (selectedProvider is TranslationProvider.Microsoft or TranslationProvider.Bing);

        return new DictionaryLookupStep(
            selectedProvider,
            convertSource ? "ZH-HANS" : sourceLanguage,
            convertTarget ? "ZH-HANS" : targetLanguage,
            convertSource,
            convertSource || convertTarget);
    }
}
