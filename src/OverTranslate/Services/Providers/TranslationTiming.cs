namespace OverTranslate.Services.Providers;

/// <summary>Timeout for one translation request; a failure is returned without automatic retry.</summary>
public static class TranslationTiming
{
    public static readonly TimeSpan Request = TimeSpan.FromSeconds(10);
}
