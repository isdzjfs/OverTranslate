namespace OverTranslate.Services;

/// <summary>
/// Carries a screenshot translation's log ID across OCR and parallel translator tasks without
/// adding diagnostic parameters to every translation provider.
/// </summary>
internal static class CaptureTranslationDiagnostics
{
    private static readonly AsyncLocal<long?> Current = new();

    public static long? RequestId => Current.Value;

    public static IDisposable Begin(long requestId)
    {
        var previous = Current.Value;
        Current.Value = requestId;
        return new Scope(previous);
    }

    private sealed class Scope(long? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
