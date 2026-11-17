using AISupport.API.AI;
using AISupport.API.Common;
using AISupport.API.Data;
using AISupport.API.DTOs;
using AISupport.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AISupport.API.Services;

// =====================================================================
//  CONVERSATIONS + AI CHAT PIPELINE
// =====================================================================
public interface IConversationService
{
    Task<ConversationSummaryDto> CreateAsync(CurrentUser user, CreateConversationRequest request, CancellationToken ct = default);
    Task<ChatResponse> QuickChatAsync(CurrentUser user, QuickChatRequest request, CancellationToken ct = default);
    Task<ChatResponse> SendMessageAsync(CurrentUser user, int conversationId, string content, CancellationToken ct = default);
    Task<PagedResult<ConversationSummaryDto>> SearchAsync(ConversationQuery query, int? customerId, CancellationToken ct = default);
    Task<ConversationDetailDto> GetDetailAsync(CurrentUser user, int id, CancellationToken ct = default);
    Task<ConversationDetailDto> RequestHumanAsync(CurrentUser user, int id, string? reason, CancellationToken ct = default);
    Task<ConversationDetailDto> CloseAsync(CurrentUser user, int id, CancellationToken ct = default);
    Task<ConversationDetailDto> SubmitFeedbackAsync(CurrentUser user, int id, FeedbackRequest request, CancellationToken ct = default);
}

public class ConversationService : IConversationService
{
    private const string DefaultTitle = "New conversation";

    private readonly AppDbContext _db;
    private readonly ISupportAi _ai;
    private readonly IKnowledgeService _knowledge;
    private readonly AiSettings _settings;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(AppDbContext db, ISupportAi ai, IKnowledgeService knowledge, IOptions<AiSettings> settings, ILogger<ConversationService> logger)
    {
        _db = db;
        _ai = ai;
        _knowledge = knowledge;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<ConversationSummaryDto> CreateAsync(CurrentUser user, CreateConversationRequest request, CancellationToken ct = default)
    {
        var conversation = await CreateEntityAsync(user, request.Title, ct);
        return await _db.Conversations.AsNoTracking().Where(c => c.Id == conversation.Id).Select(Mapping.SummaryProjection).FirstAsync(ct);
    }

    public async Task<ChatResponse> QuickChatAsync(CurrentUser user, QuickChatRequest request, CancellationToken ct = default)
    {
        var conversationId = request.ConversationId ?? (await CreateEntityAsync(user, null, ct)).Id;
        return await SendMessageAsync(user, conversationId, request.Message, ct);
    }

    /// <summary>
    /// Main pipeline: save message → load memory → retrieve knowledge → LLM (intent + sentiment + reply)
    /// → escalation policy → save AI reply / escalate → return.
    /// </summary>
    public async Task<ChatResponse> SendMessageAsync(CurrentUser user, int conversationId, string content, CancellationToken ct = default)
    {
        content = content.Trim();
        if (content.Length == 0) throw new BadRequestException("Message cannot be empty.");

        var conv = await _db.Conversations
            .Include(c => c.User)
            .Include(c => c.AssignedAgent)
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == user.Id, ct)
            ?? throw new NotFoundException($"Conversation {conversationId} was not found.");

        if (!conv.IsOpen)
            throw new BadRequestException("This conversation is closed. Please start a new conversation.");

        var now = DateTime.UtcNow;
        var userMessage = new Message
        {
            ConversationId = conv.Id,
            Role = MessageRole.User,
            Content = content.Truncate(4000),
            SenderUserId = user.Id,
            SenderName = conv.User.FullName,
            CreatedAt = now
        };
        _db.Messages.Add(userMessage);
        conv.MessageCount++;
        conv.LastMessageAt = now;

        // ---- A human agent is handling the conversation: no AI reply ----
        if (conv.Status == ConversationStatus.Escalated)
        {
            await _db.SaveChangesAsync(ct);
            var open = await _db.Escalations.AsNoTracking().Include(e => e.AssignedAgent)
                .Where(e => e.ConversationId == conv.Id && e.Status != EscalationStatus.Resolved)
                .OrderByDescending(e => e.Id).FirstOrDefaultAsync(ct);

            return new ChatResponse
            {
                ConversationId = conv.Id,
                ConversationTitle = conv.Title,
                ConversationStatus = conv.Status,
                UserMessage = userMessage.ToDto(),
                Escalated = true,
                Escalation = open?.ToSummaryDto(),
                Notice = conv.AssignedAgent is not null
                    ? $"{conv.AssignedAgent.FullName} (human agent) will reply shortly."
                    : "Your message was added to the queue. A human agent will reply shortly."
            };
        }

        // ---- Conversation memory ----
        var history = await _db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conv.Id && m.Role != MessageRole.System)
            .OrderByDescending(m => m.Id)
            .Take(_settings.MaxHistoryMessages)
            .ToListAsync(ct);
        history.Reverse();

