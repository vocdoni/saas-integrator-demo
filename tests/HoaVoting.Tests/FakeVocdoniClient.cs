using HoaVoting.Api.Services.Vocdoni;

namespace HoaVoting.Tests;

/// <summary>
/// Scripted <see cref="IVocdoniClient"/> for controller tests: every call is recorded in
/// <see cref="Calls"/> ("Method arg"), and each answer comes from a settable delegate, so a test
/// scripts a return value or throws from it.
/// </summary>
public sealed class FakeVocdoniClient : IVocdoniClient
{
    public List<string> Calls { get; } = [];

    public List<VocdoniOrgMember> Members { get; set; } = [];
    public CreateVotingProcessRequest? LastCreate { get; private set; }
    public List<VocdoniOrgMember>? LastAdded { get; private set; }

    public VocdoniOrgMember? LastUpsert { get; private set; }
    public DateTimeOffset? LastEndDate { get; private set; }

    public Func<CreateVotingProcessRequest, CreateVotingProcessResponse> OnCreateProcess { get; set; } = _ => new() { ProcessId = "proc1" };
    /// <summary>Publish answer: a job id (202) or null (200, already published). Throw to refuse.</summary>
    public Func<string, string?> OnPublish { get; set; } = _ => "job1";
    public Action<string> OnDeleteProcess { get; set; } = _ => { };
    public Func<string, VotingProcessResponse> OnGetProcess { get; set; } = id => new() { Id = id };
    public Func<string, (JobState, string?)> OnWaitForJob { get; set; } = _ => (JobState.Completed, null);
    public Func<string, JobStatusResponse> OnGetJob { get; set; } = _ => new() { Status = "pending" };
    public Func<string, string> OnSetEndDate { get; set; } = _ => "job-end";
    public Func<int, ProcessPrice> OnGetPrice { get; set; } = _ => new() { Currency = "eur" };
    public Func<IntegratorInfo> OnGetIntegrator { get; set; } = () => new() { Enabled = true, CanCreateManagedOrg = true };
    public Func<string?> OnGetIntegratorAddress { get; set; } = () => "0xintegrator";
    public Func<string, WalletInfo> OnGetWallet { get; set; } = _ => new() { BalanceCents = 0, Currency = "eur" };

    public Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"CreateOrganization {name}");
        return Task.FromResult("0xnew");
    }

    public Task DeleteOrganizationAsync(string orgAddress, CancellationToken ct = default)
    {
        Calls.Add($"DeleteOrganization {orgAddress}");
        return Task.CompletedTask;
    }

    public Task<AddMembersResponse> AddMembersAsync(string orgAddress, List<VocdoniOrgMember> members, CancellationToken ct = default)
    {
        Calls.Add($"AddMembers {orgAddress}");
        LastAdded = members;
        return Task.FromResult(new AddMembersResponse { Added = members.Count });
    }

    public Task<List<VocdoniOrgMember>> ListMembersAsync(string orgAddress, CancellationToken ct = default)
    {
        Calls.Add($"ListMembers {orgAddress}");
        return Task.FromResult(Members);
    }

    public Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default)
    {
        Calls.Add($"DeleteMembers {string.Join(",", memberIds)}");
        return Task.CompletedTask;
    }

    public Task UpsertMemberAsync(string orgAddress, VocdoniOrgMember member, CancellationToken ct = default)
    {
        Calls.Add($"UpsertMember {member.Id}");
        LastUpsert = member;
        return Task.CompletedTask;
    }

    public Task<CreateVotingProcessResponse> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
    {
        Calls.Add("CreateProcess");
        LastCreate = request;
        return Task.FromResult(OnCreateProcess(request));
    }

    public Task<string?> PublishVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        Calls.Add($"Publish {processId}");
        return Task.FromResult(OnPublish(processId));
    }

    public Task DeleteVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        Calls.Add($"DeleteProcess {processId}");
        OnDeleteProcess(processId);
        return Task.CompletedTask;
    }

    public Task<VotingProcessResponse> GetVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        Calls.Add($"GetProcess {processId}");
        return Task.FromResult(OnGetProcess(processId));
    }

    public Task SetQuestionsStatusAsync(string processId, string status, List<string>? questionIds = null, CancellationToken ct = default)
    {
        Calls.Add($"SetQuestionsStatus {processId} {status}");
        return Task.CompletedTask;
    }

    public Task<UpdateQuestionCensusResponse> SetQuestionCensusAsync(string processId, string questionId, List<string> memberIds, CancellationToken ct = default)
    {
        Calls.Add($"SetQuestionCensus {processId} {questionId}");
        return Task.FromResult(new UpdateQuestionCensusResponse());
    }

    public Task<string> SetProcessEndDateAsync(string processId, DateTimeOffset endDate, CancellationToken ct = default)
    {
        Calls.Add($"SetEndDate {processId}");
        LastEndDate = endDate;
        return Task.FromResult(OnSetEndDate(processId));
    }

    public Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        Calls.Add($"GetJob {jobId}");
        return Task.FromResult(OnGetJob(jobId));
    }

    public Task<(JobState State, string? Error)> WaitForJobAsync(string jobId, CancellationToken ct = default)
    {
        Calls.Add($"WaitForJob {jobId}");
        return Task.FromResult(OnWaitForJob(jobId));
    }

    public Task<ProcessPrice> GetPriceAsync(int voters, CancellationToken ct = default)
    {
        Calls.Add($"GetPrice {voters}");
        return Task.FromResult(OnGetPrice(voters));
    }

    public Task<IntegratorInfo> GetIntegratorInfoAsync(CancellationToken ct = default)
    {
        Calls.Add("GetIntegrator");
        return Task.FromResult(OnGetIntegrator());
    }

    public Task<string?> GetIntegratorAddressAsync(CancellationToken ct = default)
    {
        Calls.Add("GetIntegratorAddress");
        return Task.FromResult(OnGetIntegratorAddress());
    }

    public Task<WalletInfo> GetWalletAsync(string orgAddress, CancellationToken ct = default)
    {
        Calls.Add($"GetWallet {orgAddress}");
        return Task.FromResult(OnGetWallet(orgAddress));
    }
}
