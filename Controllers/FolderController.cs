using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace ADManagerAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FolderController : ControllerBase
    {
        private readonly IFolderManagementService _folderService;
        private readonly ILogger<FolderController> _logger;

        public FolderController(IFolderManagementService folderService, ILogger<FolderController> logger)
        {
            _folderService = folderService;
            _logger = logger;
        }

        [HttpPost("student")]
        public async Task<IActionResult> CreateStudentFolder([FromBody] StudentFolderRequest request)
        {
            if (request == null || request.Student == null || string.IsNullOrWhiteSpace(request.TemplateName))
            {
                _logger.LogWarning("Invalid request for creating student folder.");
                return BadRequest("Invalid request data. Student information, template name, and role are required.");
            }

            _logger.LogInformation($"Request received to create folder for student: {request.Student.Name} (Role: {request.Role}) using template: {request.TemplateName}");
            var success = await _folderService.CreateStudentFolderAsync(request.Student, request.TemplateName, request.Role);
            if (success)
            {
                return Ok(new { message = $"Folder for student {request.Student.Name} created successfully." });
            }
            return StatusCode(500, new { message = $"Failed to create folder for student {request.Student.Name}." });
        }

        [HttpPost("classgroup")]
        public async Task<IActionResult> CreateClassGroupFolder([FromBody] ClassGroupFolderRequest request)
        {
            if (request == null || request.ClassGroup == null || string.IsNullOrWhiteSpace(request.TemplateName))
            {
                _logger.LogWarning("Invalid request for creating class group folder.");
                return BadRequest("Invalid request data. Class group information and template name are required.");
            }

            _logger.LogInformation($"Request received to create folder for class group: {request.ClassGroup.Name} using template: {request.TemplateName}");
            var success = await _folderService.CreateClassGroupFolderAsync(request.ClassGroup, request.TemplateName);
            if (success)
            {
                return Ok(new { message = $"Folder for class group {request.ClassGroup.Name} created successfully." });
            }
            return StatusCode(500, new { message = $"Failed to create folder for class group {request.ClassGroup.Name}." });
        }
    }

    // Request Models for the controller
    public class StudentFolderRequest
    {
        public StudentInfo Student { get; set; }
        public string TemplateName { get; set; }
        public UserRole Role { get; set; }
    }

    public class ClassGroupFolderRequest
    {
        public ClassGroupInfo ClassGroup { get; set; }
        public string TemplateName { get; set; }
    }
} 