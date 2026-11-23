using System.Net;
using HoaVoting.Api.Services.Vocdoni;

namespace HoaVoting.Tests;

// AC3: the 502 body's {code, detail} — readable sentences for the billing/limit codes, upstream's own
// text otherwise, the raw message when the body is not an errors.Error.
public class UpstreamErrorsTests
{
    private static VocdoniApiException Upstream(HttpStatusCode status, string body) => new(status, body);

    [Fact]
    public void Wallet_short_names_both_amounts_in_euros()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.PaymentRequired,
            """{"error":"insufficient integrator wallet balance","code":40175,"data":{"requiredCents":1980,"availableCents":500}}"""));

        Assert.Equal(40175, code);
        Assert.Equal("The integrator's wallet doesn't cover this (needs €19.80, available €5.00). Top it up in the Vocdoni portal.", detail);
    }

    [Fact]
    public void Wallet_short_without_amounts_drops_the_parenthesis()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.PaymentRequired,
            """{"error":"insufficient integrator wallet balance","code":40175,"data":{"requiredCents":1980}}"""));

        Assert.Equal(40175, code);
        Assert.Equal("The integrator's wallet doesn't cover this. Top it up in the Vocdoni portal.", detail);
    }

    [Fact]
    public void Managed_org_cap_asks_for_a_top_up()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.PaymentRequired,
            """{"error":"top up to create more managed organizations","code":40184}"""));

        Assert.Equal(40184, code);
        Assert.Equal("Top up the integrator wallet in the Vocdoni portal to create another association.", detail);
    }

    [Fact]
    public void Other_codes_pass_the_upstream_text_through()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.BadRequest,
            """{"error":"anonymous voting is temporarily unavailable","code":40037}"""));

        Assert.Equal(40037, code);
        Assert.Equal("anonymous voting is temporarily unavailable", detail);
    }

    [Fact]
    public void Non_json_body_keeps_the_raw_message()
    {
        var ex = Upstream(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var (code, detail) = UpstreamErrors.Describe(ex);

        Assert.Null(code);
        Assert.Equal(ex.Message, detail);
        Assert.Null(ex.ErrorData);
    }

    [Fact]
    public void Transport_failure_keeps_its_message()
    {
        Assert.Equal((null, "connection refused"), UpstreamErrors.Describe(new HttpRequestException("connection refused")));
    }
}
