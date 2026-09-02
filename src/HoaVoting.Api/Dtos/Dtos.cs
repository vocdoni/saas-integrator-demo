using HoaVoting.Api.Models;

namespace HoaVoting.Api.Dtos;

// Client-facing DTOs. Vocdoni wire types never leak past the service layer.

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, DateTimeOffset Expires, string Role);

public record CreateAssociationRequest(string Name, string OwnerEmail, string OwnerPassword);
public record ImportAssociationRequest(string Name, string VocdoniOrgAddress, string OwnerEmail, string OwnerPassword);
public record AssociationResponse(int Id, string Name, string OwnerEmail, string VocdoniOrgAddress, DateTimeOffset CreatedAt);

public record AddHomeownerRequest(
    string Name,
    string? Surname,
    string? Email,          // optional member contact detail
    string? Phone,
    string MemberNumber,
    string? Weight);

public record HomeownerResponse(string Id, string Name, string? Surname, string Email, string? MemberNumber, string? Weight);

// A proposal is a multi-question voting process (saas-backend #571). Voters always auth by member
// number (no 2FA); each question is its own on-chain election.
public record CreateProposalRequest(
    string Title,
    string Description,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    List<QuestionInput> Questions,
    // Blind-CSP anonymous voting (saas-backend #641). Plan-gated: check /features first.
    bool Anonymous = false);

public record QuestionInput(
    string Title,
    List<ProposalChoice> Choices,
    // single (default) | multiple (approval) | ranked | cumulative.
    VotingType Kind = VotingType.Single,
    // Cumulative only: credit budget and cost exponent (1 = linear, 2 = quadratic).
    int? Budget = null,
    int? CostExponent = null);

// `Open` marks this choice as the free-text "Other" option (saas-backend #577); single-choice questions
// only, at most one per question. Voters who pick it must attach a memo.
public record ProposalChoice(string Title, bool Open = false);

public record ProposalResponse(
    int Id,
    int AssociationId,
    string Title,
    string Description,
    string Status,
    string VocdoniProcessId,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    DateTimeOffset CreatedAt,
    List<QuestionResponse> Questions,
    bool Anonymous = false);

public record QuestionResponse(
    int Id,
    int Order,
    string Title,
    List<string> Choices,
    VotingType Kind,
    // Index of the free-text "Other" choice (saas-backend #577), or -1 if none.
    int OpenChoiceIndex,
    // On-chain election id (hex) + status, once the process is published.
    string UpstreamId,
    string Status,
    // On-chain tally, best-effort (inline on GET /processes/{id}, saas-backend #596). Results is the
    // histogram matrix; MaxVoters is this question's own census size (turnout denominator).
    int VoteCount,
    int MaxVoters,
    List<List<string>>? Results,
    // Free-text voter memos on the open choice (saas-backend #577), manager-only + RESULTS-only.
    List<string>? Memos,
    // Cumulative only: credit budget + cost exponent (1 linear, 2 quadratic).
    int? Budget = null,
    int? CostExponent = null,
    // Current eligibility restriction (saas-backend #621): Vocdoni member ids, null/empty = whole census.
    List<string>? EligibleMemberIds = null);

/// <summary>Public, read-only voting-page payload (no auth). Casting is client-side per question.</summary>
public record VotingInfoResponse(
    string ProcessId,
    // Vocdoni SaaS API base URL + chain id — the voting page casts ballots client-side via the
    // integrator SDK's crypto against this API (CSP auth/sign per question, then one batch POST /votes).
    string ApiUrl,
    string ChainId,
    string Title,
    string Description,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string Status,
    List<PublicQuestion> Questions,
    // Blind-CSP anonymous process (saas-backend #641) — the page picks the blind-signature flow.
    bool Anonymous = false);

public record PublicQuestion(
    int Id,
    int Order,
    string Title,
    List<string> Choices,
    VotingType Kind,
    // Index of the free-text "Other" choice (saas-backend #577), or -1 if none — the voting page shows a
    // required memo input when it's selected. Voter memos themselves are never exposed on the public page.
    int OpenChoiceIndex,
    // The on-chain election id the voter signs against and relays to; empty until published.
    string UpstreamId,
    string Status,
    // On-chain tally (best-effort, inline on GET /processes/{id}, saas-backend #596), for the finished
    // state. Results is the histogram matrix; MaxVoters is this question's census size (turnout denom).
    int VoteCount,
    int MaxVoters,
    List<List<string>>? Results,
    // Cumulative only: the allocator UI needs the budget + cost model.
    int? Budget = null,
    int? CostExponent = null);

/// <summary>Plan features relevant to the admin UI (from the org's Vocdoni subscription).</summary>
public record AssociationFeaturesResponse(bool AnonymousVoting);

// Live per-question eligibility (saas-backend #621). MemberIds is the COMPLETE desired list
// (Vocdoni member ids); [] reopens the question to the whole census.
public record SetEligibilityRequest(List<string> MemberIds);
public record EligibilityResponse(int Eligible, int Added, int Removed);
// 409: some voters were already CSP-signed for and cannot lose eligibility while the election runs.
public record EligibilityConflictResponse(string Message, List<string> SignedMemberIds);
