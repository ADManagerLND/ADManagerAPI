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
        //    var success = await _folderService.CreateStudentFolderAsync(request.Student, request.TemplateName, request.Role);
            /*if (success)
            {
                return Ok(new { message = $"Folder for student {request.Student.Name} created successfully." });
            }*/
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
        //    var success = await _folderService.CreateClassGroupFolderAsync(request.ClassGroup, request.TemplateName);
            /*if (success)
            {
                return Ok(new { message = $"Folder for class group {request.ClassGroup.Name} created successfully." });
            }*/
            return StatusCode(500, new { message = $"Failed to create folder for class group {request.ClassGroup.Name}." });
        }

        [HttpPost("students/batch")]
        public async Task<IActionResult> CreateStudentFoldersBatch([FromBody] StudentFolderBatchRequest request)
        {
            if (request == null || request.Students == null || !request.Students.Any() || string.IsNullOrWhiteSpace(request.TemplateName))
            {
                _logger.LogWarning("Invalid batch request for creating student folders.");
                return BadRequest("Invalid request data. Students list, template name, and role are required.");
            }

            _logger.LogInformation($"Batch request received to create folders for {request.Students.Count()} students using template: {request.TemplateName}");
            
            try
            {
                // Utiliser la méthode optimisée qui sélectionne automatiquement batch vs traditionnel
              //  await _folderService.ProvisionStudentsAsync(request.Students, request.TemplateName, request.Role, request.MaxParallelism);
                
                return Ok(new { 
                    message = $"Batch creation completed for {request.Students.Count()} students.",
                    count = request.Students.Count(),
                    templateUsed = request.TemplateName,
                    role = request.Role.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create batch student folders");
                return StatusCode(500, new { 
                    message = $"Failed to create folders for students batch.", 
                    error = ex.Message 
                });
            }
        }

        [HttpPost("students/batch-optimized")]
        public async Task<IActionResult> CreateStudentFoldersBatchOptimized([FromBody] StudentFolderBatchOptimizedRequest request)
        {
            if (request == null || request.Students == null || !request.Students.Any() || string.IsNullOrWhiteSpace(request.TemplateName))
            {
                _logger.LogWarning("Invalid optimized batch request for creating student folders.");
                return BadRequest("Invalid request data. Students list, template name, and role are required.");
            }

            _logger.LogInformation($"Optimized batch request received to create folders for {request.Students.Count()} students (batch size: {request.BatchSize}) using template: {request.TemplateName}");
            
            try
            {
               // await _folderService.ProvisionStudentsBatchOptimized(request.Students, request.TemplateName, request.Role, request.BatchSize);
                
                return Ok(new { 
                    message = $"Optimized batch creation completed for {request.Students.Count()} students.",
                    count = request.Students.Count(),
                    batchSize = request.BatchSize,
                    templateUsed = request.TemplateName,
                    role = request.Role.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create optimized batch student folders");
                return StatusCode(500, new { 
                    message = $"Failed to create folders for students optimized batch.", 
                    error = ex.Message 
                });
            }
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

    public class StudentFolderBatchRequest
    {
        public IEnumerable<StudentInfo> Students { get; set; }
        public string TemplateName { get; set; }
        public UserRole Role { get; set; }
        public int? MaxParallelism { get; set; } = null;
    }

    public class StudentFolderBatchOptimizedRequest
    {
        public IEnumerable<StudentInfo> Students { get; set; }
        public string TemplateName { get; set; }
        public UserRole Role { get; set; }
        public int BatchSize { get; set; } = 50;
    }
} 