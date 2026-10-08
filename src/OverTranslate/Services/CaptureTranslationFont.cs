using MediaFontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Services;

/// <summary>The font stack used by translated text drawn over a screenshot.</summary>
public static class CaptureTranslationFont
{
    public const string DefaultFamilySource = "Microsoft JhengHei, Segoe UI, Sans-Serif";

    /// <summary>
    /// Puts the user's font first and retains the shipped families as glyph fallbacks. A font that
    /// has attractive Latin letters but no CJK glyphs can therefore still translate Chinese,
    /// Japanese and Korean without drawing missing-character boxes.
    /// </summary>
    public static string SourceFor(string? selectedFamily, string? targetLanguage = null)
    {
        var selected = NormalizeSelection(selectedFamily);
        var fallback = targetLanguage is null ? DefaultFamilySource : TranslatedTextFont.FamilyList(targetLanguage);
        if (selected.Length == 0)
            return fallback;

        return string.Join(", ", new[] { selected }
            .Concat(fallback.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static MediaFontFamily Resolve(string? selectedFamily, string? targetLanguage = null) => new(SourceFor(selectedFamily, targetLanguage));

    public static string NormalizeSelection(string? selectedFamily) => selectedFamily?.Trim() ?? "";
}
