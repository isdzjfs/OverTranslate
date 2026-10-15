using System.Diagnostics;
using System.Net.Http;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>Prepares Google2's connection and keeps it available between captures.</summary>
internal sealed class GoogleConnectionKeepAlive(HttpClient http, Func<TranslationProvider> selectedProvider)
{
    // Below SocketsHttpHandler's one-minute idle timeout. A real request also prepares a new
    // connection after the pool's five-minute lifetime or a server-side close, unlike a TCP ping.
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    internal const string Endpoint = "https://translate-pa.googleapis.com/v1/translateHtml";

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Complete each attempt before starting the delay: a slow/offline connection cannot
            // accumulate overlapping probes, and the first attempt runs at application startup.
            while (!cancellationToken.IsCancellationRequested)
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown, including a probe still in flight.
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedProvider() is not (TranslationProvider.Google or TranslationProvider.GoogleChrome))
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var started = Stopwatch.GetTimestamp();

        try
        {
            // HEAD returns 404 because this endpoint accepts POST. Its headers still establish
            // the same TLS/HTTP connection as translation, without a key, text or translation job.
            using var request = new HttpRequestMessage(HttpMethod.Head, Endpoint);
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            Log.Debug("Google2 connection warmup: HTTP {Status}, {Elapsed} ms",
                (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            // A background probe is best effort. Offline/timeout failures neither affect a real
            // translation nor cause immediate retries or a switch to another provider.
            Log.Debug("Google2 connection warmup failed: {Reason}", ex.GetType().Name);
        }
    }
}
