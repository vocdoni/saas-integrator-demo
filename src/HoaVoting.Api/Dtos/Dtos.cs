namespace HoaVoting.Api.Dtos;

// Client-facing DTOs. Vocdoni wire types never leak past the service layer.

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, DateTimeOffset Expires);

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
    string Status,
    string VocdoniProcessId,
    string VocdoniCensusId,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    DateTimeOffset CreatedAt);

public record ProposalResultsResponse(
    string ProcessId,
    string? Status,
    bool FinalResults,
    int VoteCount,
    List<List<string>>? Results);
