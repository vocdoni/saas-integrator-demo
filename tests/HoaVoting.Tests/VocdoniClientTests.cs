using System.Net;
using System.Text;
using HoaVoting.Api.Services.Vocdoni;
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
        var client = new VocdoniClient(ClientWithToken(handler, "tok-123"));

        var results = await client.GetResultsAsync("abc123");

        Assert.Equal(3, results.VoteCount);
        Assert.Equal("Bearer tok-123", handler.LastAuthorization);
        Assert.Equal(1, handler.Calls); // no login round-trip, no retry
    }

    [Fact]
    public async Task Surfaces_non_success_as_VocdoniApiException_without_retry()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"bad token"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok-123"));

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.GetResultsAsync("abc123"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal(1, handler.Calls); // surfaced immediately, not retried
    }

    [Fact]
    public async Task CreateOrganization_posts_to_path_less_integrator_endpoint()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"address":"0xabc"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok-123"));

        var addr = await client.CreateOrganizationAsync("My HOA");

        Assert.Equal("0xabc", addr);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/integrator/organizations", handler.LastPath);
    }

    [Fact]
    public async Task DeleteOrganization_deletes_managed_org_and_surfaces_409()
    {
        var okHandler = new CapturingHandler(HttpStatusCode.OK, """{"address":"0xabc"}""");
        var client = new VocdoniClient(ClientWithToken(okHandler, "tok-123"));

        await client.DeleteOrganizationAsync("0xabc");
        Assert.Equal(HttpMethod.Delete, okHandler.LastMethod);
        Assert.Equal("/integrator/organizations/0xabc", okHandler.LastPath);

        // An org with active elections surfaces the backend's 409.
        var conflict = new CapturingHandler(HttpStatusCode.Conflict, """{"error":"active elections"}""");
        var client2 = new VocdoniClient(ClientWithToken(conflict, "tok-123"));
        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client2.DeleteOrganizationAsync("0xabc"));
        Assert.Equal(HttpStatusCode.Conflict, ex.Status);
    }

    [Fact]
    public async Task ListMembers_walks_every_page()
    {
        // Page 1 of 2 (currentPage < lastPage) then page 2 (last). Without this the client stops at 10.
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"members":[{"id":"1"},{"id":"2"}],"pagination":{"currentPage":1,"lastPage":2}}"""),
            (HttpStatusCode.OK, """{"members":[{"id":"3"}],"pagination":{"currentPage":2,"lastPage":2}}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var members = await client.ListMembersAsync("0xabc");

        Assert.Equal(new[] { "1", "2", "3" }, members.Select(m => m.Id));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task PublishProcess_polls_job_until_complete()
    {
        // 202 enqueue → poll the job until it completes. Publish returns nothing (single-ProcessID model).
        var handler = new SequenceHandler(
            (HttpStatusCode.Accepted, """{"jobId":"job1"}"""),
            (HttpStatusCode.OK, """{"status":"completed"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.PublishProcessAsync("proc1");

        Assert.Equal("/jobs/job1", handler.Paths[^1]);
    }

    [Fact]
    public async Task PublishProcess_returns_immediately_when_already_published()
    {
        // 200 idempotent path: no job poll needed.
        var handler = new SequenceHandler((HttpStatusCode.OK, """{"address":"cafe","status":"READY"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.PublishProcessAsync("proc1");
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublishProcess_throws_when_job_fails()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.Accepted, """{"jobId":"job1"}"""),
            (HttpStatusCode.OK, """{"status":"failed","error":"out of quota"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.PublishProcessAsync("proc1"));
        Assert.Contains("out of quota", ex.Message);
    }

    // Returns each queued response in order (last one repeats), recording every request path.
    private sealed class SequenceHandler(params (HttpStatusCode Code, string Body)[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, body) = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            Paths.Add(request.RequestUri?.AbsolutePath ?? "");
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastAuthorization { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
