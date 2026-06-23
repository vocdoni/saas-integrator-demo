using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

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
public sealed class VocdoniClient(HttpClient http, IOptions<VocdoniOptions> options) : IVocdoniClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly VocdoniOptions _o = options.Value;

    /// <summary>
    /// Creates an association as a managed org under the integrator. API keys are not permitted on
    /// POST /organizations, but the integrator endpoint accepts them.
    /// </summary>
    public async Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_o.IntegratorAddress))
            throw new InvalidOperationException("Vocdoni:IntegratorAddress is not configured.");

        var body = new VocdoniOrganizationInfo
        {
            Type = "association",
            ProvisionAccount = true,
            Meta = new Dictionary<string, object> { ["name"] = name },
        };
        var org = await SendAsync<VocdoniOrganizationInfo>(
            HttpMethod.Post, $"/organizations/{_o.IntegratorAddress}/managed", body, ct);
        if (string.IsNullOrEmpty(org.Address))
            throw new InvalidOperationException("Vocdoni did not return an organization address.");
        return org.Address;
    }

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
        // POST /process returns the process id as a bare JSON string.
        await SendAsync<string>(HttpMethod.Post, "/process", request, ct);

    public async Task<string> PublishProcessAsync(string draftProcessId, CancellationToken ct = default)
    {
        // Publish is async: it returns a jobId (202) and the on-chain election id is assigned to the
        // process's `address` a little later. Poll the draft until that address appears.
        // ponytail: simple bounded poll, not a job-status state machine.
        await SendAsync(HttpMethod.Post, $"/process/{draftProcessId}/publish", null, ct);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var proc = await SendAsync<ProcessDetail>(HttpMethod.Get, $"/process/{draftProcessId}", null, ct);
            if (!string.IsNullOrEmpty(proc.Address))
                return proc.Address!;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new VocdoniApiException(HttpStatusCode.GatewayTimeout,
            $"process {draftProcessId} was not assigned an on-chain id within the timeout");
    }

    public Task SetProcessStatusAsync(string processId, string status, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/process/{processId}/status", new SetProcessStatusRequest { Status = status }, ct);

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
