using HoaVoting.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Api.Data;

public static class DbSeeder
{
    /// <summary>Applies migrations and seeds the single SuperAdmin from config if missing.</summary>
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var email = config["Admin:Email"];
        var password = config["Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        if (await db.Users.AnyAsync(u => u.Email == email))
            return;

        var admin = new AppUser { Email = email, Role = AppRole.SuperAdmin, CreatedAt = DateTimeOffset.UtcNow };
        admin.PasswordHash = new PasswordHasher<AppUser>().HashPassword(admin, password);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }
}
