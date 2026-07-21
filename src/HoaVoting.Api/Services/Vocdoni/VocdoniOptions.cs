namespace HoaVoting.Api.Services.Vocdoni;

public class VocdoniOptions
{
    public const string Section = "Vocdoni";

    /// <summary>SaaS base URL the backend calls server-to-server, e.g. https://api-saas.vocdoni.net.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>
    /// Browser-facing SaaS base URL for the public voting page's client-side CSP/vote calls. Defaults
    /// to <see cref="ServerUrl"/>; override when the browser reaches the SaaS at a different address
    /// than the backend (local Docker: the backend uses host.docker.internal, the browser can't — use
    /// localhost).
    /// </summary>
    public string BrowserUrl { get; set; } = "";

    /// <summary>
    /// Pre-provisioned API token (an integrator org's API key) sent as
    /// "Authorization: Bearer &lt;token&gt;" on every call. Needs the managed:write scope to
    /// create/delete managed orgs. The integrator org is resolved from this key (path-less endpoints).
    /// </summary>
    public string ApiToken { get; set; } = "";
}
