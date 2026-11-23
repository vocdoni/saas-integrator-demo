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

        var created = await client.CreateVotingProcessAsync(new CreateVotingProcessRequest { OrgAddress = "0xabc" });

        Assert.Equal("6a42", created.ProcessId);
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
    public async Task CreateVotingProcess_returns_missing_data()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"processId":"6a42","missingData":["m3"]}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var created = await client.CreateVotingProcessAsync(new CreateVotingProcessRequest { OrgAddress = "0xabc" });

        Assert.Equal(["m3"], created.MissingData!);
    }

    [Fact]
    public async Task PublishVotingProcess_returns_the_job_without_polling()
    {
        // 202 enqueue → hand the job id back; the caller decides how long to wait (pay-per-process
        // publish can stay pending for hours).
        var handler = new SequenceHandler((HttpStatusCode.Accepted, """{"jobId":"job1"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        Assert.Equal("job1", await client.PublishVotingProcessAsync("proc1"));
        Assert.Equal(["/processes/proc1/publish"], handler.Paths);
    }

    [Fact]
    public async Task PublishVotingProcess_returns_null_when_already_published()
    {
        var handler = new SequenceHandler((HttpStatusCode.OK, """{"processId":"proc1"}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        Assert.Null(await client.PublishVotingProcessAsync("proc1"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublishVotingProcess_surfaces_a_refusal_with_its_code()
    {
        var handler = new SequenceHandler((HttpStatusCode.PaymentRequired,
            """{"error":"insufficient balance","code":40175,"data":{"requiredCents":1980,"availableCents":0}}"""));
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.PublishVotingProcessAsync("proc1"));
        Assert.Equal(40175, ex.Code);
        Assert.Equal(1980, ex.ErrorData!.Value.GetProperty("requiredCents").GetInt32());
    }

    [Fact]
    public async Task DeleteVotingProcess_deletes_the_draft()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "\"ok\"");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.DeleteVotingProcessAsync("proc1");

        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal("/processes/proc1", handler.LastPath);
    }

    [Fact]
    public async Task WaitForJob_reports_completed_failed_and_pending()
    {
        var done = new VocdoniClient(ClientWithToken(new SequenceHandler(
            (HttpStatusCode.OK, """{"status":"pending"}"""),
            (HttpStatusCode.OK, """{"status":"completed"}""")), "tok")) { PollDelay = TimeSpan.Zero };
        Assert.Equal((JobState.Completed, null), await done.WaitForJobAsync("j1"));

        var failed = new VocdoniClient(ClientWithToken(new SequenceHandler(
            (HttpStatusCode.OK, """{"status":"failed","errors":["out of funds","chain down"]}""")), "tok"));
        Assert.Equal((JobState.Failed, "out of funds; chain down"), await failed.WaitForJobAsync("j1"));

        var stuck = new SequenceHandler((HttpStatusCode.OK, """{"status":"pending"}"""));
        var pending = new VocdoniClient(ClientWithToken(stuck, "tok")) { PollDelay = TimeSpan.Zero };
        Assert.Equal((JobState.Pending, null), await pending.WaitForJobAsync("j1"));
        Assert.Equal(20, stuck.Calls); // bounded poll, then give up without throwing
    }

    [Fact]
    public async Task SetProcessEndDate_puts_rfc3339_utc_and_returns_the_job()
    {
        var handler = new CapturingHandler(HttpStatusCode.Accepted, """{"jobId":"j9"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var job = await client.SetProcessEndDateAsync("p1", new DateTimeOffset(2026, 12, 31, 23, 59, 59, 500, TimeSpan.FromHours(2)));

        Assert.Equal("j9", job);
        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal("/processes/p1/endDate", handler.LastPath);
        Assert.Equal("""{"endDate":"2026-12-31T21:59:59Z"}""", handler.LastBody);
    }

    [Fact]
    public async Task GetPrice_forwards_the_voter_count()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"lines":[{"kind":"base","description":"30 voters","amountCents":1980}],"totalCents":1980,"currency":"eur","quoteRecommended":false,"quoteRequired":false}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        var price = await client.GetPriceAsync(30);

        Assert.Equal((1980, "eur"), (price.TotalCents, price.Currency));
        Assert.Equal("/pricing?voters=30", handler.LastPathAndQuery);
    }

    [Fact]
    public async Task Integrator_address_is_managedBy_of_the_first_managed_org()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"organizations":[{"address":"0xorg","managedBy":"0xintegrator"}],"pagination":{"currentPage":1,"lastPage":3}}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        Assert.Equal("0xintegrator", await client.GetIntegratorAddressAsync());
        Assert.Equal("/integrator/organizations?page=1&limit=1", handler.LastPathAndQuery);

        var none = new VocdoniClient(ClientWithToken(new CapturingHandler(HttpStatusCode.OK, """{"organizations":[]}"""), "tok"));
        Assert.Null(await none.GetIntegratorAddressAsync());
    }

    [Fact]
    public async Task Integrator_info_and_wallet_read_their_fields()
    {
        var info = await new VocdoniClient(ClientWithToken(new CapturingHandler(HttpStatusCode.OK,
            """{"enabled":true,"canCreateManagedOrg":false,"managedOrgsCreated":1,"netTopUpCents":0}"""), "tok")).GetIntegratorInfoAsync();
        Assert.False(info.CanCreateManagedOrg);
        Assert.Equal(1, info.ManagedOrgsCreated);

        var handler = new CapturingHandler(HttpStatusCode.OK, """{"balanceCents":12345,"currency":"eur","history":[]}""");
        var wallet = await new VocdoniClient(ClientWithToken(handler, "tok")).GetWalletAsync("0xintegrator");
        Assert.Equal((12345L, "eur"), (wallet.BalanceCents, wallet.Currency));
        Assert.Equal("/organizations/0xintegrator/wallet?page=1&limit=1", handler.LastPathAndQuery);
    }

    [Fact]
    public async Task UpsertMember_sends_only_id_and_password()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"id":"m1"}""");
        var client = new VocdoniClient(ClientWithToken(handler, "tok"));

        await client.UpsertMemberAsync("0xabc", new VocdoniOrgMember { Id = "m1", Password = new string('a', 64) });

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal("/organizations/0xabc/members", handler.LastPath);
        Assert.Equal($$"""{"id":"m1","password":"{{new string('a', 64)}}"}""", handler.LastBody);
    }

    [Fact]
    public async Task ListMembers_reads_hasPassword()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"members":[{"id":"1","hasPassword":true},{"id":"2"}]}""");
        var members = await new VocdoniClient(ClientWithToken(handler, "tok")).ListMembersAsync("0xabc");

        Assert.Equal([true, false], members.Select(m => m.HasPassword));
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
        public string? LastPathAndQuery { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            LastPathAndQuery = request.RequestUri?.PathAndQuery;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
