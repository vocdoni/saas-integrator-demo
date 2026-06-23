namespace HoaVoting.Api.Models;

/// <summary>A homeowners' association. Maps 1:1 to a Vocdoni organization.</summary>
public class Association
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The Owner app user who manages this association.</summary>
    public int OwnerUserId { get; set; }
    public AppUser? Owner { get; set; }

    /// <summary>Vocdoni organization address (hex string of the on-chain address).</summary>
    public string VocdoniOrgAddress { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public List<Proposal> Proposals { get; set; } = new();
}
