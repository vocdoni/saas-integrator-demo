using HoaVoting.Api.Controllers;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Models;
using Xunit;

namespace HoaVoting.Tests;

// ToQuestionRequest is the demo↔saas-backend #638 contract: named types only, with the backend
// deriving the on-chain ballot protocol. A contradiction (named type + mismatched raw protocol)
// or a non-empty typeSetup on ranked is a 400 upstream — these tests pin the exact wire shapes.
public class QuestionMappingTests
{
    private static QuestionInput Input(VotingType kind, int? budget = null, int? costExponent = null) =>
        new("Q", [new("A"), new("B"), new("C")], kind, budget, costExponent);

    [Fact]
    public void Single_maps_to_named_type_with_min_max_one()
    {
        var q = ProposalsController.ToQuestionRequest(Input(VotingType.Single));

        Assert.Equal("singlechoice", q.Type);
        Assert.Equal(1u, q.TypeSetup!.MinChoices);
        Assert.Equal(1u, q.TypeSetup!.MaxChoices);
        Assert.Null(q.BallotProtocol);
    }

    [Fact]
    public void Multiple_maps_to_multichoice_with_max_equal_to_choice_count()
    {
        var q = ProposalsController.ToQuestionRequest(Input(VotingType.Multiple));

        Assert.Equal("multichoice", q.Type);
        Assert.Equal(3u, q.TypeSetup!.MaxChoices);
        Assert.Null(q.BallotProtocol);
    }

    [Fact]
    public void Ranked_sends_only_the_named_type()
    {
        // The #638 fix: ranked derives its whole protocol from the choices and REJECTS a typeSetup
        // or a raw ballotProtocol. The previous singlechoice+protocol combination 400s upstream.
        var q = ProposalsController.ToQuestionRequest(Input(VotingType.Ranked));

        Assert.Equal("ranked", q.Type);
        Assert.Null(q.TypeSetup);
        Assert.Null(q.BallotProtocol);
    }

    [Fact]
    public void Cumulative_sends_budget_and_cost_exponent()
    {
        var q = ProposalsController.ToQuestionRequest(Input(VotingType.Cumulative, budget: 12, costExponent: 2));

        Assert.Equal("cumulative", q.Type);
        Assert.Equal(12u, q.TypeSetup!.Budget);
        Assert.Equal(2u, q.TypeSetup!.CostExponent);
        Assert.Null(q.TypeSetup!.MaxChoices);
        Assert.Null(q.BallotProtocol);
    }

    [Fact]
    public void Choices_carry_their_index_value_and_open_flag()
    {
        var q = ProposalsController.ToQuestionRequest(
            new QuestionInput("Q", [new("A"), new("Other", Open: true)], VotingType.Single));

        Assert.Equal([0u, 1u], q.Choices.Select(c => c.Value));
        Assert.Equal([false, true], q.Choices.Select(c => c.OpenValue));
    }

    [Fact]
    public void ParseEligibilityConflict_names_the_signed_members_on_40173()
    {
        var conflict = ProposalsController.ParseEligibilityConflict(
            """{"code":40173,"error":"member already signed","data":{"signedMemberIds":["m1","m2"]}}""");

        Assert.Equal(["m1", "m2"], conflict.SignedMemberIds);
        Assert.Contains("already voted", conflict.Message);
    }

    [Fact]
    public void ParseEligibilityConflict_passes_other_conflicts_through()
    {
        var conflict = ProposalsController.ParseEligibilityConflict("""{"code":40901,"error":"publish in progress"}""");

        Assert.Equal("publish in progress", conflict.Message);
        Assert.Empty(conflict.SignedMemberIds);

        var garbage = ProposalsController.ParseEligibilityConflict("not json");
        Assert.Empty(garbage.SignedMemberIds);
    }
}
