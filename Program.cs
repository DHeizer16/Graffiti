using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using GlobalGraffitiWall.API;
using GlobalGraffitiWall.API.HealthChecks;
using GlobalGraffitiWall.API.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// 1. Enable CORS for local testing
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(_ => true)
              .AllowCredentials();
    });
});

builder.Services.AddControllers();
builder.Services.AddSignalR();

// JWT Authentication
var jwtSecret = builder.Configuration["JwtSettings:Secret"] ?? "GlobalGraffitiWall_SuperSecretKey_ForSigningTokens_2026!#*CustomJwtKey789";
var jwtIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "GlobalGraffitiWall";
var jwtAudience = builder.Configuration["JwtSettings:Audience"] ?? "GlobalGraffitiWallClient";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
        };

        // Support WebSockets / SignalR sending token via access_token query param
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/canvas"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

// Redis
string redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnectionString));

// Canvas Infrastructure & Security
builder.Services.AddSingleton<CanvasMetrics>();
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck<SqlServerHealthCheck>("sqlserver", tags: ["ready"]);

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<CanvasRepository>();
builder.Services.AddSingleton<ModerationService>();
builder.Services.AddSingleton<WallService>();
builder.Services.AddSingleton<ReservationService>();
builder.Services.AddSingleton<PaletteService>();
builder.Services.AddSingleton<CanvasResetService>();
builder.Services.AddSingleton<PixelPlacementQueue>();
builder.Services.AddHostedService<PixelBatchWriterService>();
builder.Services.AddHostedService<CanvasInitializerService>();
builder.Services.AddHostedService<ReservationExpirationService>();
builder.Services.AddHostedService<CanvasResetWorkerService>();

var app = builder.Build();

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate, max-age=0");
        ctx.Context.Response.Headers.Append("Pragma", "no-cache");
        ctx.Context.Response.Headers.Append("Expires", "0");
    }
});
app.MapControllers();

// Health Check Endpoints
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteResponse
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteResponse
});
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

// SignalR Hub Endpoint
app.MapHub<CanvasHub>("/hubs/canvas");

app.Run();