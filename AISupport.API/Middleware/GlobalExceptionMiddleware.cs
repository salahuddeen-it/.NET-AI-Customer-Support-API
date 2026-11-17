using System.Text.Json;
using System.Text.Json.Serialization;
using AISupport.API.Common;

namespace AISupport.API.Middleware;

/// <summary>Turns every exception into a consistent JSON error with the right HTTP status code.</summary>
public class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var (status, message) = ex switch
            {
                AppException app => (app.StatusCode, app.Message),
                OperationCanceledException => (499, "The request was cancelled."),
                _ => (500, _env.IsDevelopment() ? ex.Message : "An unexpected error occurred. Please try again later.")
            };

            if (status >= 500) _logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            else _logger.LogWarning("{Status} {Path}: {Message}", status, context.Request.Path, message);

            if (context.Response.HasStarted) return;
            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse<object>.Fail(message), JsonOptions));
        }
    }
}
