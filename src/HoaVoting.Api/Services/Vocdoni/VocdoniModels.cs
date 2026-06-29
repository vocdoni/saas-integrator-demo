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
}

public sealed class DeleteMembersRequest
{
    public List<string>? Ids { get; set; }
    public bool All { get; set; }
}

public sealed class CreateCensusRequest
{
    public string OrgAddress { get; set; } = "";

    /// <summary>Member fields used to authenticate voters (e.g. "memberNumber").</summary>
    public List<string>? AuthFields { get; set; }

    /// <summary>Member fields used for 2FA (e.g. "email", "phone").</summary>
    public List<string>? TwoFaFields { get; set; }
}

public sealed class CreateCensusResponse
{
    public string Id { get; set; } = "";
}

public sealed class CreateMemberGroupRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public bool IncludeAllMembers { get; set; }
}

public sealed class MemberGroupInfo
{
    public string Id { get; set; } = "";
}

public sealed class PublishCensusGroupRequest
{
    public List<string>? AuthFields { get; set; }
    public List<string>? TwoFaFields { get; set; }
    public bool Weighted { get; set; }
}

public sealed class PublishedCensusResponse
{
    public string? Root { get; set; }
    public int Size { get; set; }
    public string? Uri { get; set; }
}

public sealed class CreateProcessRequest
{
    public string OrgAddress { get; set; } = "";
    public string CensusId { get; set; } = "";
    public Dictionary<string, object>? Metadata { get; set; }
    public ElectionParams? ElectionParams { get; set; }
}

public sealed class CreateProcessBundleRequest
{
    public string CensusId { get; set; } = "";

    /// <summary>ProcessIDs to include in the bundle (the on-chain election id also works; see #554).</summary>
    public List<string> Processes { get; set; } = new();
}

public sealed class CreateProcessBundleResponse
{
    /// <summary>e.g. "https://.../process/bundle/{bundleId}". The id is the last path segment.</summary>
    public string? Uri { get; set; }
    public string? Root { get; set; }
}

public sealed class ElectionParams
{
    public Dictionary<string, string>? Title { get; set; }
    public Dictionary<string, string>? Description { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public int? MaxCensusSize { get; set; }
    public List<ElectionQuestion>? Questions { get; set; }
    public ElectionType? ElectionType { get; set; }
    public VoteType? VoteType { get; set; }
}

public sealed class ElectionQuestion
{
    public Dictionary<string, string>? Title { get; set; }
    public Dictionary<string, string>? Description { get; set; }
    public List<ElectionChoice>? Choices { get; set; }
}

public sealed class ElectionChoice
{
    public Dictionary<string, string>? Title { get; set; }
    public int Value { get; set; }
}

public sealed class ElectionType
{
    public bool Autostart { get; set; }
    public bool Interruptible { get; set; }
}

public sealed class VoteType
{
    public int MaxCount { get; set; }
    public int MaxValue { get; set; }
    public int MaxVoteOverwrites { get; set; }
    public bool UniqueChoices { get; set; }
}

public sealed class SetProcessStatusRequest
{
    /// <summary>One of: ready, paused, ended, canceled.</summary>
    public string Status { get; set; } = "";
}

public sealed class EnqueuedResponse
{
    public string? JobId { get; set; }
}

/// <summary>Subset of db.Process. <c>Address</c> is the on-chain Vochain election id (set after publish).</summary>
public sealed class ProcessDetail
{
    public string? Id { get; set; }
    public string? Address { get; set; }
    public string? Status { get; set; }
}

public sealed class ProcessResultsResponse
{
    public string? Status { get; set; }
    public bool FinalResults { get; set; }
    public int VoteCount { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }

    /// <summary>Per-question, per-choice tallies (strings, big-int safe).</summary>
    public List<List<string>>? Results { get; set; }
}
