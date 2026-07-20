namespace HoaVoting.Api.Services.Vocdoni;

public class VocdoniOptions
{
    public const string Section = "Vocdoni";

    /// <summary>Base URL of the Vocdoni SaaS backend, e.g. https://api-saas.vocdoni.net. Used by the backend itself.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Browser-facing SaaS base URL for the public voting page's client-side CSP/vote calls. Defaults
    /// to <see cref="BaseUrl"/>; override when the backend reaches the SaaS at a different address than
    /// the browser (local Docker: backend uses host.docker.internal, the browser can't — use localhost).
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// Pre-provisioned API token (an integrator org's API key) sent as
    /// "Authorization: Bearer &lt;token&gt;" on every call. Needs the managed:write scope to
    /// create/delete managed orgs. The integrator org is resolved from this key (path-less endpoints).
    /// </summary>
    public string ApiToken { get; set; } = "";
}
