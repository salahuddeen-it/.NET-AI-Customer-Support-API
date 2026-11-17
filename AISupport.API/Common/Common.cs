using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AISupport.API.Models;

namespace AISupport.API.Common;

public class ApiResponse<T>
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public T? Data { get; init; }
    public IDictionary<string, string[]>? Errors { get; init; }

    public static ApiResponse<T> Ok(T? data, string message = "Request completed successfully.") => new() { Success = true, Message = message, Data = data };
    public static ApiResponse<T> Fail(string message, IDictionary<string, string[]>? errors = null) => new() { Success = false, Message = message, Errors = errors };
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNextPage => PageNumber < TotalPages;
}

public class PaginationQuery
{
    [Range(1, int.MaxValue)] public int PageNumber { get; set; } = 1;
    [Range(1, 50)] public int PageSize { get; set; } = 10;
}

// ---------------- Exceptions ----------------

public class AppException : Exception
{
    public int StatusCode { get; }
    public AppException(string message, int statusCode = 400) : base(message) => StatusCode = statusCode;
}
public class BadRequestException : AppException { public BadRequestException(string m) : base(m, 400) { } }
public class UnauthorizedException : AppException { public UnauthorizedException(string m) : base(m, 401) { } }
public class ForbiddenException : AppException { public ForbiddenException(string m) : base(m, 403) { } }
public class NotFoundException : AppException { public NotFoundException(string m) : base(m, 404) { } }
public class ConflictException : AppException { public ConflictException(string m) : base(m, 409) { } }

// ---------------- Current user ----------------

public static class Roles
{
    public const string Admin = nameof(UserRole.Admin);
    public const string Agent = nameof(UserRole.Agent);
    public const string Customer = nameof(UserRole.Customer);
    public const string Staff = Agent + "," + Admin;
}

public record CurrentUser(int Id, string Name, UserRole Role)
{
    public bool IsStaff => Role is UserRole.Agent or UserRole.Admin;
}

public static class ClaimsPrincipalExtensions
{
    public static CurrentUser ToCurrentUser(this ClaimsPrincipal p)
    {
        if (!int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            throw new UnauthorizedException("Invalid token.");
        var role = Enum.TryParse<UserRole>(p.FindFirstValue(ClaimTypes.Role), out var r) ? r : UserRole.Customer;
        return new CurrentUser(id, p.FindFirstValue(ClaimTypes.Name) ?? "User", role);
    }
}

/// <summary>Always writes DateTime values as UTC ISO-8601 (with "Z") so clients show correct local times.</summary>
public class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Local ? DateTimeKind.Local : DateTimeKind.Utc).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
}
