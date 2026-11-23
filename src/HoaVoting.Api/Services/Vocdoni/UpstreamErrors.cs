using System.Globalization;
using System.Text.Json;

namespace HoaVoting.Api.Services.Vocdoni;

/// <summary>
/// Turns an upstream failure into the <c>{code, detail}</c> of the 502 body (Program.cs). The billing
/// and limit codes the SPA shows verbatim get a readable sentence; anything else passes upstream's own
/// <c>error</c> text through, or the raw message when the body was not an <c>errors.Error</c>.
/// </summary>
internal static class UpstreamErrors
{
    public static (int? Code, string Detail) Describe(Exception ex)
    {
        if (ex is not VocdoniApiException v) return (null, ex.Message);
        var detail = v.Code switch
        {
            40175 => $"The integrator's wallet doesn't cover this{Amounts(v.ErrorData)}. Top it up in the Vocdoni portal.",
            40183 => "This association reached the spending cap set by its integrator.",
            40184 => "Top up the integrator wallet in the Vocdoni portal to create another association.",
            40176 => "Over 50 000 voters: this proposal needs a custom quote from Vocdoni.",
            _ => string.IsNullOrEmpty(v.UpstreamError) ? v.Message : v.UpstreamError,
        };
        return (v.Code, detail);
    }

    // " (needs €19.80, available €5.00)", or "" unless data carries both amounts.
    private static string Amounts(JsonElement? data) =>
        Cents(data, "requiredCents") is { } need && Cents(data, "availableCents") is { } have
            ? $" (needs {Eur(need)}, available {Eur(have)})"
            : "";

    private static long? Cents(JsonElement? data, string name) =>
        data is { ValueKind: JsonValueKind.Object } d
        && d.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : null;

    private static string Eur(long cents) => (cents / 100m).ToString("€0.00", CultureInfo.InvariantCulture);
}
