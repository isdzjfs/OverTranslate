namespace OverTranslate.Services.Realtime;

/// <summary>Prevents unchanged failed text from being requested again by the polling loop.</summary>
internal sealed class RealtimeTranslationFailureGate
{
    private readonly object _gate = new();
    private string[]? _failedText;
    private int _generation;

    public bool ShouldSkip(IEnumerable<string> text, int generation)
    {
        lock (_gate)
        {
            if (_failedText is null) return false;
            if (_generation == generation && _failedText.SequenceEqual(text, StringComparer.Ordinal))
                return true;

            // A new reading or an explicit resume is a new request, not a failure retry.
            _failedText = null;
            return false;
        }
    }

    public void Record(IEnumerable<string> text, int generation)
    {
        lock (_gate)
        {
            _failedText = text.ToArray();
            _generation = generation;
        }
    }
}
