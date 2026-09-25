using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// Reuses completed Google Web translations when a screenshot contains the same OCR text again.
/// The cache is in memory only; the caller decides which provider may use it.
/// </summary>
internal sealed class CaptureTranslationCache
{
    private const int Capacity = 256;

    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly LinkedList<Key> _recent = new();

    private readonly record struct Key(
        TranslationProvider Provider, string SourceLanguage, string TargetLanguage, string Text);

    private sealed record Entry(string Translation, LinkedListNode<Key> Node);

    public async Task<(List<TranslatedBlock> Blocks, int Hits, int Requests)> TranslateAsync(
        List<OcrTextBlock> blocks,
        TranslationProvider provider,
        string sourceLanguage,
        string targetLanguage,
        Func<List<OcrTextBlock>, Task<List<TranslatedBlock>>> translateMissing)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<OcrTextBlock>();
        var missingTexts = new HashSet<string>(StringComparer.Ordinal);
        var hits = 0;

        foreach (var block in blocks)
        {
            var key = new Key(provider, sourceLanguage, targetLanguage, block.Text);
            if (TryGet(key, out var answer))
            {
                answers[block.Text] = answer;
                hits++;
            }
            else if (missingTexts.Add(block.Text))
            {
                missing.Add(block);
            }
        }

        if (missing.Count > 0)
        {
            var translated = await translateMissing(missing);
            if (translated.Count != missing.Count)
                throw new InvalidOperationException("The translation provider returned a different number of blocks.");

            // Publish only after the entire provider batch succeeds. A failed capture cannot leave
            // partial answers in the cache and make its next attempt look successful.
            for (var i = 0; i < missing.Count; i++)
            {
                var text = missing[i].Text;
                var answer = translated[i].TranslatedText;
                answers[text] = answer;
                Set(new Key(provider, sourceLanguage, targetLanguage, text), answer);
            }
        }

        // Geometry belongs to this screenshot, even when its text was translated in an earlier one.
        var result = blocks.Select(block => new TranslatedBlock(
            block.Text, answers[block.Text], block.Bounds, block.Lines, block.RenderGlyphHeight)
        {
            RunsAcross = block.RunsAcross
        }).ToList();
        return (result, hits, missing.Count);
    }

    private bool TryGet(Key key, out string translation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _recent.Remove(entry.Node);
                _recent.AddLast(entry.Node);
                translation = entry.Translation;
                return true;
            }
        }

        translation = "";
        return false;
    }

    private void Set(Key key, string translation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _recent.Remove(existing.Node);
                _entries.Remove(key);
            }

            var node = _recent.AddLast(key);
            _entries.Add(key, new Entry(translation, node));
            while (_entries.Count > Capacity)
            {
                var oldest = _recent.First!;
                _entries.Remove(oldest.Value);
                _recent.RemoveFirst();
            }
        }
    }
}
