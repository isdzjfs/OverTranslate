using System.Net;
using System.Net.Http;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Translation;
using Xunit;

namespace OverTranslate.Tests;

public class SelectedProviderRoutingTests
{
    [Theory]
    [InlineData(TranslationProvider.Google, "/v1/translateHtml")]
    [InlineData(TranslationProvider.GoogleChrome, "/v1/translateHtml")]
    [InlineData(TranslationProvider.GoogleWeb, "/translate_a/t")]
    [InlineData(TranslationProvider.Google2, "/_/TranslateWebserverUi/data/batchexecute")]
    [InlineData(TranslationProvider.Microsoft, "/translate")]
    public async Task HttpFailure_UsesOnlyTheSelectedEndpointOnce(TranslationProvider provider, string path)
    {
        using var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var service = new TranslationService(http, () => "test-key", _ => { });

        var error = await Assert.ThrowsAsync<TranslationEngineException>(() => service.TranslateAsync(
            [new OcrTextBlock("Hello world", new Rect())], "EN", "ZH-HANS", "", engine: provider));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(path, Assert.Single(handler.Requests).AbsolutePath);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }
    }
}
