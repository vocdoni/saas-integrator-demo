namespace HoaVoting.Api.Models;

public enum ProposalStatus
{
    Draft,
    Open,
    Closed,
}

/// <summary>
/// A proposal put to vote. Backed by a Vocdoni census + process (election). Vote casting is
/// done client-side via the Vocdoni JS SDK; this backend only creates the process and reads results.
/// </summary>
public class Proposal
{
    public int Id { get; set; }

    public int AssociationId { get; set; }
    public Association? Association { get; set; }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Vocdoni census id this proposal was published against.</summary>
    public string VocdoniCensusId { get; set; } = "";

    /// <summary>Vocdoni process (election) id.</summary>
    public string VocdoniProcessId { get; set; } = "";

    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset EndDate { get; set; }

    public ProposalStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
