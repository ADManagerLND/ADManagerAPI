using System.Text;
using System.Text.Encodings.Web;
using ADManagerAPI.Config;
using ADManagerAPI.Hubs;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;
using ADManagerAPI.Services.Teams;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

//──────────────── HOSTING (HTTPS avec fallback HTTP)
const int HttpsPort = 5022;
const int HttpPort = 5021;
var useHttps = false;

builder.WebHost.UseStaticWebAssets()
    .ConfigureKestrel(options =>
    {
        var certPath = Path.Combine(Directory.GetCurrentDirectory(), "certnew.cer");
        
        // Tentative de configuration HTTPS
        if (File.Exists(certPath))
        {
            try
            {
                var cert = new X509Certificate2(certPath);
                
                // Vérifier si le certificat a une clé privée
                if (cert.HasPrivateKey)
                {
                    options.ListenAnyIP(HttpsPort, listenOptions =>
                    {
                        listenOptions.UseHttps(cert);
                    });
                    useHttps = true;
                    Console.WriteLine($"✅ HTTPS configuré sur le port {HttpsPort} avec certificat 'certnew.cer'");
                }
                else
                {
                    Console.WriteLine($"⚠️ Le certificat 'certnew.cer' n'a pas de clé privée. Basculement vers HTTP.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Erreur lors du chargement du certificat 'certnew.cer': {ex.Message}");
                Console.WriteLine($"🔄 Basculement vers HTTP sur le port {HttpPort}");
            }
        }
        else
        {
            Console.WriteLine($"⚠️ Fichier certificat 'certnew.cer' non trouvé. Utilisation d'HTTP.");
        }
        
        // Configuration HTTP (fallback ou principal)
        if (!useHttps)
        {
            options.ListenAnyIP(HttpPort);
            Console.WriteLine($"🌐 HTTP configuré sur le port {HttpPort}");
        }
    });

//──────────────── MVC + JSON
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        o.JsonSerializerOptions.WriteIndented = true;
    });

//──────────────── UPLOAD (100 MB)
const long MaxUpload = 100 * 1024 * 1024;
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUpload;
    o.MemoryBufferThreshold = int.MaxValue;
});
builder.Services.Configure<KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = MaxUpload);

//──────────────── CORS pour réseau local
builder.Services.AddCors(opts =>
{
    opts.AddPolicy("AllowLocalhost", policy =>
        policy.SetIsOriginAllowed(origin =>
        {
            var uri = new Uri(origin);
            // Autoriser localhost et adresses IP privées
            return uri.IsLoopback || 
                   IsPrivateNetwork(uri.Host) ||
                   uri.Host.Equals("lycee.nd", StringComparison.OrdinalIgnoreCase);
        })
            .AllowAnyHeader() // ← règle clé pour SignalR
            .AllowAnyMethod()
            .AllowCredentials());
});

// Fonction helper pour détecter les réseaux privés
static bool IsPrivateNetwork(string host)
{
    if (System.Net.IPAddress.TryParse(host, out var ip))
    {
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               (bytes[0] == 10 || // 10.0.0.0/8
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || // 172.16.0.0/12
                (bytes[0] == 192 && bytes[1] == 168)); // 192.168.0.0/16
    }
    return false;
}

//──────────────── Swagger + JWT helper
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ADManagerAPI", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT – Authorization: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
                { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

//──────────────── DEPENDENCY INJECTION
builder.Services.AddSingleton<ILogService, LogService>();
builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddSingleton<ISignalRService, SignalRService>();
builder.Services.AddSingleton<ISpreadsheetDataParser, CsvParserService>();
builder.Services.AddSingleton<ISpreadsheetDataParser, ExcelParserService>();

builder.Services.AddScoped<ILdapService, LdapService>();
builder.Services.AddScoped<IFolderManagementService, FolderManagementService>();
builder.Services.AddScoped<ISpreadsheetImportService, SpreadsheetImportService>();
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Debug);

var clientSecret = builder.Configuration["AzureAD:ClientSecret"];

builder.Services.AddScoped<ITeamsGroupService, TeamsGroupService>();
builder.Services.AddScoped<ITeamsIntegrationService>(provider =>
{
    var ldapService = provider.GetRequiredService<ILdapService>();
    var logger = provider.GetRequiredService<ILogger<TeamsIntegrationService>>();
    var configService = provider.GetRequiredService<IConfigService>();
    var teamsService = provider.GetRequiredService<ITeamsGroupService>();
    
    return new TeamsIntegrationService(ldapService, logger, configService, teamsService);
});

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

//──────────────── AUTHENTICATION Azure AD
var aad = builder.Configuration.GetSection("AzureAd");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = $"https://login.microsoftonline.com/{aad["TenantId"]}/v2.0";
        o.Audience = aad["ClientId"];
        o.SaveToken = true;
        o.RequireHttpsMetadata = useHttps; // HTTPS requis seulement si disponible
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuers = new[]
            {
                $"https://login.microsoftonline.com/{aad["TenantId"]}/v2.0",
                $"https://sts.windows.net/{aad["TenantId"]}/"
            },
            ValidAudiences = new[] { aad["ClientId"], $"api://{aad["ClientId"]}" },
            NameClaimType = "name",
            RoleClaimType = "roles",
            ClockSkew = TimeSpan.FromMinutes(5)
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
    o.EnableDetailedErrors = true;
    o.MaximumReceiveMessageSize = null;
    o.StreamBufferCapacity = 20;
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
app.UseCors("AllowLocalhost"); // CORS d'abord
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<CsvImportHub>("/hubs/csvImportHub");
app.MapHub<NotificationHub>("/hubs/notificationHub");

var protocol = useHttps ? "https" : "http";
var port = useHttps ? HttpsPort : HttpPort;
app.Logger.LogInformation($"🚀 ADManagerAPI démarré sur {protocol}://0.0.0.0:{port} " + 
                         (useHttps ? "avec certificat certnew.cer" : "en mode HTTP"));
app.Run();

// Rendre la classe Program accessible pour les tests d'intégration
public partial class Program { }