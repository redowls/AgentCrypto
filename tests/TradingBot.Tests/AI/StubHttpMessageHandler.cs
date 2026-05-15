using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace TradingBot.Tests.AI;

/// HttpMessageHandler that lets the test inspect every outgoing request and
/// hand back a configurable JSON body. Used in place of HttpClientFactory so
/// the IClaudeClient suite can run without network access.
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage>  Requests   { get; } = new();
    public List<string>              RequestBodies { get; } = new();

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Responder { get; set; }

    public int CallCount => Requests.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            RequestBodies.Add(body);
        }
        else
        {
            RequestBodies.Add(string.Empty);
        }

        if (Responder is null) throw new InvalidOperationException("StubHttpMessageHandler.Responder not configured");
        return await Responder(request).ConfigureAwait(false);
    }

    public static HttpResponseMessage JsonOk(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "") => new(code)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

/// Minimal IHttpClientFactory implementation that hands out one HttpClient
/// configured with the supplied StubHttpMessageHandler.
internal sealed class StubHttpClientFactory(StubHttpMessageHandler handler, string baseAddress = "https://api.test.local")
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseAddress),
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
