namespace HoaVoting.Api.Models;

public enum ProposalStatus
{
    Draft,
    Open,
    Closed,
}

/// <summary>
/// A proposal put to vote — a Vocdoni **voting process** (container). Each of its questions is its own
/// on-chain election (saas-backend #571). The backend authors + publishes the process; vote casting is
/// client-side per question via the integrator SDK.
/// </summary>
public class Proposal
{
    public int Id { get; set; }

    public int AssociationId { get; set; }
    public Association? Association { get; set; }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Vocdoni 24-hex ProcessID of the container (the handle for read/publish/status).</summary>
    public string VocdoniProcessId { get; set; } = "";

    /// <summary>Vochain chain id the process's votes must be signed against (captured from the process read, #582).</summary>
    public string ChainId { get; set; } = "";

    public List<ProposalQuestion> Questions { get; set; } = new();

    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset EndDate { get; set; }

    public ProposalStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One question of a proposal — maps 1:1 to an on-chain election under the process container.</summary>
public class ProposalQuestion
{
    public int Id { get; set; }

    public int ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    /// <summary>Order within the process (0-based).</summary>
    public int Order { get; set; }

    public string Title { get; set; } = "";

    /// <summary>The question's choice titles, JSON-serialized.</summary>
    public string ChoicesJson { get; set; } = "[]";

    /// <summary>Ballot kind for this question: single choice, multiple (approval), or ranked.</summary>
    public VotingType Kind { get; set; }

    /// <summary>On-chain election id (hex), assigned when the process is published. Empty before publish.</summary>
    public string UpstreamId { get; set; } = "";

    /// <summary>On-chain status: ready | paused | ended | canceled | results. Empty before publish.</summary>
    public string Status { get; set; } = "";
}
