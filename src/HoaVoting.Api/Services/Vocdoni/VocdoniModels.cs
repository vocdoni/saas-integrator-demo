using System.Text.Json.Serialization;

namespace HoaVoting.Api.Services.Vocdoni;

// Wire models for the Vocdoni SaaS API. Names match the swagger (camelCase via serializer).
//
// ponytail: Vocdoni's swagger types org/process addresses as []byte (rendered as int arrays),
// but the Go HexBytes type marshals to a hex string on the wire and URL path params are hex
// strings. We model them as strings (hex). If a live server rejects this, that's the knob to turn.

/// <summary>Subset of apicommon.OrganizationInfo we read/write. Name lives in <see cref="Meta"/>.</summary>
public sealed class VocdoniOrganizationInfo
{
    public string? Address { get; set; }
    public string? Type { get; set; }
    public string? Country { get; set; }

    /// <summary>Eagerly provision the on-chain account so census/processes can be created.</summary>
    public bool? ProvisionAccount { get; set; }

    public Dictionary<string, object>? Meta { get; set; }
}

public sealed class VocdoniOrgMember
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? MemberNumber { get; set; }
    public string? NationalId { get; set; }
    public string? BirthDate { get; set; }

    /// <summary>Census weight (string per the API).</summary>
    public string? Weight { get; set; }
}

public sealed class AddMembersRequest
{
    public List<VocdoniOrgMember> Members { get; set; } = new();
}

public sealed class AddMembersResponse
{
    public int Added { get; set; }
    public string? JobId { get; set; }
    public List<string>? Errors { get; set; }
}

public sealed class OrganizationMembersResponse
{
    public List<VocdoniOrgMember> Members { get; set; } = new();

    /// <summary>Present on the paginated GET; null on older backends (treat as single page).</summary>
    public Pagination? Pagination { get; set; }
}

public sealed class Pagination
{
    public int CurrentPage { get; set; }
    public int LastPage { get; set; }
    public int TotalItems { get; set; }
}

public sealed class DeleteMembersRequest
{
    public List<string>? Ids { get; set; }
    public bool All { get; set; }
}

// --- multi-question /processes API (saas-backend #571) ---------------------

/// <summary>POST /processes — a draft container with N questions. Census is inline (no separate census calls).</summary>
public sealed class CreateVotingProcessRequest
{
    public string OrgAddress { get; set; } = "";
    public CensusSpec Census { get; set; } = new();
    /// <summary>MultiLangString, e.g. {"default":"..."}.</summary>
    public Dictionary<string, string> Title { get; set; } = new();
    public Dictionary<string, string>? Description { get; set; }
    public string? StartDate { get; set; } // RFC3339
    public string? EndDate { get; set; }   // RFC3339
    public List<VotingProcessQuestionRequest> Questions { get; set; } = new();
}

/// <summary>Process census config — the census type is inferred from the auth/2FA fields.</summary>
public sealed class CensusSpec
{
    public bool Weighted { get; set; }
    public List<string>? AuthFields { get; set; }
    public List<string>? TwoFaFields { get; set; }
    public string? GroupId { get; set; }
    public List<string>? MemberIds { get; set; }
}

