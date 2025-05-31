using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Hubs;
using ADManagerAPI.Services.Parse;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using ADManagerAPI.Config;

// Récupérer la première adresse IPv4 non-loopback
IPAddress localIP = Dns.GetHostAddresses(Dns.GetHostName())
    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(ip));

if (localIP == null)
{
    localIP = IPAddress.Loopback;
    Console.WriteLine("Aucune IP IPv4 trouvée, utilisation de 127.0.0.1.");
}

var ipAddressString = localIP.ToString();
Console.WriteLine($"Adresse IP locale utilisée: {ipAddressString}");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

// Définir les ports d'écoute
var port = 5021;
builder.WebHost.UseUrls($"http://{ipAddressString}:{port}", $"http://localhost:{port}");

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
    options.AddPolicy("AllowSpecificOrigin", builder =>
        builder.WithOrigins(
                "http://localhost:5173",  // Vite dev server
                "http://localhost:5174",  // Fallback port
                "http://localhost:4173",  // Vite preview
                "http://localhost:3000",   // Autre port possible
                "http://127.0.0.1:5173",  // Variante localhost
                "http://127.0.0.1:5174",  // Variante localhost
                "http://127.0.0.1:3000"   // Variante localhost
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
            .SetIsOriginAllowed(origin => true)); // Permettre toutes les origines pour le débogage
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
builder.Services.AddScoped<IFolderManagementService, FolderManagementService>();
builder.Services.AddScoped<ISpreadsheetImportService, SpreadsheetImportService>();
builder.Services.AddSingleton<ISignalRService, SignalRService>();

//PARSER
builder.Services.AddSingleton<ISpreadsheetParserService, CsvParserService>();
builder.Services.AddSingleton<ISpreadsheetParserService, ExcelParserService>();
builder.Services.AddScoped<ISpreadsheetImportService, SpreadsheetImportService>();

// Configuration de l'authentification Azure AD
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.Authority = "https://login.microsoftonline.com/a70e01a3-ae69-4d17-ad6d-407f168bb45e/v2.0";
    options.Audience = "api://114717d2-5cae-4569-900a-efa4e58eb3f5";
    options.RequireHttpsMetadata = false; // Pour le développement
    options.SaveToken = true;
    
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuers = new[]
        {
            "https://login.microsoftonline.com/a70e01a3-ae69-4d17-ad6d-407f168bb45e/v2.0",
            "https://sts.windows.net/a70e01a3-ae69-4d17-ad6d-407f168bb45e/"
        },
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidAudiences = new[] { 
            "api://114717d2-5cae-4569-900a-efa4e58eb3f5",
            "114717d2-5cae-4569-900a-efa4e58eb3f5"  // Ajout du GUID seul pour supporter les deux formats
        },
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromMinutes(5) // Tolérance pour les différences d'horloge
    };
    
    // Logs détaillés pour le débogage de l'authentification
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            Console.WriteLine($"Échec d'authentification: {context.Exception.Message}");
            return Task.CompletedTask;
        },
        OnTokenValidated = context =>
        {
            Console.WriteLine($"Token validé pour: {context.Principal?.Identity?.Name}");
            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            Console.WriteLine($"Challenge d'authentification émis: {context.Error}");
            return Task.CompletedTask;
        },
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
                Console.WriteLine("Token extrait du paramètre de requête pour SignalR");
            }
            
            return Task.CompletedTask;
        }
    };
});

// Configuration de l'autorisation - les utilisateurs doivent être authentifiés par défaut
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser() // Exiger un utilisateur authentifié
        .Build();
    
    // Configurer la politique par défaut mais avec des exceptions
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
        
    // Ajouter une politique anonyme pour les endpoints publics
    options.AddPolicy("AllowAnonymous", policy => policy.RequireAssertion(_ => true));
});

builder.Services.AddDataProtection()
    .SetApplicationName("ADManagerAPI")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ADManagerAPI", "Keys")));

// EncryptionHelper n'a pas de dépendances sur IConfigService, donc on peut l'enregistrer en premier
builder.Services.AddSingleton<EncryptionHelper>();

// Enregistrer d'abord IConfigService comme singleton pour que LdapSettingsProvider puisse l'utiliser
builder.Services.AddSingleton<IConfigService, ConfigService>();

// Puis enregistrer LdapSettingsProvider qui dépend de IConfigService
builder.Services.AddSingleton<LdapSettingsProvider>();

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

// IMPORTANT: Appliquer CORS avant l'authentification pour les requêtes préflight
app.UseCors("AllowSpecificOrigin");

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Middleware de journalisation pour le débogage
app.Use(async (context, next) =>
{
    app.Logger.LogInformation($"Requête depuis: {context.Connection.RemoteIpAddress} - Chemin: {context.Request.Path}");

    // Exempter certains endpoints de l'authentification
    var path = context.Request.Path.Value?.ToLower();
    if (path == "/api/test/public")
    {
        app.Logger.LogInformation("Endpoint public détecté, contournement de l'authentification");
        context.Request.Headers["SkipAuthorization"] = "true";
    }

    // Log des headers pour le débogage
    if (app.Environment.IsDevelopment())
    {
        app.Logger.LogDebug("Headers de la requête:");
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.ToLower() != "authorization") // Ne pas logger le token complet
            {
                app.Logger.LogDebug($"  {header.Key}: {header.Value}");
            }
            else
            {
                app.Logger.LogDebug($"  {header.Key}: [PRÉSENT]");
            }
        }
    }

    if (context.User.Identity != null && context.User.Identity.IsAuthenticated)
    {
        app.Logger.LogInformation($"Utilisateur authentifié: {context.User.Identity.Name}");
        
        // Afficher les claims pour le débogage
        if (app.Environment.IsDevelopment())
        {
            foreach (var claim in context.User.Claims)
            {
                app.Logger.LogDebug($"  Claim: {claim.Type} = {claim.Value}");
            }
        }
    }
    else
    {
        app.Logger.LogInformation("Utilisateur non authentifié");
    }

    await next();
});

app.MapControllers();

app.MapHub<CsvImportHub>("/hubs/csvImportHub");
app.MapHub<NotificationHub>("/hubs/notificationHub");

app.Run();