        var turns = history.Select(m => new ChatTurn(
            m.Role == MessageRole.User ? "user" : "assistant",
            m.Role == MessageRole.Agent ? $"[Human agent {m.SenderName}]: {m.Content}" : m.Content)).ToList();
        var previousConfidence = history.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Confidence;

        // ---- Knowledge retrieval + LLM ----
        var lastUserText = history.LastOrDefault(m => m.Role == MessageRole.User)?.Content ?? string.Empty;
        var knowledge = await _knowledge.FindRelevantAsync($"{content} {lastUserText}", 3, ct);

        var ai = await _ai.RespondAsync(new AiRequest
        {
            CustomerName = conv.User.FullName,
            Message = content,
            History = turns,
            Knowledge = knowledge
        }, ct);

        var decision = EscalationPolicy.Evaluate(ai, content, previousConfidence, _settings.LowConfidenceThreshold);

        // ---- Save analysis + AI reply ----
        userMessage.Intent = ai.Intent;
        userMessage.Sentiment = ai.Sentiment;

        var replyText = ai.Reply;
        if (decision.Escalate && !ai.ShouldEscalate && ai.Intent != CustomerIntent.HumanAgentRequest)
            replyText += "\n\nI've also asked a human support agent to join this conversation – they'll reply here shortly.";

        var aiMessage = new Message
        {
            ConversationId = conv.Id,
            Role = MessageRole.Assistant,
            Content = replyText.Truncate(4000),
            SenderName = _settings.AssistantName,
            Intent = ai.Intent,
            Sentiment = ai.Sentiment,
            Confidence = Math.Round(ai.Confidence, 2),
            TokensUsed = ai.TokensUsed,
            LatencyMs = ai.LatencyMs,
            Provider = ai.Provider.Truncate(60),
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(aiMessage);
        conv.MessageCount++;
        conv.LastMessageAt = aiMessage.CreatedAt;
        conv.LastSentiment = ai.Sentiment;
        if (ai.Intent is not (CustomerIntent.Greeting or CustomerIntent.Feedback) || conv.CurrentIntent is null)
            conv.CurrentIntent = ai.Intent;
        if (conv.Title == DefaultTitle && !string.IsNullOrWhiteSpace(ai.SuggestedTitle) && ai.Intent != CustomerIntent.Greeting)
            conv.Title = ai.SuggestedTitle.Trim().Truncate(150);

        Escalation? escalation = null;
        if (decision.Escalate)
            escalation = await EscalateAsync(conv, decision.Reason, decision.Trigger, decision.Priority, ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Conversation {Id}: intent={Intent} sentiment={Sentiment} confidence={Confidence:0.00} escalated={Escalated}",
            conv.Id, ai.Intent, ai.Sentiment, ai.Confidence, escalation is not null);

        return new ChatResponse
        {
            ConversationId = conv.Id,
            ConversationTitle = conv.Title,
            ConversationStatus = conv.Status,
            UserMessage = userMessage.ToDto(),
            Reply = aiMessage.ToDto(),
            Analysis = new AnalysisDto
            {
                Intent = ai.Intent,
                Sentiment = ai.Sentiment,
                Confidence = Math.Round(ai.Confidence, 2),
                KnowledgeArticlesUsed = knowledge.Select(k => k.Title).ToList(),
                Provider = ai.Provider,
                LatencyMs = ai.LatencyMs,
                TokensUsed = ai.TokensUsed
            },
            Escalated = escalation is not null,
            Escalation = escalation?.ToSummaryDto(),
            Notice = escalation is null ? null
                : escalation.AssignedAgent is not null ? $"Escalated to human agent {escalation.AssignedAgent.FullName}." : "Escalated to the human support queue."
        };
    }