public sealed class VotingProcessQuestionRequest
{
    public Dictionary<string, string> Title { get; set; } = new();
    public Dictionary<string, string>? Description { get; set; }
    public List<VocdoniChoice> Choices { get; set; } = new();
    /// <summary>"singlechoice" | "multichoice". Ranked/approval use <see cref="BallotProtocol"/> instead.</summary>
    public string Type { get; set; } = "singlechoice";
    public QuestionTypeSetup TypeSetup { get; set; } = new();
    /// <summary>Raw vote-type override (wins over type/typeSetup) — used for ranked/approval/quadratic.</summary>
    public BallotProtocol? BallotProtocol { get; set; }
    public bool SecretUntilTheEnd { get; set; }
    /// <summary>Per-question eligibility subset ⊆ census. NB: keyed "census" in the question JSON.</summary>
    [JsonPropertyName("census")]
    public EligibilitySpec? Eligibility { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public sealed class VocdoniChoice
{
    public Dictionary<string, string> Title { get; set; } = new();
    public uint Value { get; set; }
    /// <summary>Marks the one choice that accepts a free-text voter memo (saas-backend #577). Max one per question.</summary>
    public bool OpenValue { get; set; }
}

public sealed class QuestionTypeSetup
{
    public uint MinChoices { get; set; }
    public uint MaxChoices { get; set; }
    public bool UniqueChoices { get; set; }
}

/// <summary>Raw on-chain vote-type override (note <c>UniqueValues</c>, not UniqueChoices).</summary>
public sealed class BallotProtocol
{
    public int MaxCount { get; set; }
    public int MaxValue { get; set; }
    public int MaxVoteOverwrites { get; set; }
    public int CostExponent { get; set; }
    public int MaxTotalCost { get; set; }
    public bool UniqueValues { get; set; }
    public bool CostFromWeight { get; set; }
}

public sealed class EligibilitySpec
{
    public string? GroupId { get; set; }
    public List<string>? MemberIds { get; set; }
}

public sealed class CreateVotingProcessResponse
{
    public string? ProcessId { get; set; }
}

/// <summary>GET /processes/{id} — fully hydrated; questions carry upstreamId + status after publish.</summary>
public sealed class VotingProcessResponse
{
    public string? Id { get; set; }
    public string? OrgAddress { get; set; }
    public bool Published { get; set; }
    /// <summary>Vochain chain id votes must be signed against (saas-backend #582). Exposed to the voting page.</summary>
    public string? ChainId { get; set; }
    public CensusSpec? Census { get; set; }
    public Dictionary<string, string>? Title { get; set; }
    public Dictionary<string, string>? Description { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public List<VotingProcessQuestion> Questions { get; set; } = new();
}

public sealed class VotingProcessQuestion
{
    public string? Id { get; set; }
    public string? ParentProcessId { get; set; }
    public Dictionary<string, string>? Title { get; set; }
    public List<VocdoniChoice>? Choices { get; set; }
    public string? Type { get; set; }
    public QuestionTypeSetup? TypeSetup { get; set; }
    public BallotProtocol? BallotProtocol { get; set; }
    public List<string>? EligibleMemberIds { get; set; }
    /// <summary>On-chain election id (hex), assigned at publish.</summary>
    public string? UpstreamId { get; set; }
    /// <summary>ready | paused | ended | canceled | results.</summary>
    public string? Status { get; set; }
    /// <summary>Inline on-chain tally (saas-backend #596), resolved on read only when Status is "results".</summary>
    public VocdoniQuestionResults? Results { get; set; }
}

/// <summary>One question's on-chain tally (saas-backend #596 db.QuestionResults).</summary>
public sealed class VocdoniQuestionResults
{
    public int VoteCount { get; set; }
    /// <summary>The question's own on-chain maxCensusSize (its eligibility subset) — a turnout denominator.</summary>
    public int MaxVoters { get; set; }
    public bool FinalResults { get; set; }
    /// <summary>Raw tally matrix (strings, big-int safe): one row per ballot field, per-value counts.</summary>
    public List<List<string>>? Results { get; set; }
    /// <summary>
    /// Free-text voter memos cast alongside the question's open-value choice (saas-backend #577).
    /// Present only for a manager/admin caller (the process read carries them inline once RESULTS);
    /// never surfaced to the public voting page.
    /// </summary>
    public List<string>? Memos { get; set; }
}

public sealed class SetQuestionsStatusRequest
{
    /// <summary>ready | paused | ended | canceled.</summary>
    public string Status { get; set; } = "";
    /// <summary>Null/empty ⇒ all published questions.</summary>
    public List<QuestionStatusId>? Questions { get; set; }
}

public sealed class QuestionStatusId
{
    public string Id { get; set; } = "";
}

public sealed class VotingProcessValidateResponse
{
    public bool Valid { get; set; }
    public List<string>? Errors { get; set; }
}

public sealed class EnqueuedResponse
{
    public string? JobId { get; set; }
}

/// <summary>db.JobStatus of an async transaction job (publish, status change, vote relay).</summary>
public sealed class JobStatusResponse
{
    /// <summary>One of: pending, completed, failed.</summary>
    public string? Status { get; set; }
    /// <summary>Failure detail(s) when Status is "failed" (empty otherwise).</summary>
    public List<string>? Errors { get; set; }
}
