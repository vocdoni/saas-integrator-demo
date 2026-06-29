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
    string? Email,          // optional — only needed for email-2FA censuses
    string? Phone,
    string MemberNumber,
    string? Weight);

public record HomeownerResponse(string Id, string Name, string? Surname, string Email, string? MemberNumber, string? Weight);

public record CreateProposalRequest(
    string Title,
    string Description,
    List<ProposalChoice> Choices,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    bool AllowMultiple = false,
    // When true, voters confirm via an email OTP. When false, the census is CSP-based with no
    // 2FA — voters authenticate by member number alone.
    bool TwoFactorAuth = true);

public record ProposalChoice(string Title);

public record ProposalResponse(
    int Id,
    int AssociationId,
    string Title,
    string Description,
    List<string> Choices,
    string Status,
    string VocdoniProcessId,
    string VocdoniCensusId,
    string VocdoniBundleId,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    DateTimeOffset CreatedAt);

public record ProposalResultsResponse(
    string ProcessId,
    string? Status,
    bool FinalResults,
    int VoteCount,
    List<List<string>>? Results,
    // Published census size = eligible voters; the tally bars fill against this. Null if unavailable.
    int? CensusSize);

/// <summary>Public, read-only voting-page payload (no auth).</summary>
public record VotingInfoResponse(
    string ProcessId,
    string BundleId,
    // Vocdoni SaaS API base URL — the voting page casts ballots client-side via the integrator SDK,
    // which talks straight to this API (CSP auth/sign/relay). Never the chain directly.
    string ApiUrl,
    string Title,
    string Description,
    List<string> Choices,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string Status,
    int? VoteCount,
    string? OnchainStatus,
    List<List<string>>? Results,
    // Published census size = eligible voters; shown on the page and used to fill the result bars.
    int? CensusSize);
