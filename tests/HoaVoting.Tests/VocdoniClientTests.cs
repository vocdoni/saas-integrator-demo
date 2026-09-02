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
            """{"id":"p1","published":true,"questions":[{"id":"q1","upstreamId":"deadbeef","status":"ready"}]}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok-123"));

        var proc = await client.GetVotingProcessAsync("p1");

        Assert.Equal("deadbeef", proc.Questions[0].UpstreamId);
        Assert.Equal("/processes/p1", handler.LastPath);
        Assert.Equal("Bearer tok-123", handler.LastAuthorization);
        Assert.Equal(1, handler.Calls); // no login round-trip, no retry
    }

    [Fact]
    public async Task Surfaces_non_success_as_VocdoniApiException_without_retry()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"bad token"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok-123"));

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.GetVotingProcessAsync("p1"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal(1, handler.Calls); // surfaced immediately, not retried
    }

    [Fact]
    public async Task CreateVotingProcess_posts_to_processes_and_returns_id()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"processId":"6a42"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var id = await client.CreateVotingProcessAsync(new CreateVotingProcessRequest { OrgAddress = "0xabc" });

        Assert.Equal("6a42", id);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/processes", handler.LastPath);
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
    public async Task PublishVotingProcess_polls_job_until_complete()
    {
        // 202 enqueue at /processes/{id}/publish → poll the batch-publish job until it completes.
        var handler = new SequenceHandler(
            (HttpStatusCode.Accepted, """{"jobId":"job1"}"""),
            (HttpStatusCode.OK, """{"status":"completed"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.PublishVotingProcessAsync("proc1");

        Assert.Equal("/processes/proc1/publish", handler.Paths[0]);
        Assert.Equal("/jobs/job1", handler.Paths[^1]);
    }

    [Fact]
    public async Task PublishVotingProcess_returns_immediately_when_already_published()
    {
        // 200 idempotent path: no job poll needed.
        var handler = new SequenceHandler((HttpStatusCode.OK, """{"processId":"proc1"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.PublishVotingProcessAsync("proc1");
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublishVotingProcess_throws_when_job_fails()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.Accepted, """{"jobId":"job1"}"""),
            (HttpStatusCode.OK, """{"status":"failed","errors":["out of quota"]}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.PublishVotingProcessAsync("proc1"));
        Assert.Contains("out of quota", ex.Message);
    }

    [Fact]
    public async Task SetQuestionsStatus_puts_and_returns_without_polling()
    {
        // Fire-and-forget: enqueue the end (202) and return. The real status is reconciled from
        // the process read on the next hydrate, so we must NOT poll /jobs here.
        var handler = new SequenceHandler(
            (HttpStatusCode.Accepted, """{"jobId":"j1"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.SetQuestionsStatusAsync("proc1", "ended");

        Assert.Equal(1, handler.Calls);
        Assert.Equal("/processes/proc1/questions/status", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task GetSubscriptionFeatures_reads_plan_features()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"plan":{"features":{"anonymous":true,"overwrite":false}}}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var features = await client.GetSubscriptionFeaturesAsync("0xabc");

        Assert.True(features.Anonymous);
        Assert.Equal("/organizations/0xabc/subscription", handler.LastPath);
    }

    [Fact]
    public async Task GetSubscriptionFeatures_reads_missing_plan_as_all_off()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"plan":null}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        Assert.False((await client.GetSubscriptionFeaturesAsync("0xabc")).Anonymous);
    }

    [Fact]
    public async Task GetSubscriptionFeatures_resolves_via_managed_orgs_and_plans_on_403()
    {
        // The subscription read is JWT-only upstream (403/40157 for API keys). Positive confirmation
        // instead: managed-orgs list carries the org's planId; the public /plans catalog carries the
        // plan's features. Address matching is case-insensitive (hex).
        var handler = new SequenceHandler(
            (HttpStatusCode.Forbidden, """{"error":"API keys are not permitted for this endpoint","code":40157}"""),
            (HttpStatusCode.OK, """{"organizations":[{"address":"0xABC","subscription":{"planId":"prod_free"}}],"pagination":{"currentPage":1,"lastPage":1}}"""),
            (HttpStatusCode.OK, """[{"id":"prod_pro","features":{"anonymous":false}},{"id":"prod_free","features":{"anonymous":true}}]"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var features = await client.GetSubscriptionFeaturesAsync("0xabc");

        Assert.True(features.Anonymous);
        Assert.Equal(3, handler.Calls);
        Assert.Equal("/plans", handler.Paths[^1]);
    }

    [Fact]
    public async Task GetSubscriptionFeatures_reads_unknown_org_or_plan_as_all_off_on_403()
    {
        // Org missing from the managed list ⇒ no planId ⇒ all-off (never optimistic).
        var handler = new SequenceHandler(
            (HttpStatusCode.Forbidden, """{"code":40157}"""),
            (HttpStatusCode.OK, """{"organizations":[{"address":"0xother","subscription":{"planId":"prod_free"}}],"pagination":{"currentPage":1,"lastPage":1}}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        Assert.False((await client.GetSubscriptionFeaturesAsync("0xabc")).Anonymous);
        Assert.Equal(2, handler.Calls); // /plans never fetched without a planId
    }

    [Fact]
    public async Task CreateVotingProcess_sends_census_anonymous_only_when_set()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"processId":"6a42"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.CreateVotingProcessAsync(new CreateVotingProcessRequest
        {
            OrgAddress = "0xabc",
            Census = new CensusSpec { AuthFields = ["memberNumber"], Anonymous = true },
        });
        Assert.Contains("\"anonymous\":true", handler.LastBody);

        // A regular census omits the flag entirely (null → not serialized).
        await client.CreateVotingProcessAsync(new CreateVotingProcessRequest
        {
            OrgAddress = "0xabc",
            Census = new CensusSpec { AuthFields = ["memberNumber"] },
        });
        Assert.DoesNotContain("anonymous", handler.LastBody);
    }

    [Fact]
    public async Task SetQuestionCensus_puts_complete_list_and_returns_without_job()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"eligible":2,"added":1,"removed":0}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var res = await client.SetQuestionCensusAsync("p1", "q1", ["m1", "m2"]);

        Assert.Equal((2, 1, 0), (res.Eligible, res.Added, res.Removed));
        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal("/processes/p1/questions/q1/census", handler.LastPath);
        Assert.Contains("\"memberIds\":[\"m1\",\"m2\"]", handler.LastBody);
        Assert.Equal(1, handler.Calls); // no /jobs round trip on a plain 200
    }

    [Fact]
    public async Task SetQuestionCensus_polls_job_when_a_resize_was_enqueued()
    {
        // 202 + jobId: the list is committed but the on-chain census resize is async — wait for it.
        var handler = new SequenceHandler(
            (HttpStatusCode.Accepted, """{"jobId":"j1","eligible":0,"added":0,"removed":3}"""),
            (HttpStatusCode.OK, """{"status":"completed"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var res = await client.SetQuestionCensusAsync("p1", "q1", []);

        Assert.Equal(3, res.Removed);
        Assert.Equal("/jobs/j1", handler.Paths[^1]);
    }

    [Fact]
    public async Task SetQuestionCensus_surfaces_409_with_body()
    {
        // 40173: restricting would strip a voter the CSP already signed for. The body names them.
        var handler = new CapturingHandler(HttpStatusCode.Conflict,
            """{"code":40173,"error":"member already signed","data":{"signedMemberIds":["m2"]}}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.SetQuestionCensusAsync("p1", "q1", ["m1"]));

        Assert.Equal(HttpStatusCode.Conflict, ex.Status);
        Assert.Contains("signedMemberIds", ex.Body);
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
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
