using System.Text;
using System.Text.Encodings.Web;
using ADManagerAPI.Config;
using ADManagerAPI.Hubs;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

//──────────────── HOSTING
const int Port = 5022;
builder.WebHost.UseStaticWebAssets().UseUrls($"http://localhost:{Port}");

//──────────────── MVC + JSON
builder.Services.AddControllers()
       .AddJsonOptions(o =>
       {
           o.JsonSerializerOptions.Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
           o.JsonSerializerOptions.WriteIndented = true;
       });

//──────────────── UPLOAD (100 MB)
const long MaxUpload = 100 * 1024 * 1024;
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUpload;
    o.MemoryBufferThreshold    = int.MaxValue;
});
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = MaxUpload);

//──────────────── CORS localhost
builder.Services.AddCors(opts =>
{
    opts.AddPolicy("AllowLocalhost", policy =>
        policy.SetIsOriginAllowed(origin => new Uri(origin).IsLoopback)
              .AllowAnyHeader()     // ← règle clé pour SignalR
              .AllowAnyMethod()
              .AllowCredentials());
});

//──────────────── Swagger + JWT helper
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ADManagerAPI", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT – Authorization: Bearer {token}",
        Name        = "Authorization",
        In          = ParameterLocation.Header,
        Type        = SecuritySchemeType.ApiKey,
        Scheme      = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme
            { Reference = new OpenApiReference{ Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
          Array.Empty<string>() }
    });
});

//──────────────── DEPENDENCY INJECTION
builder.Services.AddSingleton<ILogService,            LogService>();
builder.Services.AddSingleton<IConfigService,         ConfigService>();
builder.Services.AddSingleton<ISignalRService,        SignalRService>();
builder.Services.AddSingleton<ISpreadsheetDataParser, CsvParserService>();
builder.Services.AddSingleton<ISpreadsheetDataParser, ExcelParserService>();

builder.Services.AddScoped<ILdapService,              LdapService>();
builder.Services.AddScoped<IFolderManagementService,  FolderManagementService>();
builder.Services.AddScoped<ISpreadsheetImportService, SpreadsheetImportService>();
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel",            LogLevel.Debug);
// Teams (conditionnel) — inchangé

var clientSecret      = builder.Configuration["AzureAD:ClientSecret"];


builder.Services.AddScoped<ITeamsGroupService,    ADManagerAPI.Services.Teams.TeamsGroupService>();
builder.Services.AddScoped<ITeamsIntegrationService, ADManagerAPI.Services.Teams.TeamsIntegrationService>();


Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
//──────────────── AUTHENTICATION Azure AD
var aad = builder.Configuration.GetSection("AzureAd");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(o =>
       {
           o.Authority            = $"https://login.microsoftonline.com/{aad["TenantId"]}/v2.0";
           o.Audience             = aad["ClientId"];
           o.SaveToken            = true;
           o.RequireHttpsMetadata = false; // dev only
           o.TokenValidationParameters = new TokenValidationParameters
           {
               ValidIssuers = new[]
               {
                   $"https://login.microsoftonline.com/{aad["TenantId"]}/v2.0",
                   $"https://sts.windows.net/{aad["TenantId"]}/"
               },
               ValidAudiences = new[] { aad["ClientId"], $"api://{aad["ClientId"]}" },
               NameClaimType  = "name",
               RoleClaimType  = "roles",
               ClockSkew      = TimeSpan.FromMinutes(5)
           };
       });
builder.Services.AddAuthorization();

//──────────────── DATA-PROTECTION + helpers
builder.Services.AddDataProtection()
       .SetApplicationName("ADManagerAPI")
       .PersistKeysToFileSystem(
           new DirectoryInfo(Path.Combine(
               Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
               "ADManagerAPI", "Keys")));
builder.Services.AddSingleton<EncryptionHelper>();
builder.Services.AddSingleton<LdapSettingsProvider>();

//──────────────── SIGNALR
builder.Services.AddSignalR(o =>
{
    o.EnableDetailedErrors      = true;
    o.MaximumReceiveMessageSize = null;
    o.StreamBufferCapacity      = 20;
});

//──────────────── PIPELINE
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Chrome PNA — header « Access-Control-Allow-Private-Network »
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    await next();
});

app.UseRouting();
app.UseCors("AllowLocalhost");   // CORS d’abord
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<CsvImportHub>   ("/hubs/csvImportHub");
app.MapHub<NotificationHub>("/hubs/notificationHub");

app.Logger.LogInformation($"ADManagerAPI démarré sur http://localhost:{Port}");
app.Run();
