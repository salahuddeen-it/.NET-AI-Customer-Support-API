using AISupport.API.AI;
using AISupport.API.Data;
using AISupport.API.DTOs;
using AISupport.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AISupport.API.Services;

public interface IAnalyticsService
{
    Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default);
    Task<AnalyticsDto> GetAnalyticsAsync(int days, CancellationToken ct = default);
}

public class AnalyticsService : IAnalyticsService
{
    private readonly AppDbContext _db;
    private readonly ISupportAi _ai;

    public AnalyticsService(AppDbContext db, ISupportAi ai)
    {
        _db = db;
        _ai = ai;
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        var conversations = _db.Conversations.AsNoTracking();
        var today = DateTime.UtcNow.Date;

        var total = await conversations.CountAsync(ct);
        var escalated = await conversations.CountAsync(c => c.IsEscalated, ct);
        var finished = await conversations.CountAsync(c => c.Status == ConversationStatus.Resolved || c.Status == ConversationStatus.Closed, ct);
        var finishedByAi = await conversations.CountAsync(c => (c.Status == ConversationStatus.Resolved || c.Status == ConversationStatus.Closed) && !c.IsEscalated, ct);

        var intents = await conversations.Where(c => c.CurrentIntent != null)
            .GroupBy(c => c.CurrentIntent)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var sentiments = await _db.Messages.AsNoTracking()
            .Where(m => m.Role == MessageRole.User && m.Sentiment != null)
            .GroupBy(m => m.Sentiment)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var latest = await _db.Escalations.AsNoTracking()
            .Include(e => e.Conversation).ThenInclude(c => c.User)
            .Include(e => e.AssignedAgent)
            .OrderByDescending(e => e.CreatedAt).Take(5)
            .ToListAsync(ct);

        var aiMessages = _db.Messages.AsNoTracking().Where(m => m.Role == MessageRole.Assistant && m.LatencyMs != null);

        return new DashboardDto
        {
            TotalConversations = total,
            ActiveConversations = await conversations.CountAsync(c => c.Status == ConversationStatus.Active || c.Status == ConversationStatus.Escalated, ct),
            ConversationsToday = await conversations.CountAsync(c => c.CreatedAt >= today, ct),
            TotalMessages = await _db.Messages.CountAsync(m => m.Role != MessageRole.System, ct),
            OpenEscalations = await _db.Escalations.CountAsync(e => e.Status != EscalationStatus.Resolved, ct),
            UrgentEscalations = await _db.Escalations.CountAsync(e => e.Status != EscalationStatus.Resolved && e.Priority == EscalationPriority.Urgent, ct),
            EscalationRate = Percent(escalated, total),
            AiResolutionRate = Percent(finishedByAi, finished),
            AverageRating = Round(await conversations.Where(c => c.Rating != null).AverageAsync(c => (double?)c.Rating, ct)),
            AverageAiLatencyMs = (long)(await aiMessages.AverageAsync(m => (double?)m.LatencyMs, ct) ?? 0),
            TotalTokensUsed = await _db.Messages.SumAsync(m => (long?)m.TokensUsed, ct) ?? 0,
            TotalCustomers = await _db.Users.CountAsync(u => u.Role == UserRole.Customer, ct),
            TotalAgents = await _db.Users.CountAsync(u => u.Role == UserRole.Agent, ct),
            AiProvider = _ai.ProviderName,
            TopIntents = ToNameCounts(intents.Select(x => (x.Key.ToString()!, x.Count))).Take(6).ToList(),
            Sentiments = ToNameCounts(sentiments.Select(x => (x.Key.ToString()!, x.Count))),
            LatestEscalations = latest.Select(e => e.ToDto()).ToList()
        };
    }

    public async Task<AnalyticsDto> GetAnalyticsAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var from = DateTime.UtcNow.Date.AddDays(-(days - 1));
        var to = DateTime.UtcNow;

        var conversations = await _db.Conversations.AsNoTracking()
            .Where(c => c.CreatedAt >= from)
            .Select(c => new { c.CreatedAt, c.ClosedAt, c.Status, c.IsEscalated, c.CurrentIntent, c.Rating, c.MessageCount })
            .ToListAsync(ct);

