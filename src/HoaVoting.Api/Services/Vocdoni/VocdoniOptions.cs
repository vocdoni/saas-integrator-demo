namespace HoaVoting.Api.Services.Vocdoni;

public class VocdoniOptions
{
    public const string Section = "Vocdoni";

    /// <summary>Base URL of the Vocdoni SaaS backend, e.g. https://api-saas.vocdoni.net.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Vochain chain id (e.g. "vocdoni/DEV/36"). Needed by the voter to sign vote transactions; the
    /// #571 question read doesn't include it, so it's configured. Exposed to the public voting page.
    /// </summary>
    public string ChainId { get; set; } = "";

    /// <summary>
    /// Pre-provisioned API token (an integrator org's API key) sent as
    /// "Authorization: Bearer &lt;token&gt;" on every call. Needs the managed:write scope to
    /// create/delete managed orgs. The integrator org is resolved from this key (path-less endpoints).
    /// </summary>
    public string ApiToken { get; set; } = "";
}