    public async Task<PagedResult<ConversationSummaryDto>> SearchAsync(ConversationQuery q, int? customerId, CancellationToken ct = default)
    {
        var query = _db.Conversations.AsNoTracking().AsQueryable();

        if (customerId.HasValue) { var uid = customerId.Value; query = query.Where(c => c.UserId == uid); }
        if (q.Status.HasValue) { var s = q.Status.Value; query = query.Where(c => c.Status == s); }
        if (q.Intent.HasValue) { var i = q.Intent.Value; query = query.Where(c => c.CurrentIntent == i); }
        if (q.Sentiment.HasValue) { var s = q.Sentiment.Value; query = query.Where(c => c.LastSentiment == s); }
        if (q.Escalated.HasValue) { var e = q.Escalated.Value; query = query.Where(c => c.IsEscalated == e); }
        if (q.From.HasValue) { var from = q.From.Value.Date; query = query.Where(c => c.CreatedAt >= from); }
        if (q.To.HasValue) { var to = q.To.Value.Date.AddDays(1); query = query.Where(c => c.CreatedAt < to); }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(c => c.Title.Contains(term) || c.User.FullName.Contains(term) || c.User.Email.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(c => c.LastMessageAt)
            .Skip((q.PageNumber - 1) * q.PageSize).Take(q.PageSize)
            .Select(Mapping.SummaryProjection)
            .ToListAsync(ct);

        return new PagedResult<ConversationSummaryDto> { Items = items, TotalCount = total, PageNumber = q.PageNumber, PageSize = q.PageSize };
    }

    public async Task<ConversationDetailDto> GetDetailAsync(CurrentUser user, int id, CancellationToken ct = default) =>
        (await LoadFullAsync(id, user, tracking: false, ct)).ToDetailDto();

    public async Task<ConversationDetailDto> RequestHumanAsync(CurrentUser user, int id, string? reason, CancellationToken ct = default)
    {
        var conv = await LoadFullAsync(id, user, tracking: true, ct);
        if (conv.UserId != user.Id) throw new ForbiddenException("Only the customer can request a human agent.");
        if (!conv.IsOpen) throw new BadRequestException("This conversation is closed.");
        if (conv.Status == ConversationStatus.Escalated) throw new BadRequestException("A human agent is already handling this conversation.");

        var priority = conv.LastSentiment switch
        {
            Sentiment.Angry => EscalationPriority.Urgent,
            Sentiment.Negative => EscalationPriority.High,
            _ => EscalationPriority.Normal
        };
        await EscalateAsync(conv, string.IsNullOrWhiteSpace(reason) ? "Customer asked for a human agent" : reason.Trim(), "Customer", priority, ct);
        await _db.SaveChangesAsync(ct);
        return conv.ToDetailDto();
    }

    public async Task<ConversationDetailDto> CloseAsync(CurrentUser user, int id, CancellationToken ct = default)
    {
        var conv = await LoadFullAsync(id, user, tracking: true, ct);
        if (conv.UserId != user.Id && !user.IsStaff) throw new ForbiddenException("You cannot close this conversation.");
        if (!conv.IsOpen) throw new BadRequestException("This conversation is already closed.");

        var now = DateTime.UtcNow;
        conv.Status = ConversationStatus.Closed;
        conv.ClosedAt = now;
        conv.ResolvedBy ??= conv.IsEscalated ? ResolvedBy.Human : ResolvedBy.AI;

        foreach (var e in conv.Escalations.Where(e => e.Status != EscalationStatus.Resolved))
        {
            e.Status = EscalationStatus.Resolved;
            e.ResolvedAt = now;
            e.ResolutionNote ??= $"Conversation closed by {user.Name}.";
        }

        conv.Messages.Add(new Message { Role = MessageRole.System, Content = $"Conversation closed by {user.Name}.", CreatedAt = now });
        await _db.SaveChangesAsync(ct);
        return conv.ToDetailDto();
    }

    public async Task<ConversationDetailDto> SubmitFeedbackAsync(CurrentUser user, int id, FeedbackRequest request, CancellationToken ct = default)
    {
        var conv = await LoadFullAsync(id, user, tracking: true, ct);
        if (conv.UserId != user.Id) throw new ForbiddenException("Only the customer can rate this conversation.");
        if (conv.MessageCount == 0) throw new BadRequestException("You can rate a conversation after chatting.");

        conv.Rating = request.Rating;
        conv.FeedbackComment = request.Comment?.Trim();
        await _db.SaveChangesAsync(ct);
        return conv.ToDetailDto();
    }

    // ---------- helpers ----------

    private async Task<Conversation> CreateEntityAsync(CurrentUser user, string? title, CancellationToken ct)
    {
        if (user.IsStaff) throw new ForbiddenException("Only customers can start support conversations.");

        var conversation = new Conversation
        {
            UserId = user.Id,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim().Truncate(150)
        };
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);
        return conversation;
    }

