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
    List<QuestionInput> Questions);

public record QuestionInput(
    string Title,
    List<ProposalChoice> Choices,
    // single (default) | multiple (approval) | ranked.
    VotingType Kind = VotingType.Single);

public record ProposalChoice(string Title);

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
    List<QuestionResponse> Questions);

public record QuestionResponse(
    int Id,
    int Order,
    string Title,
    List<string> Choices,
    VotingType Kind,
    // On-chain election id (hex) + status, once the process is published.
    string UpstreamId,
    string Status);

/// <summary>Public, read-only voting-page payload (no auth). Casting is client-side per question.</summary>
public record VotingInfoResponse(
    string ProcessId,
    // Vocdoni SaaS API base URL + chain id — the voting page casts ballots client-side via the
    // integrator SDK's crypto against this API (CSP auth/sign per question, relay POST /vote).
    string ApiUrl,
    string ChainId,
    string Title,
    string Description,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string Status,
    List<PublicQuestion> Questions);

public record PublicQuestion(
    int Id,
    int Order,
    string Title,
    List<string> Choices,
    VotingType Kind,
    // The on-chain election id the voter signs against and relays to; empty until published.
    string UpstreamId,
    string Status);
