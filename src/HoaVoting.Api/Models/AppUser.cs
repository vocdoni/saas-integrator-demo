namespace HoaVoting.Api.Models;

public enum AppRole
{
    SuperAdmin,
    Owner,
}

/// <summary>
/// Application-level identity. Homeowners are NOT app users — they live in Vocdoni as org
/// members/census participants. App users are the single SuperAdmin and per-association Owners.
/// </summary>
public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public AppRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
