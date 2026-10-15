using RapidOcrNet;

namespace OverTranslate.Services.Ocr;

/// <summary>Chooses per line between the general CJK/Latin reader and the Cyrillic reader.</summary>
internal static class AutomaticOcrRecognition
{
    public static bool NeedsCyrillicReading(TextLine general) =>
        Score(general) < 0.8 || !OnnxOcrEngine.UsesCjkRenderMetricsForText(Text(general));

    public static TextLine Choose(TextLine general, TextLine cyrillic)
    {
        var text = Text(cyrillic);
        var letters = text.Count(char.IsLetter);
        var cyrillicLetters = text.Count(character => character is >= '\u0400' and <= '\u04ff');
        var generalLetters = Text(general).Count(char.IsLetter);
        // A restricted alphabet can turn Latin glyphs into lookalike Cyrillic. Require actual
        // Cyrillic evidence and a better reading, rather than replacing every Latin-looking line.
        // An unsupported alphabet can also disappear completely, leaving just highly confident
        // punctuation (", !" for "Привет, мир!"). Its score cannot veto actual recovered words.
        return cyrillicLetters > 0 && cyrillicLetters >= letters * 0.3
            && Score(cyrillic) >= 0.8
            && (letters >= generalLetters + 2 || Score(cyrillic) >= Score(general) + 0.02)
            ? cyrillic : general;
    }

    private static string Text(TextLine line) => string.Concat(line.Chars ?? []);
    private static double Score(TextLine line) => line.CharScores is { Length: > 0 } scores ? scores.Average() : 0;
}
