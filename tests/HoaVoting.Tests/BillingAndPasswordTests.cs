using System.Net;
using HoaVoting.Api.Controllers;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Services.Vocdoni;

namespace HoaVoting.Tests;

// AC4 (price), AC5 (integrator wallet), AC7 (member password hash at the trust boundary).
public class BillingAndPasswordTests : ControllerTestBase
{
    private static readonly string Hash = new('a', 64);

    public BillingAndPasswordTests()
    {
        Vocdoni.Members =
        [
            new() { Id = "m1", HasPassword = true },
            new() { Id = "m2" },
            new() { Id = "m3", HasPassword = true },
        ];
    }

    private AssociationsController Associations() => AsOwner(new AssociationsController(Db, Vocdoni));
    private HomeownersController Homeowners() => AsOwner(new HomeownersController(Db, Vocdoni));
    private IntegratorController Integrator() => AsAdmin(new IntegratorController(Vocdoni));

    // --- price -----------------------------------------------------------------

    [Fact]
    public async Task Price_counts_every_homeowner_or_only_password_holders()
    {
        Vocdoni.OnGetPrice = voters => new() { TotalCents = voters * 100, Currency = "eur" };

        var all = (await Associations().Price(Assoc.Id, requirePassword: false, default)).Value!;
        var withPassword = (await Associations().Price(Assoc.Id, requirePassword: true, default)).Value!;

        Assert.Equal((3, 300), (all.Voters, all.TotalCents));
        Assert.Equal((2, 200), (withPassword.Voters, withPassword.TotalCents));
        Assert.Equal(["GetPrice 3", "GetPrice 2"], Vocdoni.Calls.Where(c => c.StartsWith("GetPrice")));
    }

    [Fact]
    public async Task Price_with_no_eligible_homeowner_answers_locally()
    {
        Vocdoni.Members = [new() { Id = "m1" }];

        var res = (await Associations().Price(Assoc.Id, requirePassword: true, default)).Value!;

        Assert.Equal(new PriceResponse(0, 0, "eur", false, false), res);
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("GetPrice")); // /pricing 400s on voters=0
    }

    // --- integrator ----------------------------------------------------------------

    [Fact]
    public async Task Integrator_reads_the_balance_of_the_managedBy_wallet()
    {
        Vocdoni.OnGetIntegrator = () => new() { CanCreateManagedOrg = false, ManagedOrgsCreated = 1 };
        Vocdoni.OnGetWallet = _ => new() { BalanceCents = 4200, Currency = "eur" };

        var res = (await Integrator().Get(default)).Value!;

        Assert.Equal(new IntegratorResponse(false, 1, 4200, "eur"), res);
        Assert.Contains("GetWallet 0xintegrator", Vocdoni.Calls);
    }

    [Fact]
    public async Task Integrator_balance_is_null_without_a_managed_org()
    {
        Vocdoni.OnGetIntegratorAddress = () => null;

        var res = (await Integrator().Get(default)).Value!;

        Assert.Null(res.BalanceCents);
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("GetWallet"));
    }

    [Fact]
    public async Task Integrator_balance_is_null_when_the_wallet_read_is_forbidden()
    {
        Vocdoni.OnGetWallet = _ => throw new VocdoniApiException(HttpStatusCode.Forbidden, """{"error":"scope","code":40157}""");

        var res = (await Integrator().Get(default)).Value!;

        Assert.True(res.CanCreateManagedOrg);
        Assert.Null(res.BalanceCents);
    }

    // --- member password -------------------------------------------------------------

    [Theory]
    [InlineData("hunter2")] // plaintext
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // uppercase
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 63 chars
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")] // trailing newline
    public async Task A_password_that_is_not_the_client_hash_is_a_400(string bad)
    {
        Assert.Equal(400, StatusOf(await Homeowners().SetPassword(Assoc.Id, "m1", new(bad), default)));
        Assert.Equal(400, StatusOf(await Homeowners().Add(Assoc.Id, new("Ann", null, null, null, "7", null, bad), default)));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("UpsertMember") || c.StartsWith("AddMembers"));
    }

    [Fact]
    public async Task A_valid_hash_reaches_the_upstream_member_password()
    {
        Assert.Equal(204, StatusOf(await Homeowners().SetPassword(Assoc.Id, "m2", new(Hash), default)));
        Assert.Equal(("m2", Hash), (Vocdoni.LastUpsert!.Id, Vocdoni.LastUpsert.Password));

        await Homeowners().Add(Assoc.Id, new("Ann", null, null, null, "7", null, Hash), default);
        Assert.Equal(Hash, Assert.Single(Vocdoni.LastAdded!).Password);
    }

    [Fact]
    public async Task Setting_a_password_for_an_unknown_member_is_a_404_without_upstream_write()
    {
        Assert.Equal(404, StatusOf(await Homeowners().SetPassword(Assoc.Id, "not-a-member", new(Hash), default)));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("UpsertMember"));
    }

    [Fact]
    public async Task Homeowner_list_reports_who_has_a_password()
    {
        var res = await Homeowners().List(Assoc.Id, default);

        Assert.Equal([true, false, true], res.Value!.Select(h => h.HasPassword));
    }
}