    private async Task<Conversation> LoadFullAsync(int id, CurrentUser user, bool tracking, CancellationToken ct)
    {
        var query = _db.Conversations
            .Include(c => c.User)
            .Include(c => c.AssignedAgent)
            .Include(c => c.Messages)
            .Include(c => c.Escalations).ThenInclude(e => e.AssignedAgent)
            .AsSplitQuery();

        var conv = await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conv is null || (!user.IsStaff && conv.UserId != user.Id))
            throw new NotFoundException($"Conversation {id} was not found.");
        return conv;
    }

    /// <summary>Creates the escalation, picks the least-busy agent and adds a system message (not saved yet).</summary>
    private async Task<Escalation> EscalateAsync(Conversation conv, string reason, string trigger, EscalationPriority priority, CancellationToken ct)
    {
        var agent = await AgentAssignment.PickLeastBusyAgentAsync(_db, ct);
        var now = DateTime.UtcNow;

        var escalation = new Escalation
        {
            Conversation = conv,
            Reason = reason.Truncate(300),
            Trigger = trigger,
            Priority = priority,
            Status = agent is null ? EscalationStatus.Pending : EscalationStatus.Assigned,
            AssignedAgentId = agent?.Id,
            AssignedAgent = agent,
            AssignedAt = agent is null ? null : now,
            CreatedAt = now
        };
        _db.Escalations.Add(escalation);

        conv.Status = ConversationStatus.Escalated;
        conv.IsEscalated = true;
        conv.AssignedAgentId = agent?.Id;
        conv.AssignedAgent = agent;

        _db.Messages.Add(new Message
        {
            ConversationId = conv.Id,
            Role = MessageRole.System,
            Content = agent is null
                ? $"Conversation escalated to the human support queue ({priority} priority). Reason: {reason}"
                : $"Conversation escalated to {agent.FullName} ({priority} priority). Reason: {reason}",
            CreatedAt = now.AddMilliseconds(5)
        });

        _logger.LogWarning("Conversation {Id} escalated ({Trigger}): {Reason}", conv.Id, trigger, reason);
        return escalation;
    }
}

public static class AgentAssignment
{
    /// <summary>Active agent with the fewest open escalations.</summary>
    public static async Task<User?> PickLeastBusyAgentAsync(AppDbContext db, CancellationToken ct)
    {
        var agents = await db.Users.Where(u => u.Role == UserRole.Agent && u.IsActive).ToListAsync(ct);
        if (agents.Count == 0) return null;

        var loads = await db.Escalations
            .Where(e => e.Status == EscalationStatus.Assigned && e.AssignedAgentId != null)
            .GroupBy(e => e.AssignedAgentId!.Value)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, ct);

        return agents.OrderBy(a => loads.TryGetValue(a.Id, out var c) ? c : 0).ThenBy(a => a.Id).First();
    }
}

// =====================================================================
//  HUMAN AGENTS
// =====================================================================
public interface IAgentService
{
    Task<PagedResult<EscalationDto>> GetEscalationsAsync(CurrentUser agent, EscalationQuery query, CancellationToken ct = default);
    Task<EscalationDto> ClaimAsync(int escalationId, CurrentUser agent, CancellationToken ct = default);
    Task<MessageDto> ReplyAsync(int conversationId, CurrentUser agent, string content, CancellationToken ct = default);
    Task<EscalationDto> ResolveAsync(int escalationId, CurrentUser agent, ResolveEscalationRequest request, CancellationToken ct = default);
}

public class AgentService : IAgentService
{
    private readonly AppDbContext _db;

    public AgentService(AppDbContext db) => _db = db;

    public async Task<PagedResult<EscalationDto>> GetEscalationsAsync(CurrentUser agent, EscalationQuery q, CancellationToken ct = default)
    {
        var query = _db.Escalations.AsNoTracking()
            .Include(e => e.Conversation).ThenInclude(c => c.User)
            .Include(e => e.AssignedAgent)
            .AsQueryable();

        switch ((q.Scope ?? "all").ToLowerInvariant())
        {
            case "mine": query = query.Where(e => e.AssignedAgentId == agent.Id); break;
            case "unassigned": query = query.Where(e => e.AssignedAgentId == null); break;
        }
        if (q.Status.HasValue) { var s = q.Status.Value; query = query.Where(e => e.Status == s); }
        if (q.Priority.HasValue) { var p = q.Priority.Value; query = query.Where(e => e.Priority == p); }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(e => e.Status)
            .ThenByDescending(e => e.Priority)
            .ThenBy(e => e.CreatedAt)
            .Skip((q.PageNumber - 1) * q.PageSize).Take(q.PageSize)
            .ToListAsync(ct);

        return new PagedResult<EscalationDto> { Items = items.Select(e => e.ToDto()).ToList(), TotalCount = total, PageNumber = q.PageNumber, PageSize = q.PageSize };
    }

