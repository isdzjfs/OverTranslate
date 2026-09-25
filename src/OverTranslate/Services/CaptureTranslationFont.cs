using MediaFontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Services;

/// <summary>The font stack used by translated text drawn over a screenshot.</summary>
public static class CaptureTranslationFont
{
    public const string DefaultFamilySource = "Microsoft JhengHei, Segoe UI, Sans-Serif";

    private static readonly string[] DefaultFamilies =
        DefaultFamilySource.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Puts the user's font first and retains the shipped families as glyph fallbacks. A font that
    /// has attractive Latin letters but no CJK glyphs can therefore still translate Chinese,
    /// Japanese and Korean without drawing missing-character boxes.
    /// </summary>
    public static string SourceFor(string? selectedFamily)
    {
        var selected = NormalizeSelection(selectedFamily);
        if (selected.Length == 0)
            return DefaultFamilySource;

        return string.Join(", ", new[] { selected }
            .Concat(DefaultFamilies)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static MediaFontFamily Resolve(string? selectedFamily) => new(SourceFor(selectedFamily));

    public static string NormalizeSelection(string? selectedFamily) => selectedFamily?.Trim() ?? "";
}
