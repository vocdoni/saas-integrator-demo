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

    public Func<CreateVotingProcessRequest, string> OnCreateProcess { get; set; } = _ => "proc1";
    public Action<string> OnPublish { get; set; } = _ => { };
    public Func<string, VotingProcessResponse> OnGetProcess { get; set; } = id => new() { Id = id };

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

    public Task<string> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
    {
        Calls.Add("CreateProcess");
        LastCreate = request;
        return Task.FromResult(OnCreateProcess(request));
    }

    public Task PublishVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        Calls.Add($"Publish {processId}");
        OnPublish(processId);
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
}
