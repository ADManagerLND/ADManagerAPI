using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ADManagerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class LogsController : ControllerBase
{
    private readonly LogService _logService;
    private readonly ISignalRService _signalRService;

    public LogsController(LogService logService, ISignalRService signalRService)
    {
        _logService = logService;
        _signalRService = signalRService;
    }

    [HttpGet]
    public IActionResult GetAllLogs()
    {
        var logs = _logService.GetAllLogs().ToList();
        return Ok(logs.AsEnumerable().Reverse());
    }

    [HttpDelete]
    public IActionResult ClearLogs()
    {
        _logService.ClearLogs();
        return Ok("Tous les logs ont été effacés.");
    }
}