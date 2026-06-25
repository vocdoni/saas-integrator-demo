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
        var resp = await SendAsync<OrganizationMembersResponse>(HttpMethod.Get, $"/organizations/{orgAddress}/members", null, ct);
        return resp.Members;
    }

    public Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default) =>
        // Note: the route is plural /members (the swagger's singular /member 404s on the backend).
        SendAsync(HttpMethod.Delete, $"/organizations/{orgAddress}/members",
            new DeleteMembersRequest { Ids = memberIds }, ct);

    public async Task<string> CreateAllMembersGroupAsync(string orgAddress, string title, CancellationToken ct = default)
    {
        var body = new CreateMemberGroupRequest { Title = title, IncludeAllMembers = true };
        var group = await SendAsync<MemberGroupInfo>(HttpMethod.Post, $"/organizations/{orgAddress}/groups", body, ct);
        return group.Id;
    }

    public async Task<string> CreateCensusAsync(string orgAddress, List<string> authFields, List<string> twoFaFields, CancellationToken ct = default)
    {
        var body = new CreateCensusRequest { OrgAddress = orgAddress, AuthFields = authFields, TwoFaFields = twoFaFields };
        var resp = await SendAsync<CreateCensusResponse>(HttpMethod.Post, "/census", body, ct);
        return resp.Id;
    }

    public Task<PublishedCensusResponse> PublishCensusGroupAsync(
        string censusId, string groupId, List<string> authFields, List<string> twoFaFields, CancellationToken ct = default) =>
        SendAsync<PublishedCensusResponse>(HttpMethod.Post, $"/census/{censusId}/group/{groupId}/publish",
            new PublishCensusGroupRequest { AuthFields = authFields, TwoFaFields = twoFaFields, Weighted = false }, ct);

    public async Task<string> CreateProcessAsync(CreateProcessRequest request, CancellationToken ct = default) =>
        // POST /process returns the 24-hex ProcessID as a bare JSON string. This is the handle for
        // status/results/metadata (saas-backend #551), so the caller persists it.
        await SendAsync<string>(HttpMethod.Post, "/process", request, ct);

    /// <summary>
    /// Publishes a process on-chain and returns its 64-hex on-chain election id. That id is only needed
    /// to wrap the process in a bundle for the CSP voting flow; status/results/metadata stay addressed by
    /// the 24-hex ProcessID (passed in here), not by this value — see saas-backend #551.
    /// </summary>
    public async Task<string> PublishProcessAsync(string processId, CancellationToken ct = default)
    {
        // Publish is async: it returns a jobId (202) and the on-chain election id is assigned to the
        // process's `address` a little later. Poll the process until that address appears.
        // ponytail: simple bounded poll, not a job-status state machine.
        await SendAsync(HttpMethod.Post, $"/process/{processId}/publish", null, ct);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var proc = await SendAsync<ProcessDetail>(HttpMethod.Get, $"/process/{processId}", null, ct);
            if (!string.IsNullOrEmpty(proc.Address))
                return proc.Address!;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new VocdoniApiException(HttpStatusCode.GatewayTimeout,
            $"process {processId} was not assigned an on-chain id within the timeout");
    }

    public async Task<string> CreateBundleAsync(string censusId, List<string> electionIds, CancellationToken ct = default)
    {
        var body = new CreateProcessBundleRequest { CensusId = censusId, Processes = electionIds };
        var resp = await SendAsync<CreateProcessBundleResponse>(HttpMethod.Post, "/process/bundle", body, ct);
        // The response gives the bundle URI ".../process/bundle/{bundleId}"; the id is the last segment.
        var id = resp.Uri?.TrimEnd('/').Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Vocdoni did not return a bundle URI.");
        return id;
    }

    /// <summary>Changes an election's status (e.g. "ended"). <paramref name="processId"/> is the 24-hex ProcessID (#551).</summary>
    public Task SetProcessStatusAsync(string processId, string status, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/process/{processId}/status", new SetProcessStatusRequest { Status = status }, ct);

    /// <summary>Reads an election's tally. <paramref name="processId"/> is the 24-hex ProcessID (#551), not the on-chain id.</summary>
    public Task<ProcessResultsResponse> GetResultsAsync(string processId, CancellationToken ct = default) =>
        SendAsync<ProcessResultsResponse>(HttpMethod.Get, $"/process/{processId}/results", null, ct);

    // --- transport ---------------------------------------------------------

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var resp = await SendCoreAsync(method, path, body, ct);
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
