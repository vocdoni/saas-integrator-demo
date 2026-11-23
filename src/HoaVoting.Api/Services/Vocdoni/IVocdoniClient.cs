namespace HoaVoting.Api.Services.Vocdoni;

public interface IVocdoniClient
{
    Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default);

    /// <summary>Deletes a managed org and all its data; throws 409 if it has active on-chain elections.</summary>
    Task DeleteOrganizationAsync(string orgAddress, CancellationToken ct = default);

    Task<AddMembersResponse> AddMembersAsync(string orgAddress, List<VocdoniOrgMember> members, CancellationToken ct = default);
    Task<List<VocdoniOrgMember>> ListMembersAsync(string orgAddress, CancellationToken ct = default);
    Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default);

    /// <summary>Upserts one member; null fields keep their stored values. An unused id creates a member.</summary>
    Task UpsertMemberAsync(string orgAddress, VocdoniOrgMember member, CancellationToken ct = default);

    // --- multi-question /processes API (saas-backend #571) -----------------

    /// <summary>Creates a draft voting process (container with N questions): processId + missingData.</summary>
    Task<CreateVotingProcessResponse> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default);

    /// <summary>Starts the on-chain batch publish; returns its job id, or null when already published (200). Does not wait.</summary>
    Task<string?> PublishVotingProcessAsync(string processId, CancellationToken ct = default);

    /// <summary>Deletes an unpublished draft; 409 once a publish submitted elections, 404 when gone.</summary>
    Task DeleteVotingProcessAsync(string processId, CancellationToken ct = default);

    /// <summary>Reads a process fully hydrated — each question carries its on-chain upstreamId + status, and
    /// (saas-backend #596) an inline tally once it reaches "results" status.</summary>
    Task<VotingProcessResponse> GetVotingProcessAsync(string processId, CancellationToken ct = default);

    /// <summary>Changes question status (e.g. "ended"); null/empty questionIds ⇒ all published questions.</summary>
    Task SetQuestionsStatusAsync(string processId, string status, List<string>? questionIds = null, CancellationToken ct = default);

    /// <summary>Replaces a question's eligibility list (saas-backend #621). Complete list, [] = open to the
    /// whole census; waits for the census-resize job when one is enqueued (202). Upstream 409 (code 40173,
    /// voter already signed) surfaces as a VocdoniApiException the caller can inspect.</summary>
    Task<UpdateQuestionCensusResponse> SetQuestionCensusAsync(string processId, string questionId, List<string> memberIds, CancellationToken ct = default);

    /// <summary>Extends the process end date (saas-backend #844); returns the job id.</summary>
    Task<string> SetProcessEndDateAsync(string processId, DateTimeOffset endDate, CancellationToken ct = default);

    /// <summary>One read of GET /jobs/{id}, no polling.</summary>
    Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>Bounded poll of a job: Completed, Failed (with the joined errors), or Pending when it outlived the poll.</summary>
    Task<(JobState State, string? Error)> WaitForJobAsync(string jobId, CancellationToken ct = default);

    // --- pricing + integrator wallet (saas-backend #819) -----------------------

    Task<ProcessPrice> GetPriceAsync(int voters, CancellationToken ct = default);
    Task<IntegratorInfo> GetIntegratorInfoAsync(CancellationToken ct = default);

    /// <summary>The integrator org address (managedBy of its first managed org); null when it manages none.</summary>
    Task<string?> GetIntegratorAddressAsync(CancellationToken ct = default);
    Task<WalletInfo> GetWalletAsync(string orgAddress, CancellationToken ct = default);
}