        var messagesPerDay = await _db.Messages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && m.Role != MessageRole.System)
            .GroupBy(m => m.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var escalations = await _db.Escalations.AsNoTracking()
            .Where(e => e.CreatedAt >= from)
            .Select(e => new
            {
                e.CreatedAt, e.AssignedAt, e.ResolvedAt, e.Trigger, e.Status, e.AssignedAgentId,
                AgentName = e.AssignedAgent != null ? e.AssignedAgent.FullName : null
            })
            .ToListAsync(ct);

        var sentiments = await _db.Messages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && m.Role == MessageRole.User && m.Sentiment != null)
            .GroupBy(m => m.Sentiment)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var finished = conversations.Where(c => c.Status is ConversationStatus.Resolved or ConversationStatus.Closed).ToList();
        var rated = conversations.Where(c => c.Rating.HasValue).ToList();
        var totalMessages = messagesPerDay.Sum(x => x.Count);

        return new AnalyticsDto
        {
            From = from,
            To = to,
            Conversations = conversations.Count,
            Messages = totalMessages,
            AverageMessagesPerConversation = conversations.Count == 0 ? 0 : Math.Round(conversations.Average(c => c.MessageCount), 1),
            EscalationRate = Percent(conversations.Count(c => c.IsEscalated), conversations.Count),
            AiResolutionRate = Percent(finished.Count(c => !c.IsEscalated), finished.Count),
            AverageResolutionMinutes = Round(finished.Where(c => c.ClosedAt.HasValue).Select(c => (c.ClosedAt!.Value - c.CreatedAt).TotalMinutes).DefaultIfEmptyAverage()),
            AverageEscalationWaitMinutes = Round(escalations.Where(e => e.AssignedAt.HasValue).Select(e => (e.AssignedAt!.Value - e.CreatedAt).TotalMinutes).DefaultIfEmptyAverage()),
            AverageRating = Round(rated.Count == 0 ? null : rated.Average(c => (double)c.Rating!.Value)),
            RatingDistribution = Enumerable.Range(1, 5).ToDictionary(r => r, r => rated.Count(c => c.Rating == r)),
            Daily = Enumerable.Range(0, days).Select(i => from.AddDays(i)).Select(day => new DailyStat
            {
                Date = day,
                Conversations = conversations.Count(c => c.CreatedAt.Date == day),
                Messages = messagesPerDay.FirstOrDefault(m => m.Day == day)?.Count ?? 0,
                Escalations = escalations.Count(e => e.CreatedAt.Date == day)
            }).ToList(),
            Intents = conversations.Where(c => c.CurrentIntent.HasValue)
                .GroupBy(c => c.CurrentIntent!.Value)
                .Select(g => new IntentStat
                {
                    Intent = g.Key,
                    Count = g.Count(),
                    Escalated = g.Count(c => c.IsEscalated),
                    EscalationRate = Percent(g.Count(c => c.IsEscalated), g.Count())
                })
                .OrderByDescending(x => x.Count).ToList(),
            Sentiments = ToNameCounts(sentiments.Select(x => (x.Key.ToString()!, x.Count))),
            EscalationTriggers = ToNameCounts(escalations.GroupBy(e => e.Trigger).Select(g => (g.Key, g.Count()))),
            Agents = escalations.Where(e => e.AssignedAgentId.HasValue)
                .GroupBy(e => new { Id = e.AssignedAgentId!.Value, Name = e.AgentName ?? "Agent" })
                .Select(g => new AgentStat
                {
                    AgentId = g.Key.Id,
                    AgentName = g.Key.Name,
                    Assigned = g.Count(),
                    Resolved = g.Count(e => e.Status == EscalationStatus.Resolved),
                    AverageResolveMinutes = Round(g.Where(e => e.ResolvedAt.HasValue && e.AssignedAt.HasValue)
                        .Select(e => (e.ResolvedAt!.Value - e.AssignedAt!.Value).TotalMinutes).DefaultIfEmptyAverage())
                })
                .OrderByDescending(a => a.Resolved).ToList()
        };
    }

    private static List<NameCount> ToNameCounts(IEnumerable<(string Name, int Count)> rows)
    {
        var list = rows.ToList();
        var total = list.Sum(x => x.Count);
        return list.OrderByDescending(x => x.Count)
            .Select(x => new NameCount { Name = x.Name, Count = x.Count, Percent = Percent(x.Count, total) })
            .ToList();
    }

    private static double Percent(int part, int total) => total == 0 ? 0 : Math.Round(part * 100.0 / total, 1);
    private static double? Round(double? value) => value.HasValue ? Math.Round(value.Value, 1) : null;
}

internal static class StatsExtensions
{
    public static double? DefaultIfEmptyAverage(this IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list.Average();
    }
}
