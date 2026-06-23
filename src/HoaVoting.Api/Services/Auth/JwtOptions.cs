namespace HoaVoting.Api.Services.Auth;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "hoa-voting";
    public string Audience { get; set; } = "hoa-voting";
    public string SigningKey { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 480;
}
