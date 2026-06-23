namespace HoaVoting.Api.Services.Vocdoni;

public interface IVocdoniClient
{
    Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default);

    Task<AddMembersResponse> AddMembersAsync(string orgAddress, List<VocdoniOrgMember> members, CancellationToken ct = default);
    Task<List<VocdoniOrgMember>> ListMembersAsync(string orgAddress, CancellationToken ct = default);
    Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default);

    /// <summary>Creates a member group containing all current org members; returns its id.</summary>
    Task<string> CreateAllMembersGroupAsync(string orgAddress, string title, CancellationToken ct = default);

    Task<string> CreateCensusAsync(string orgAddress, List<string> authFields, List<string> twoFaFields, CancellationToken ct = default);

    /// <summary>
    /// Publishes a census against a member group. Unlike the plain publish, this path supports
    /// auth-only (no-2FA) censuses and populates participants from the group.
    /// </summary>
    Task<PublishedCensusResponse> PublishCensusGroupAsync(
        string censusId, string groupId, List<string> authFields, List<string> twoFaFields, CancellationToken ct = default);

    Task<string> CreateProcessAsync(CreateProcessRequest request, CancellationToken ct = default);

    /// <summary>Publishes a draft process and returns its on-chain (Vochain) process id.</summary>
    Task<string> PublishProcessAsync(string draftProcessId, CancellationToken ct = default);
    Task SetProcessStatusAsync(string processId, string status, CancellationToken ct = default);
    Task<ProcessResultsResponse> GetResultsAsync(string processId, CancellationToken ct = default);
}
