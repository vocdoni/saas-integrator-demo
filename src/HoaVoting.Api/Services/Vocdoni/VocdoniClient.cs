using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HoaVoting.Api.Services.Vocdoni;

/// <summary>Thrown when the Vocdoni API returns a non-success status.</summary>
public sealed class VocdoniApiException(HttpStatusCode status, string body)
    : Exception($"Vocdoni API returned {(int)status} {status}: {body}")
{
    private readonly (int? Code, string? Error, JsonElement? Data) _parsed = Parse(body);

    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;

    /// <summary>Upstream <c>errors.Error.code</c> (e.g. 40175); null when the body is not that shape.</summary>
    public int? Code => _parsed.Code;

    /// <summary>Upstream <c>errors.Error.error</c> text; null when absent.</summary>
    public string? UpstreamError => _parsed.Error;

    /// <summary>Upstream <c>errors.Error.data</c> (e.g. requiredCents/availableCents); null when absent. Not
    /// named Data: that would hide Exception.Data.</summary>
    public JsonElement? ErrorData => _parsed.Data;

    // Best-effort: the body is upstream input, so every read is guarded by ValueKind (JsonElement
    // getters throw InvalidOperationException on a mismatched kind) and non-JSON yields all-null.
    private static (int?, string?, JsonElement?) Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            int? code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            return (code, error, data);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>
/// Typed HttpClient over the Vocdoni SaaS API. The pre-provisioned API token is attached as the
/// default Authorization header when the client is registered (see Program.cs), so this class is
/// just request shaping + JSON. // ponytail: no login flow — one configured Bearer token.
/// </summary>
public sealed class VocdoniClient(HttpClient http) : IVocdoniClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates an association as a managed org under the integrator. The integrator org is resolved
    /// from the API key (path-less endpoint), so no address is sent. Requires the key's managed:write scope.
    /// </summary>
    public async Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default)
    {
        var body = new VocdoniOrganizationInfo
        {
            Type = "association",
            Meta = new Dictionary<string, object> { ["name"] = name },
        };
        var org = await SendAsync<VocdoniOrganizationInfo>(
            HttpMethod.Post, "/integrator/organizations", body, ct);
        if (string.IsNullOrEmpty(org.Address))
            throw new InvalidOperationException("Vocdoni did not return an organization address.");
        return org.Address;
    }

    /// <summary>Deletes a managed org and all its data. 409 if it has active on-chain elections.</summary>
    public Task DeleteOrganizationAsync(string orgAddress, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/integrator/organizations/{orgAddress}", null, ct);

    public Task<AddMembersResponse> AddMembersAsync(string orgAddress, List<VocdoniOrgMember> members, CancellationToken ct = default) =>
        SendAsync<AddMembersResponse>(HttpMethod.Post, $"/organizations/{orgAddress}/members",
            new AddMembersRequest { Members = members }, ct);

    public async Task<List<VocdoniOrgMember>> ListMembersAsync(string orgAddress, CancellationToken ct = default)
    {
        // The list is paginated (default limit 10); walk every page so large memberbases aren't truncated.
        var all = new List<VocdoniOrgMember>();
        for (var page = 1; ; page++)
        {
            var resp = await SendAsync<OrganizationMembersResponse>(
                HttpMethod.Get, $"/organizations/{orgAddress}/members?page={page}&limit=100", null, ct);
            all.AddRange(resp.Members);
            if (resp.Members.Count == 0 || resp.Pagination is not { } p || p.CurrentPage >= p.LastPage)
                break;
        }
        return all;
    }

    public Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default) =>
        // Note: the route is plural /members (the swagger's singular /member 404s on the backend).
        SendAsync(HttpMethod.Delete, $"/organizations/{orgAddress}/members",
            new DeleteMembersRequest { Ids = memberIds }, ct);

    /// <summary>PUT /organizations/{org}/members: upsert one member. Fields left null are omitted on the wire
    /// and keep their stored values. NB: an unused id CREATES a member — callers check it exists first.</summary>
    public Task UpsertMemberAsync(string orgAddress, VocdoniOrgMember member, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/organizations/{orgAddress}/members", member, ct);

    // --- multi-question /processes API (saas-backend #571) -----------------

    /// <summary>Creates a draft voting process (container with N questions): its 24-hex ProcessID plus
    /// <c>missingData</c>, the members left out of the census for lacking a required auth field.</summary>
    public async Task<CreateVotingProcessResponse> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
    {
        var resp = await SendAsync<CreateVotingProcessResponse>(HttpMethod.Post, "/processes", request, ct);
        if (string.IsNullOrEmpty(resp.ProcessId))
            throw new VocdoniApiException(HttpStatusCode.BadGateway, "POST /processes returned no processId");
        return resp;
    }

    /// <summary>
    /// Starts publishing every question of a process on-chain as one batch (pay-per-process: this is
    /// the call that debits the wallet). Returns the job id to follow, or null when upstream answered
    /// 200 (already published). Does not wait: the job can stay pending for minutes to hours.
    /// </summary>
    public async Task<string?> PublishVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Post, $"/processes/{processId}/publish", null, ct);
        if (resp.StatusCode == HttpStatusCode.OK) return null; // 200 = already published (idempotent)

        var enqueued = await ReadJsonAsync<EnqueuedResponse>(resp, ct);
        if (string.IsNullOrEmpty(enqueued.JobId))
            throw new VocdoniApiException(resp.StatusCode, "publish returned neither a result nor a jobId");
        return enqueued.JobId;
    }

    /// <summary>DELETE /processes/{id}: deletes an unpublished draft (credits back a paid one). 409 once
    /// a publish submitted elections (40903/40904), 404 when already gone.</summary>
    public Task DeleteVotingProcessAsync(string processId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/processes/{processId}", null, ct);

    /// <summary>Reads a process fully hydrated — each question carries its on-chain upstreamId + status, and
    /// (saas-backend #596) an inline tally once it reaches "results" status.</summary>
    public Task<VotingProcessResponse> GetVotingProcessAsync(string processId, CancellationToken ct = default) =>
        SendAsync<VotingProcessResponse>(HttpMethod.Get, $"/processes/{processId}", null, ct);

    /// <summary>Changes question status (e.g. "ended"); a null/empty <paramref name="questionIds"/> targets all published questions.</summary>
    // ponytail: fire-and-forget — we enqueue the status change and return without waiting for the
    // async job, so the caller's request stays fast. The real per-question status is reconciled from
    // GET /processes/{id}/results on the next read (ProposalsController.HydrateAsync). A rejected
    // enqueue still surfaces: SendCoreAsync throws VocdoniApiException on any non-2xx.
    public Task SetQuestionsStatusAsync(string processId, string status, List<string>? questionIds = null, CancellationToken ct = default)
    {
        var body = new SetQuestionsStatusRequest
        {
            Status = status,
            Questions = questionIds?.Select(id => new QuestionStatusId { Id = id }).ToList(),
        };
        return SendAsync(HttpMethod.Put, $"/processes/{processId}/questions/status", body, ct);
    }

    /// <summary>Replaces a question's eligibility list (saas-backend #621); [] reopens it to the whole census.
    /// A 202 means an on-chain census resize was enqueued — the list is already committed, so we just wait
    /// for the job before returning. Upstream 409 (already-signed voters) throws VocdoniApiException.</summary>
    public async Task<UpdateQuestionCensusResponse> SetQuestionCensusAsync(string processId, string questionId, List<string> memberIds, CancellationToken ct = default)
    {
        var resp = await SendAsync<UpdateQuestionCensusResponse>(HttpMethod.Put,
            $"/processes/{processId}/questions/{questionId}/census",
            new UpdateQuestionCensusRequest { MemberIds = memberIds }, ct);
        if (!string.IsNullOrEmpty(resp.JobId))
            await PollJobAsync(resp.JobId!, ct);
        return resp;
    }

    /// <summary>PUT /processes/{id}/endDate (saas-backend #844): extend every open question's end; returns the job id.</summary>
    public async Task<string> SetProcessEndDateAsync(string processId, DateTimeOffset endDate, CancellationToken ct = default)
    {
        var body = new SetProcessEndDateRequest
        {
            // RFC 3339 in UTC; upstream truncates to whole seconds anyway.
            EndDate = endDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        var enqueued = await SendAsync<EnqueuedResponse>(HttpMethod.Put, $"/processes/{processId}/endDate", body, ct);
        return string.IsNullOrEmpty(enqueued.JobId)
            ? throw new VocdoniApiException(HttpStatusCode.BadGateway, "endDate change returned no jobId")
            : enqueued.JobId!;
    }

    // --- jobs ----------------------------------------------------------------

    /// <summary>One non-polling read of GET /jobs/{id} (the reconcile uses it).</summary>
    public Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default) =>
        SendAsync<JobStatusResponse>(HttpMethod.Get, $"/jobs/{jobId}", null, ct);

    /// <summary>Delay between job polls. Tests set it to zero.</summary>
    internal TimeSpan PollDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Polls the job up to 20 times; Pending when it is still running after that. Error = the job's errors joined.</summary>
    // ponytail: bounded poll (~40 s, under nginx's 60 s proxy timeout), no webhook available. Move to a
    // background worker for production; until then the read-side reconcile settles what outlives it.
    public async Task<(JobState State, string? Error)> WaitForJobAsync(string jobId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (attempt > 0) await Task.Delay(PollDelay, ct);
            var job = await GetJobAsync(jobId, ct);
            if (job.Status == "completed") return (JobState.Completed, null);
            if (job.Status == "failed") return (JobState.Failed, string.Join("; ", job.Errors ?? []));
        }
        return (JobState.Pending, null);
    }

    /// <summary>Waits for the job and throws unless it completed (callers that need the outcome inline).</summary>
    private async Task PollJobAsync(string jobId, CancellationToken ct)
    {
        var (state, error) = await WaitForJobAsync(jobId, ct);
        if (state == JobState.Failed)
            throw new VocdoniApiException(HttpStatusCode.BadGateway, $"job {jobId} failed: {error}");
        if (state == JobState.Pending)
            throw new VocdoniApiException(HttpStatusCode.GatewayTimeout, $"job {jobId} did not complete within the timeout");
    }

    // --- pricing + integrator wallet (saas-backend #819) -----------------------

    /// <summary>GET /pricing?voters=N — the public pay-per-process calculator (voters ≥ 1).</summary>
    public Task<ProcessPrice> GetPriceAsync(int voters, CancellationToken ct = default) =>
        SendAsync<ProcessPrice>(HttpMethod.Get, $"/pricing?voters={voters}", null, ct);

    /// <summary>GET /integrator — whether another managed org may be created now (quota:read).</summary>
    public Task<IntegratorInfo> GetIntegratorInfoAsync(CancellationToken ct = default) =>
        SendAsync<IntegratorInfo>(HttpMethod.Get, "/integrator", null, ct);

    /// <summary>The integrator org's address: the <c>managedBy</c> of its first managed org (managed:read);
    /// null when it manages none yet.</summary>
    public async Task<string?> GetIntegratorAddressAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync<ManagedOrganizationsResponse>(HttpMethod.Get, "/integrator/organizations?page=1&limit=1", null, ct);
        return resp.Organizations.FirstOrDefault()?.ManagedBy is { Length: > 0 } addr ? addr : null;
    }

    /// <summary>GET /organizations/{org}/wallet — balance only (one history row requested). quota:read of that org.</summary>
    public Task<WalletInfo> GetWalletAsync(string orgAddress, CancellationToken ct = default) =>
        SendAsync<WalletInfo>(HttpMethod.Get, $"/organizations/{orgAddress}/wallet?page=1&limit=1", null, ct);

    // --- transport ---------------------------------------------------------

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var resp = await SendCoreAsync(method, path, body, ct);
        return await ReadJsonAsync<T>(resp, ct);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
        return value ?? throw new VocdoniApiException(resp.StatusCode, "empty response body");
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var _ = await SendCoreAsync(method, path, body, ct);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: Json);

        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var status = resp.StatusCode;
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new VocdoniApiException(status, errBody);
        }
        return resp;
    }
}
