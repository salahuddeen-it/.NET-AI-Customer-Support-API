using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AISupport.API.AI;
using AISupport.API.Common;
using AISupport.API.Data;
using AISupport.API.Middleware;
using AISupport.API.Models;
using AISupport.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------- Settings ----------
builder.Services.Configure<JwtSettings>(config.GetSection("JwtSettings"));
builder.Services.Configure<AiSettings>(config.GetSection("AiSettings"));
var jwt = config.GetSection("JwtSettings").Get<JwtSettings>() ?? throw new InvalidOperationException("JwtSettings missing.");

// ---------- Database ----------
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(config.GetConnectionString("DefaultConnection")));
builder.Services.AddMemoryCache();

// ---------- AI provider (Gemini / OpenAI, or offline Demo when no API key) ----------
builder.Services.AddHttpClient(LlmSupportAiBase.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<ISupportAi>(sp =>
{
    var ai = sp.GetRequiredService<IOptions<AiSettings>>().Value;
    if (!ai.IsConfigured) return ActivatorUtilities.CreateInstance<DemoSupportAi>(sp);
    return ai.IsOpenAi
        ? (ISupportAi)ActivatorUtilities.CreateInstance<OpenAiSupportAi>(sp)
        : ActivatorUtilities.CreateInstance<GeminiSupportAi>(sp);
});

// ---------- Application services ----------
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IKnowledgeService, KnowledgeService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

// ---------- JWT authentication ----------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
        o.Events = new JwtBearerEvents
        {
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(ApiResponse<object>.Fail("Authentication required. Please login and send a valid JWT token."));
            },
            OnForbidden = async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(ApiResponse<object>.Fail("You do not have permission to perform this action."));
            }
        };
    });
builder.Services.AddAuthorization();

// ---------- Rate limiting (protects the paid LLM API from abuse) ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            ApiResponse<object>.Fail("Too many messages. Please wait a minute and try again."), token);
    };
});

// ---------- Controllers + uniform validation errors ----------
builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var errors = ctx.ModelState
                .Where(e => e.Value is { Errors.Count: > 0 })
                .ToDictionary(
                    e => string.IsNullOrEmpty(e.Key) ? "request" : e.Key,
                    e => e.Value!.Errors.Select(x => string.IsNullOrWhiteSpace(x.ErrorMessage) ? "Invalid value." : x.ErrorMessage).ToArray());
            return new BadRequestObjectResult(ApiResponse<object>.Fail("One or more validation errors occurred.", errors));
        };
    });

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AI Customer Support API",
        Version = "v1",
        Description = """
            AI-powered customer-support backend: **LLM chat, intent & sentiment detection, knowledge-base answers, human escalation and analytics**.

            👉 **Try the live chat playground:** open [`/`](/) in your browser.

            **Quick start:** `POST /api/auth/login` → copy `token` → **Authorize** 🔒 → `POST /api/chat` with `{ "message": "Where is my order?" }`

            | Role | Email | Password |
            |------|-------|----------|
            | Customer | customer@support.ai | Customer@123 |
            | Agent | agent@support.ai | Agent@123 |
            | Admin | admin@support.ai | Admin@123 |
            """,
        Contact = new OpenApiContact { Name = "Syed Muhammad Shakeel", Url = new Uri("https://github.com/salahuddeen-it") }
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT token (without 'Bearer')."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml, includeControllerXmlComments: true);
});

var app = builder.Build();

// ---------- Database + demo data ----------
using (var scope = app.Services.CreateScope())
{
    await DbSeeder.SeedAsync(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
        app.Logger);
    app.Logger.LogInformation("AI provider: {Provider}", scope.ServiceProvider.GetRequiredService<ISupportAi>().ProviderName);
}

// ---------- Pipeline ----------
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "AI Support API v1");
    c.DocumentTitle = "AI Customer Support API";
    c.InjectStylesheet("/swagger-ui/custom.css");
    c.DocExpansion(DocExpansion.List);
    c.DefaultModelsExpandDepth(-1);
    c.DisplayRequestDuration();
    c.EnableFilter();
    c.EnablePersistAuthorization();
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();
