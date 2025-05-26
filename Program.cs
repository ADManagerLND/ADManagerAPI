using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Identity.Web;
using Microsoft.OpenApi.Models;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Hubs;
using ADManagerAPI.Services.Parse;
using OfficeOpenXml;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();


builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "ADManagerAPI", 
        Version = "v1" 
    });
});

builder.Services.AddCors(options =>
{
    // Politique plus restrictive et adaptée pour SignalR et les API
    options.AddPolicy("AllowSpecificOrigin", builder =>
        builder.WithOrigins("http://localhost:5173") // Remplacez par l'URL de votre frontend si différente
               .AllowAnyMethod()
               .AllowAnyHeader()
               .AllowCredentials()); // Crucial pour SignalR
});

// Enregistrement de SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = null; // Pas de limite pour les messages
    options.StreamBufferCapacity = 20; // Pour le streaming
});

// Enregistrement des services
builder.Services.AddSingleton<LogService>();
builder.Services.AddScoped<ILdapService, LdapService>();
builder.Services.AddScoped<ILogService, LogService>();
builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddScoped<ICsvManagerService, CsvManagerService>();
builder.Services.AddSingleton<ISignalRService, SignalRService>();

//PARSER
builder.Services.AddSingleton<ISpreadsheetParserService, CsvParserService>();
builder.Services.AddSingleton<ISpreadsheetParserService, ExcelParserService>();
builder.Services.AddScoped<ICsvManagerService, CsvManagerService>();


if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();

    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();
        options.FallbackPolicy = options.DefaultPolicy;
    });
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

string configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
if (!Directory.Exists(configDir))
{
    Directory.CreateDirectory(configDir);
    app.Logger.LogInformation($"Répertoire de configuration créé: {configDir}");
}

app.UseCors("AllowSpecificOrigin");

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHub<CsvImportHub>("/csvImportHub");
app.MapHub<NotificationHub>("/notificationHub");


if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("UseMocks", false))
{
    app.Logger.LogWarning("Services mocks activés pour le développement");
}

app.Run();
