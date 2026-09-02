namespace HoaVoting.Api.Models;

/// <summary>How a proposal's ballot works. Maps to the Vocdoni election <c>voteType</c>.</summary>
public enum VotingType
{
    /// <summary>Pick exactly one choice. <c>maxCount:1, maxValue:N-1</c>.</summary>
    Single,

    /// <summary>Approval — pick any number of choices. <c>maxCount:N, maxValue:1, uniqueChoices:false</c>.</summary>
    Multiple,

    /// <summary>Ranked (linear-weighted) — order the choices. <c>maxCount:N, maxValue:N-1, uniqueChoices:true</c>.</summary>
    Ranked,

    /// <summary>Cumulative/quadratic — distribute a credit budget among choices.
    /// <c>maxValue:0 (amounts mode), maxTotalCost:budget, costExponent:1|2</c>.</summary>
    Cumulative,
}
