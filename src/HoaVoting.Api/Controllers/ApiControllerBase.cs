using System.Security.Claims;
using HoaVoting.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace HoaVoting.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    protected int CurrentUserId => int.Parse(User.FindFirstValue("sub")!);

    protected AppRole CurrentRole => Enum.Parse<AppRole>(User.FindFirstValue(ClaimTypes.Role)!);
}
