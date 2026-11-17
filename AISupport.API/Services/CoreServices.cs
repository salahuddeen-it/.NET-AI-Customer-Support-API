using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using AISupport.API.Common;
using AISupport.API.Data;
using AISupport.API.DTOs;
using AISupport.API.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AISupport.API.Services;

// =====================================================================
//  MAPPING
// =====================================================================
public static class Mapping
{
    public static UserDto ToDto(this User u) => new()
    {
        Id = u.Id, FullName = u.FullName, Email = u.Email, Role = u.Role, IsActive = u.IsActive, CreatedAt = u.CreatedAt
    };

    public static MessageDto ToDto(this Message m) => new()
    {
        Id = m.Id, Role = m.Role, Content = m.Content, SenderName = m.SenderName,
        Intent = m.Intent, Sentiment = m.Sentiment, Confidence = m.Confidence, CreatedAt = m.CreatedAt
    };

    public static EscalationSummaryDto ToSummaryDto(this Escalation e) => new()
    {
        Id = e.Id, Reason = e.Reason, Trigger = e.Trigger, Priority = e.Priority, Status = e.Status,
        AssignedAgentName = e.AssignedAgent?.FullName, CreatedAt = e.CreatedAt
    };

    public static EscalationDto ToDto(this Escalation e) => new()
    {
        Id = e.Id, Reason = e.Reason, Trigger = e.Trigger, Priority = e.Priority, Status = e.Status,
        AssignedAgentName = e.AssignedAgent?.FullName, CreatedAt = e.CreatedAt,
        ConversationId = e.ConversationId,
        ConversationTitle = e.Conversation?.Title ?? string.Empty,
        CustomerName = e.Conversation?.User?.FullName ?? string.Empty,
        Intent = e.Conversation?.CurrentIntent,
        Sentiment = e.Conversation?.LastSentiment,
        ResolutionNote = e.ResolutionNote,
        AssignedAt = e.AssignedAt,
        ResolvedAt = e.ResolvedAt,
        WaitingMinutes = (int)Math.Max(0, ((e.AssignedAt ?? (e.Status == EscalationStatus.Resolved ? e.ResolvedAt : null) ?? DateTime.UtcNow) - e.CreatedAt).TotalMinutes)
    };

    public static KnowledgeArticleDto ToDto(this KnowledgeArticle k) => new()
    {
        Id = k.Id, Title = k.Title, Category = k.Category, Content = k.Content, Keywords = k.Keywords,
        IsPublished = k.IsPublished, CreatedAt = k.CreatedAt, UpdatedAt = k.UpdatedAt
    };

    /// <summary>SQL-translated projection for conversation lists.</summary>
    public static readonly Expression<Func<Conversation, ConversationSummaryDto>> SummaryProjection = c => new ConversationSummaryDto
    {
        Id = c.Id,
        Title = c.Title,
        Status = c.Status,
        CurrentIntent = c.CurrentIntent,
        LastSentiment = c.LastSentiment,
        IsEscalated = c.IsEscalated,
        MessageCount = c.MessageCount,
        Rating = c.Rating,
        CustomerName = c.User.FullName,
        AssignedAgentName = c.AssignedAgent != null ? c.AssignedAgent.FullName : null,
        LastMessagePreview = c.Messages.Where(m => m.Role != MessageRole.System).OrderByDescending(m => m.Id).Select(m => m.Content).FirstOrDefault(),
        CreatedAt = c.CreatedAt,
        LastMessageAt = c.LastMessageAt
    };

    public static ConversationDetailDto ToDetailDto(this Conversation c) => new()
    {
        Id = c.Id,
        Title = c.Title,
        Status = c.Status,
        CurrentIntent = c.CurrentIntent,
        LastSentiment = c.LastSentiment,
        IsEscalated = c.IsEscalated,
        MessageCount = c.MessageCount,
        Rating = c.Rating,
        FeedbackComment = c.FeedbackComment,
        ResolvedBy = c.ResolvedBy,
        CustomerName = c.User?.FullName ?? string.Empty,
        AssignedAgentName = c.AssignedAgent?.FullName,
        CreatedAt = c.CreatedAt,
        LastMessageAt = c.LastMessageAt,
        Messages = c.Messages.OrderBy(m => m.Id).Select(m => m.ToDto()).ToList(),
        Escalations = c.Escalations.OrderBy(e => e.Id).Select(e => e.ToSummaryDto()).ToList(),
        LastMessagePreview = c.Messages.Where(m => m.Role != MessageRole.System).OrderByDescending(m => m.Id).Select(m => m.Content).FirstOrDefault()
    };

