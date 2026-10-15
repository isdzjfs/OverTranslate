using System.Net;
using System.Net.Http;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class GoogleConnectionKeepAliveTests
{
    [Theory]
    [InlineData(TranslationProvider.Google)]
    [InlineData(TranslationProvider.GoogleChrome)]
    public async Task Warms_the_translation_host_without_text_or_credentials(TranslationProvider provider)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var keepAlive = new GoogleConnectionKeepAlive(http, () => provider);

        // A 404 is expected for HEAD and still leaves a reusable connection.
        await keepAlive.TickAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Equal("https://translate-pa.googleapis.com/v1/translateHtml", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Content);
        Assert.Empty(request.Headers);
        Assert.True(handler.LastResponse!.IsDisposed);
    }

    [Theory]
    [InlineData(TranslationProvider.Google2)] // The saved name Google2 is the separate RPC engine.
    [InlineData(TranslationProvider.GoogleWeb)]
    [InlineData(TranslationProvider.Bing)]
    [InlineData(TranslationProvider.Microsoft)]
    [InlineData(TranslationProvider.DeepL)]
    [InlineData(TranslationProvider.OpenAI)]
    [InlineData(TranslationProvider.Youdao)]
    public async Task Other_selected_engines_do_not_send_google_probes(TranslationProvider provider)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await new GoogleConnectionKeepAlive(http, () => provider).TickAsync(CancellationToken.None);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rechecks_provider_and_resumes_after_a_network_failure()
    {
        var selected = TranslationProvider.Google;
        using var handler = new RecordingHandler { FailNext = true };
        using var http = new HttpClient(handler);
        var keepAlive = new GoogleConnectionKeepAlive(http, () => selected);

        await keepAlive.TickAsync(CancellationToken.None);
        Assert.Single(handler.Requests);
        selected = TranslationProvider.Microsoft;
        await keepAlive.TickAsync(CancellationToken.None);
        Assert.Single(handler.Requests);
        selected = TranslationProvider.GoogleChrome;
        await keepAlive.TickAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Shutdown_cancels_an_inflight_probe_and_finishes_the_loop()
    {
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        using var cancel = new CancellationTokenSource();
        var keepAlive = new GoogleConnectionKeepAlive(http, () => TranslationProvider.Google);

        var run = keepAlive.RunAsync(cancel.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, handler.RequestCount);
        Assert.True(handler.WasCancelled);
    }

    [Fact]
    public async Task A_stalled_probe_has_its_own_short_timeout()
    {
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var keepAlive = new GoogleConnectionKeepAlive(http, () => TranslationProvider.Google);

        await keepAlive.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6));

        Assert.True(handler.WasCancelled);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task An_already_cancelled_loop_does_not_send_anything()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await new GoogleConnectionKeepAlive(http, () => TranslationProvider.Google)
            .RunAsync(new CancellationToken(canceled: true));
        Assert.Empty(handler.Requests);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public bool FailNext { get; set; }
        public TrackedContent? LastResponse { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request);
            if (FailNext)
            {
                FailNext = false;
                throw new HttpRequestException("offline");
            }

            LastResponse = new TrackedContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = LastResponse });
        }
    }

    private sealed class TrackedContent() : StringContent("")
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }
        public bool WasCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("A probe must be cancelled.");
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
