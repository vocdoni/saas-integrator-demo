using System.IdentityModel.Tokens.Jwt;
using System.Text;
using HoaVoting.Api.Data;
using HoaVoting.Api.Services.Auth;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    // Serialize enums (e.g. VotingType) as camelCase strings — "single" / "multiple" / "ranked".
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=hoavoting.db"));

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<VocdoniOptions>(builder.Configuration.GetSection(VocdoniOptions.Section));
builder.Services.AddSingleton<JwtTokenService>();

// Vocdoni typed client. The pre-provisioned API token is attached as the default Bearer header.
builder.Services.AddHttpClient<IVocdoniClient, VocdoniClient>((sp, http) =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VocdoniOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl);
    if (!string.IsNullOrEmpty(o.ApiToken))
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.ApiToken);
});

// Keep JWT claim types verbatim ("sub", role) instead of the legacy SOAP remapping.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            NameClaimType = "sub",
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Map Vocdoni upstream failures to 502 instead of leaking a 500 + stack trace.
app.UseExceptionHandler(eh => eh.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    if (ex is VocdoniApiException or HttpRequestException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new { error = "vocdoni_upstream", detail = ex.Message });
    }
    else
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new { error = "internal_error" });
    }
}));

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await DbSeeder.SeedAsync(app.Services, app.Configuration);

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
