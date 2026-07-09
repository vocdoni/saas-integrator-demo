namespace HoaVoting.Api.Services.Vocdoni;

public interface IVocdoniClient
{
    Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default);

    /// <summary>Deletes a managed org and all its data; throws 409 if it has active on-chain elections.</summary>
    Task DeleteOrganizationAsync(string orgAddress, CancellationToken ct = default);

    Task<AddMembersResponse> AddMembersAsync(string orgAddress, List<VocdoniOrgMember> members, CancellationToken ct = default);
    Task<List<VocdoniOrgMember>> ListMembersAsync(string orgAddress, CancellationToken ct = default);
    Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default);

    // --- multi-question /processes API (saas-backend #571) -----------------

    /// <summary>Creates a draft voting process (container with N questions); returns its 24-hex ProcessID.</summary>
    Task<string> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default);

    /// <summary>Publishes every question of a process on-chain (async batch) and waits for the job.</summary>
    Task PublishVotingProcessAsync(string processId, CancellationToken ct = default);

    /// <summary>Reads a process fully hydrated — after publish each question carries its on-chain upstreamId + status.</summary>
    Task<VotingProcessResponse> GetVotingProcessAsync(string processId, CancellationToken ct = default);

    /// <summary>Changes question status (e.g. "ended"); null/empty questionIds ⇒ all published questions.</summary>
    Task SetQuestionsStatusAsync(string processId, string status, List<string>? questionIds = null, CancellationToken ct = default);
}
