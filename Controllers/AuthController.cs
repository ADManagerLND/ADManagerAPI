using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    [HttpGet("windows")]
    [Authorize(AuthenticationSchemes = "Negotiate")]
    public IActionResult GetWindowsUser()
    {
        return Ok(new
        {
            Username = User.Identity.Name
        });
    }

    [HttpGet("azure")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult GetAzureUser()
    {
        return Ok(new
        {
            Username = User.Identity.Name
        });
    }
}