namespace HoaVoting.Api.Services.Vocdoni;

public interface IVocdoniClient
{
    Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default);

    /// <summary>Deletes a managed org and all its data; throws 409 if it has active on-chain elections.</summary>
    Task DeleteOrganizationAsync(string orgAddress, CancellationToken ct = default);

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

    /// <summary>Creates a process and returns its 24-hex ProcessID (the handle for status/results/metadata).</summary>
    Task<string> CreateProcessAsync(CreateProcessRequest request, CancellationToken ct = default);

    /// <summary>
    /// Publishes a process on-chain and returns its on-chain (Vochain) election id — used only to bundle
    /// the process for voting, not for status/results (those use the ProcessID). See saas-backend #551.
    /// </summary>
    Task<string> PublishProcessAsync(string processId, CancellationToken ct = default);

    /// <summary>Creates a process bundle from the census + on-chain election ids; returns the bundle id.</summary>
    Task<string> CreateBundleAsync(string censusId, List<string> electionIds, CancellationToken ct = default);

    /// <summary>Changes a process status (by ProcessID).</summary>
    Task SetProcessStatusAsync(string processId, string status, CancellationToken ct = default);

    /// <summary>Reads a process tally (by ProcessID).</summary>
    Task<ProcessResultsResponse> GetResultsAsync(string processId, CancellationToken ct = default);
}