    public static string Truncate(this string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

// =====================================================================
//  AUTH
// =====================================================================
public class JwtSettings
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 240;
}

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<UserDto> GetProfileAsync(int userId, CancellationToken ct = default);
    Task<UserDto> CreateAgentAsync(CreateAgentRequest request, CancellationToken ct = default);
    Task<PagedResult<UserDto>> GetUsersAsync(UserRole? role, string? search, PaginationQuery page, CancellationToken ct = default);
}

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly JwtSettings _jwt;

    public AuthService(AppDbContext db, IPasswordHasher<User> hasher, IOptions<JwtSettings> jwt)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt.Value;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var user = await CreateUserAsync(request.FullName, request.Email, request.Password, UserRole.Customer, ct);
        return BuildToken(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            throw new UnauthorizedException("Invalid email or password.");
        if (!user.IsActive) throw new ForbiddenException("Your account has been deactivated.");
        return BuildToken(user);
    }

    public async Task<UserDto> GetProfileAsync(int userId, CancellationToken ct = default) =>
        (await _db.Users.FindAsync(new object[] { userId }, ct) ?? throw new NotFoundException("User not found.")).ToDto();

    public async Task<UserDto> CreateAgentAsync(CreateAgentRequest request, CancellationToken ct = default) =>
        (await CreateUserAsync(request.FullName, request.Email, request.Password, UserRole.Agent, ct)).ToDto();

    public async Task<PagedResult<UserDto>> GetUsersAsync(UserRole? role, string? search, PaginationQuery page, CancellationToken ct = default)
    {
        var q = _db.Users.AsNoTracking().AsQueryable();
        if (role.HasValue) { var r = role.Value; q = q.Where(u => u.Role == r); }
        if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(u => u.FullName.Contains(s) || u.Email.Contains(s)); }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(u => u.CreatedAt).Skip((page.PageNumber - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new PagedResult<UserDto> { Items = items.Select(u => u.ToDto()).ToList(), TotalCount = total, PageNumber = page.PageNumber, PageSize = page.PageSize };
    }

    private async Task<User> CreateUserAsync(string name, string email, string password, UserRole role, CancellationToken ct)
    {
        email = email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("An account with this email already exists.");

        var user = new User { FullName = name.Trim(), Email = email, Role = role };
        user.PasswordHash = _hasher.HashPassword(user, password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    private AuthResponse BuildToken(User user)
    {
        var expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_jwt.Issuer, _jwt.Audience, claims, expires: expires, signingCredentials: creds);
        return new AuthResponse { Token = new JwtSecurityTokenHandler().WriteToken(token), ExpiresAt = expires, User = user.ToDto() };
    }
}

// =====================================================================
//  KNOWLEDGE BASE (retrieval for grounded AI answers)
// =====================================================================
public interface IKnowledgeService
{
    Task<List<KnowledgeArticle>> FindRelevantAsync(string text, int take = 3, CancellationToken ct = default);
    Task<List<KnowledgeArticleDto>> GetAllAsync(string? search, CancellationToken ct = default);
    Task<KnowledgeArticleDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<KnowledgeArticleDto> CreateAsync(KnowledgeArticleRequest request, CancellationToken ct = default);
    Task<KnowledgeArticleDto> UpdateAsync(int id, KnowledgeArticleRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class KnowledgeService : IKnowledgeService
{
    private const string CacheKey = "kb:published";
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "you", "your", "are", "was", "with", "this", "that", "have", "has", "can", "how", "what", "when",
        "where", "why", "will", "would", "could", "please", "help", "need", "want", "get", "not", "but", "about", "from", "there",
        "they", "them", "its", "our", "out", "any", "all", "did", "does", "hello", "thanks", "thank", "just", "still", "yet"
    };

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public KnowledgeService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    /// <summary>Lightweight keyword relevance scoring (title and keywords weigh more than body).</summary>
    public async Task<List<KnowledgeArticle>> FindRelevantAsync(string text, int take = 3, CancellationToken ct = default)
    {
        var articles = await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _db.KnowledgeArticles.AsNoTracking().Where(k => k.IsPublished).ToListAsync(ct);
        }) ?? new List<KnowledgeArticle>();

        var terms = Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Select(Stem)
            .Distinct()
            .ToList();
        if (terms.Count == 0) return new List<KnowledgeArticle>();

        return articles
            .Select(a =>
            {
                var title = a.Title.ToLowerInvariant();
                var keywords = a.Keywords.ToLowerInvariant();
                var content = a.Content.ToLowerInvariant();
                var score = terms.Sum(t => (title.Contains(t) ? 3 : 0) + (keywords.Contains(t) ? 3 : 0) + (content.Contains(t) ? 1 : 0));
                return (Article: a, Score: score);
            })
            .Where(x => x.Score >= 3)
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x => x.Article)
            .ToList();
    }

    public async Task<List<KnowledgeArticleDto>> GetAllAsync(string? search, CancellationToken ct = default)
    {
        var q = _db.KnowledgeArticles.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(k => k.Title.Contains(s) || k.Keywords.Contains(s) || k.Category.Contains(s));
        }
        return (await q.OrderBy(k => k.Category).ThenBy(k => k.Title).ToListAsync(ct)).Select(k => k.ToDto()).ToList();
    }

    public async Task<KnowledgeArticleDto> GetByIdAsync(int id, CancellationToken ct = default) =>
        (await _db.KnowledgeArticles.FindAsync(new object[] { id }, ct) ?? throw new NotFoundException($"Article {id} was not found.")).ToDto();

    public async Task<KnowledgeArticleDto> CreateAsync(KnowledgeArticleRequest r, CancellationToken ct = default)
    {
        var article = new KnowledgeArticle();
        Map(article, r);
        _db.KnowledgeArticles.Add(article);
        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey);
        return article.ToDto();
    }

    public async Task<KnowledgeArticleDto> UpdateAsync(int id, KnowledgeArticleRequest r, CancellationToken ct = default)
    {
        var article = await _db.KnowledgeArticles.FindAsync(new object[] { id }, ct) ?? throw new NotFoundException($"Article {id} was not found.");
        Map(article, r);
        article.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey);
        return article.ToDto();
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var article = await _db.KnowledgeArticles.FindAsync(new object[] { id }, ct) ?? throw new NotFoundException($"Article {id} was not found.");
        _db.KnowledgeArticles.Remove(article);
        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey);
    }

    private static void Map(KnowledgeArticle a, KnowledgeArticleRequest r)
    {
        a.Title = r.Title.Trim();
        a.Category = r.Category.Trim();
        a.Content = r.Content.Trim();
        a.Keywords = r.Keywords.Trim();
        a.IsPublished = r.IsPublished;
    }

    private static string Stem(string word)
    {
        var stem = Regex.Replace(word, "(ing|ed|es|s)$", "");
        return stem.Length >= 3 ? stem : word;
    }
}
