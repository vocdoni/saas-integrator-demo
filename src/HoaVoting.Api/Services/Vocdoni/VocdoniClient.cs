using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HoaVoting.Api.Services.Vocdoni;

/// <summary>Thrown when the Vocdoni API returns a non-success status.</summary>
public sealed class VocdoniApiException(HttpStatusCode status, string body)
    : Exception($"Vocdoni API returned {(int)status} {status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;
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

    // --- multi-question /processes API (saas-backend #571) -----------------

    /// <summary>Creates a draft voting process (container with N questions); returns its 24-hex ProcessID.</summary>
    public async Task<string> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
    {
        var resp = await SendAsync<CreateVotingProcessResponse>(HttpMethod.Post, "/processes", request, ct);
        if (string.IsNullOrEmpty(resp.ProcessId))
            throw new VocdoniApiException(HttpStatusCode.BadGateway, "POST /processes returned no processId");
        return resp.ProcessId!;
    }

    /// <summary>
    /// Publishes every question of a process on-chain as one atomic batch and waits for the job. Each
    /// question becomes its own election; read the process back to get the per-question upstream ids.
    /// </summary>
    public async Task PublishVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Post, $"/processes/{processId}/publish", null, ct);
        if (resp.StatusCode == HttpStatusCode.OK) return; // 200 = already published (idempotent)

        var enqueued = await ReadJsonAsync<EnqueuedResponse>(resp, ct);
        if (string.IsNullOrEmpty(enqueued.JobId))
            throw new VocdoniApiException(resp.StatusCode, "publish returned neither a result nor a jobId");
        await PollJobAsync(enqueued.JobId!, ct);
    }

    /// <summary>Reads a process fully hydrated — after publish each question carries its on-chain upstreamId + status.</summary>
    public Task<VotingProcessResponse> GetVotingProcessAsync(string processId, CancellationToken ct = default) =>
        SendAsync<VotingProcessResponse>(HttpMethod.Get, $"/processes/{processId}", null, ct);

    /// <summary>Reads per-question on-chain results (public; only once the process is published).</summary>
    public Task<VotingProcessResultsResponse> GetVotingProcessResultsAsync(string processId, CancellationToken ct = default) =>
        SendAsync<VotingProcessResultsResponse>(HttpMethod.Get, $"/processes/{processId}/results", null, ct);

    /// <summary>Changes question status (e.g. "ended"); a null/empty <paramref name="questionIds"/> targets all published questions.</summary>
    public async Task SetQuestionsStatusAsync(string processId, string status, List<string>? questionIds = null, CancellationToken ct = default)
    {
        var body = new SetQuestionsStatusRequest
        {
            Status = status,
            Questions = questionIds?.Select(id => new QuestionStatusId { Id = id }).ToList(),
        };
        using var resp = await SendCoreAsync(HttpMethod.Put, $"/processes/{processId}/questions/status", body, ct);
        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            var enqueued = await ReadJsonAsync<EnqueuedResponse>(resp, ct);
            if (!string.IsNullOrEmpty(enqueued.JobId))
                await PollJobAsync(enqueued.JobId!, ct);
        }
    }

    /// <summary>Polls GET /jobs/{id} until the async transaction completes; throws on failure or timeout.</summary>
    // ponytail: bounded poll (~40s), no webhook available. Move to a background worker for production.
    private async Task PollJobAsync(string jobId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var job = await SendAsync<JobStatusResponse>(HttpMethod.Get, $"/jobs/{jobId}", null, ct);
            if (job.Status == "completed")
                return;
            if (job.Status == "failed")
                throw new VocdoniApiException(HttpStatusCode.BadGateway, $"job {jobId} failed: {job.Error}");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new VocdoniApiException(HttpStatusCode.GatewayTimeout, $"job {jobId} did not complete within the timeout");
    }

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
