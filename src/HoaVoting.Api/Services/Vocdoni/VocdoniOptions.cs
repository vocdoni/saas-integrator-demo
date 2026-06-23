namespace HoaVoting.Api.Services.Vocdoni;

public class VocdoniOptions
{
    public const string Section = "Vocdoni";

    /// <summary>Base URL of the Vocdoni SaaS backend, e.g. https://api-saas.vocdoni.net.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Pre-provisioned API token (an integrator org's API key) sent as
    /// "Authorization: Bearer &lt;token&gt;" on every call.
    /// </summary>
    public string ApiToken { get; set; } = "";

    /// <summary>
    /// Address of the integrator organization the <see cref="ApiToken"/> is scoped to. Associations
    /// are created as managed orgs under it via POST /organizations/{IntegratorAddress}/managed.
    /// </summary>
    public string IntegratorAddress { get; set; } = "";
}
