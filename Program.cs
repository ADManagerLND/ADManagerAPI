using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Identity.Web;
using Microsoft.OpenApi.Models;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.WebSockets;
using ADManagerAPI.Models;

var builder = WebApplication.CreateBuilder(args);

// Désactiver les assets statiques
builder.WebHost.UseStaticWebAssets();

// Enregistrer les services de l'application
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
    options.AddPolicy("AllowAll", builder =>
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader()
               .SetIsOriginAllowed((host) => true));
});



builder.Services.AddSingleton<IConfigService, ConfigService>();



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
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddNegotiate()
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = options.DefaultPolicy;
    });
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

app.UseCors("AllowAll");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();


app.Run();
