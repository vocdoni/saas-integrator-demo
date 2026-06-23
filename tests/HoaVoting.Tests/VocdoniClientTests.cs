using System.Net;
using System.Text;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaVoting.Tests;

public class VocdoniClientTests
{
    // Mirrors how Program.cs registers the typed client: the API token is the default Bearer header.
    private static HttpClient ClientWithToken(HttpMessageHandler handler, string token) =>
        new(handler)
        {
            BaseAddress = new Uri("https://vocdoni.test"),
            DefaultRequestHeaders = { Authorization = new("Bearer", token) },
        };

    [Fact]
    public async Task Sends_configured_api_token_as_bearer()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"status":"ended","finalResults":true,"voteCount":3,"results":[["2","1"]]}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok-123"), Options.Create(new VocdoniOptions()));

        var results = await client.GetResultsAsync("abc123");

        Assert.Equal(3, results.VoteCount);
        Assert.Equal("Bearer tok-123", handler.LastAuthorization);
        Assert.Equal(1, handler.Calls); // no login round-trip, no retry
    }

    [Fact]
    public async Task Surfaces_non_success_as_VocdoniApiException_without_retry()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"bad token"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok-123"), Options.Create(new VocdoniOptions()));

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.GetResultsAsync("abc123"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal(1, handler.Calls); // surfaced immediately, not retried
    }

    private sealed class CapturingHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
