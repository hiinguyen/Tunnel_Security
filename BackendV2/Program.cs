using BackendV2.Data;
using Microsoft.EntityFrameworkCore;
using BackendV2.Hubs;
using BackendV2.Services;
using BackendV2.Middlewares;
using BackendV2.Auth;
using BackendV2.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

// ── Auth (JWT bearer + roles/permissions) ─────────────────────────────────────
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<LoginSecuritySettings>(builder.Configuration.GetSection("LoginSecurity"));

var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
        };

        // SignalR WebSocket connections can't set an Authorization header, so accept
        // the access token via query string for the hub path.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs/sensors"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<IAuthService, AuthService>();

// ── TimescaleDB / PostgreSQL (time-series Readings) ───────────────────────────
// Enabled by TimeSeries:Enabled flag. App starts fine without TimescaleDB running;
// Reading writes gracefully degrade.
if (builder.Configuration.GetValue<bool>("TimeSeries:Enabled"))
{
    var tsConnStr = builder.Configuration.GetConnectionString("TimeSeries")!;
    builder.Services.AddDbContext<TimeSeriesDbContext>(options => options.UseNpgsql(tsConnStr));
}

builder.Services.AddHostedService<MqttSubscriberService>();

builder.Services.AddSingleton<CameraRelayRegistry>();

var app = builder.Build();

// TimescaleDB — EnsureCreated + hypertable init (best-effort; app still starts if Postgres is down)
using (var scope = app.Services.CreateScope())
{
    var ts = scope.ServiceProvider.GetService<TimeSeriesDbContext>();
    if (ts != null)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            await TimescaleInitializer.InitializeAsync(ts, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Startup] TimescaleDB initialization failed: {Msg} — " +
                              "time-series writes will be skipped", ex.Message);
        }
    }
}

// Auth — seed standard roles/permissions and bootstrap the initial admin user
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
    await AuthSeeder.SeedAsync(db, passwordHasher, app.Configuration);
    await TopologySeeder.SeedAsync(db);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.UseStaticFiles();
app.UseWebSockets();
app.UseMiddleware<CameraIngestMiddleware>();
app.UseMiddleware<CameraViewMiddleware>();

app.MapControllers();

app.MapHub<SensorHub>("/hubs/sensors");

app.Run();