    public async Task<EscalationDto> ClaimAsync(int escalationId, CurrentUser agent, CancellationToken ct = default)
    {
        var e = await LoadAsync(escalationId, ct);
        if (e.Status == EscalationStatus.Resolved) throw new BadRequestException("This escalation is already resolved.");
        if (e.AssignedAgentId == agent.Id) throw new BadRequestException("You are already handling this escalation.");

        var me = await _db.Users.FindAsync(new object[] { agent.Id }, ct) ?? throw new NotFoundException("Agent not found.");
        var now = DateTime.UtcNow;
        e.AssignedAgentId = me.Id;
        e.AssignedAgent = me;
        e.AssignedAt = now;
        e.Status = EscalationStatus.Assigned;
        e.Conversation.AssignedAgentId = me.Id;
        e.Conversation.AssignedAgent = me;

        _db.Messages.Add(new Message { ConversationId = e.ConversationId, Role = MessageRole.System, Content = $"{me.FullName} (human agent) joined the conversation.", CreatedAt = now });
        await _db.SaveChangesAsync(ct);
        return e.ToDto();
    }

    public async Task<MessageDto> ReplyAsync(int conversationId, CurrentUser agent, string content, CancellationToken ct = default)
    {
        var conv = await _db.Conversations.Include(c => c.Escalations).FirstOrDefaultAsync(c => c.Id == conversationId, ct)
                   ?? throw new NotFoundException($"Conversation {conversationId} was not found.");
        if (!conv.IsOpen) throw new BadRequestException("This conversation is closed.");
        if (conv.Status != ConversationStatus.Escalated)
            throw new BadRequestException("The AI is handling this conversation. Agents can reply after it has been escalated.");

        var now = DateTime.UtcNow;
        var open = conv.Escalations.Where(e => e.Status != EscalationStatus.Resolved).OrderByDescending(e => e.Id).FirstOrDefault();
        if (open is not null && open.AssignedAgentId is null)
        {
            open.AssignedAgentId = agent.Id;
            open.AssignedAt = now;
            open.Status = EscalationStatus.Assigned;
        }
        conv.AssignedAgentId ??= agent.Id;

        var message = new Message
        {
            ConversationId = conv.Id,
            Role = MessageRole.Agent,
            Content = content.Trim().Truncate(4000),
            SenderUserId = agent.Id,
            SenderName = agent.Name,
            CreatedAt = now
        };
        _db.Messages.Add(message);
        conv.MessageCount++;
        conv.LastMessageAt = now;

        await _db.SaveChangesAsync(ct);
        return message.ToDto();
    }

    public async Task<EscalationDto> ResolveAsync(int escalationId, CurrentUser agent, ResolveEscalationRequest request, CancellationToken ct = default)
    {
        var e = await LoadAsync(escalationId, ct);
        if (e.Status == EscalationStatus.Resolved) throw new BadRequestException("This escalation is already resolved.");

        var now = DateTime.UtcNow;
        e.Status = EscalationStatus.Resolved;
        e.ResolvedAt = now;
        e.ResolutionNote = request.Note?.Trim();
        if (e.AssignedAgentId is null)
        {
            e.AssignedAgentId = agent.Id;
            e.AssignedAt = now;
        }

        var conv = e.Conversation;
        if (request.ReturnToAi)
        {
            conv.Status = ConversationStatus.Active;
            _db.Messages.Add(new Message { ConversationId = conv.Id, Role = MessageRole.System, Content = $"{agent.Name} resolved the escalation and handed the conversation back to the AI assistant.", CreatedAt = now });
        }
        else
        {
            conv.Status = ConversationStatus.Resolved;
            conv.ResolvedBy = ResolvedBy.Human;
            conv.ClosedAt = now;
            _db.Messages.Add(new Message { ConversationId = conv.Id, Role = MessageRole.System, Content = $"{agent.Name} marked this conversation as resolved.", CreatedAt = now });
        }

        await _db.SaveChangesAsync(ct);
        return (await LoadAsync(escalationId, ct)).ToDto();
    }

    private async Task<Escalation> LoadAsync(int id, CancellationToken ct) =>
        await _db.Escalations
            .Include(e => e.Conversation).ThenInclude(c => c.User)
            .Include(e => e.AssignedAgent)
            .FirstOrDefaultAsync(e => e.Id == id, ct)
        ?? throw new NotFoundException($"Escalation {id} was not found.");
}
