using System.ComponentModel.DataAnnotations;
using AISupport.API.Common;
using AISupport.API.Models;

namespace AISupport.API.DTOs;

// ======================= AUTH =======================

public class RegisterRequest
{
    /// <example>Hina Malik</example>
    [Required, StringLength(100, MinimumLength = 2)]
    public string FullName { get; set; } = string.Empty;

    /// <example>hina@example.com</example>
    [Required, EmailAddress, StringLength(150)]
    public string Email { get; set; } = string.Empty;

    /// <example>Customer@123</example>
    [Required, StringLength(100, MinimumLength = 6)]
    [RegularExpression(@"^(?=.*[A-Za-z])(?=.*\d).+$", ErrorMessage = "Password must contain at least one letter and one number.")]
    public string Password { get; set; } = string.Empty;
}

public class LoginRequest
{
    /// <example>customer@support.ai</example>
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    /// <example>Customer@123</example>
    [Required] public string Password { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public DateTime ExpiresAt { get; set; }
    public UserDto User { get; set; } = null!;
}

public class UserDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateAgentRequest
{
    /// <example>Usman Tariq</example>
    [Required, StringLength(100, MinimumLength = 2)] public string FullName { get; set; } = string.Empty;
    /// <example>usman@support.ai</example>
    [Required, EmailAddress, StringLength(150)] public string Email { get; set; } = string.Empty;
    /// <example>Agent@123</example>
    [Required, StringLength(100, MinimumLength = 6)] public string Password { get; set; } = string.Empty;
}

// ======================= CHAT =======================

public class QuickChatRequest
{
    /// <summary>Leave empty to start a new conversation.</summary>
    public int? ConversationId { get; set; }

    /// <example>Hi, where is my order? It has been 6 days.</example>
    [Required, StringLength(2000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
}

public class SendMessageRequest
{
    /// <example>How long does a refund take?</example>
    [Required, StringLength(2000, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;
}

public class CreateConversationRequest
{
    /// <example>Question about my order</example>
    [StringLength(150)]
    public string? Title { get; set; }
}

public class EscalateRequest
{
    /// <example>I would like to talk to a real person.</example>
    [StringLength(300)]
    public string? Reason { get; set; }
}

public class FeedbackRequest
{
    /// <example>5</example>
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5.")]
    public int Rating { get; set; }

    /// <example>Very quick and helpful!</example>
    [StringLength(500)]
    public string? Comment { get; set; }
}

public class MessageDto
{
    public int Id { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? SenderName { get; set; }
    public CustomerIntent? Intent { get; set; }
    public Sentiment? Sentiment { get; set; }
    public double? Confidence { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AnalysisDto
{
    public CustomerIntent Intent { get; set; }
    public Sentiment Sentiment { get; set; }
    public double Confidence { get; set; }
    public List<string> KnowledgeArticlesUsed { get; set; } = new();
    public string Provider { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
    public int TokensUsed { get; set; }
}

public class EscalationSummaryDto
{
    public int Id { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public EscalationPriority Priority { get; set; }
    public EscalationStatus Status { get; set; }
    public string? AssignedAgentName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ChatResponse
{
    public int ConversationId { get; set; }
    public string ConversationTitle { get; set; } = string.Empty;
    public ConversationStatus ConversationStatus { get; set; }
    public MessageDto UserMessage { get; set; } = null!;
    /// <summary>AI reply (null when a human agent is handling the conversation).</summary>
    public MessageDto? Reply { get; set; }
    public AnalysisDto? Analysis { get; set; }
    public bool Escalated { get; set; }
    public EscalationSummaryDto? Escalation { get; set; }
    public string? Notice { get; set; }
}

public class ConversationSummaryDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public ConversationStatus Status { get; set; }
    public CustomerIntent? CurrentIntent { get; set; }
    public Sentiment? LastSentiment { get; set; }
    public bool IsEscalated { get; set; }
    public int MessageCount { get; set; }
    public int? Rating { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string? AssignedAgentName { get; set; }
    public string? LastMessagePreview { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
}

public class ConversationDetailDto : ConversationSummaryDto
{
    public string? FeedbackComment { get; set; }
    public ResolvedBy? ResolvedBy { get; set; }
    public List<MessageDto> Messages { get; set; } = new();
    public List<EscalationSummaryDto> Escalations { get; set; } = new();
}

public class ConversationQuery : PaginationQuery
{
    public ConversationStatus? Status { get; set; }
    public CustomerIntent? Intent { get; set; }
    public Sentiment? Sentiment { get; set; }
    public bool? Escalated { get; set; }
    /// <summary>Title or customer name/email.</summary>
    [StringLength(100)] public string? Search { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

// ======================= AGENTS =======================

public class AgentReplyRequest
{
    /// <example>Hi! I'm Sana from the support team. I've checked your order and issued the refund.</example>
    [Required, StringLength(2000, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;
}

public class ResolveEscalationRequest
{
    /// <example>Refund issued manually.</example>
    [StringLength(500)]
    public string? Note { get; set; }

    /// <summary>true = hand the conversation back to the AI, false = mark it resolved.</summary>
    public bool ReturnToAi { get; set; }
}

public class EscalationQuery : PaginationQuery
{
    public EscalationStatus? Status { get; set; }
    public EscalationPriority? Priority { get; set; }
    /// <summary>all (default) | mine | unassigned</summary>
    [RegularExpression("^(?i)(all|mine|unassigned)$")]
    public string? Scope { get; set; }
}

public class EscalationDto : EscalationSummaryDto
{
    public int ConversationId { get; set; }
    public string ConversationTitle { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public CustomerIntent? Intent { get; set; }
    public Sentiment? Sentiment { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int WaitingMinutes { get; set; }
}

// ======================= KNOWLEDGE BASE =======================

public class KnowledgeArticleDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Keywords { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class KnowledgeArticleRequest
{
    /// <example>Gift card balance</example>
    [Required, StringLength(150, MinimumLength = 3)] public string Title { get; set; } = string.Empty;
    /// <example>Billing</example>
    [Required, StringLength(50)] public string Category { get; set; } = string.Empty;
    /// <example>You can check your gift card balance under Account > Wallet. Gift cards never expire.</example>
    [Required, StringLength(4000, MinimumLength = 10)] public string Content { get; set; } = string.Empty;
    /// <example>gift card, balance, wallet, voucher</example>
    [StringLength(300)] public string Keywords { get; set; } = string.Empty;
    public bool IsPublished { get; set; } = true;
}

// ======================= ADMIN / ANALYTICS =======================

public class DashboardDto
{
    public int TotalConversations { get; set; }
    public int ActiveConversations { get; set; }
    public int ConversationsToday { get; set; }
    public int TotalMessages { get; set; }
    public int OpenEscalations { get; set; }
    public int UrgentEscalations { get; set; }
    public double EscalationRate { get; set; }
    public double AiResolutionRate { get; set; }
    public double? AverageRating { get; set; }
    public long AverageAiLatencyMs { get; set; }
    public long TotalTokensUsed { get; set; }
    public int TotalCustomers { get; set; }
    public int TotalAgents { get; set; }
    public string AiProvider { get; set; } = string.Empty;
    public List<NameCount> TopIntents { get; set; } = new();
    public List<NameCount> Sentiments { get; set; } = new();
    public List<EscalationDto> LatestEscalations { get; set; } = new();
}

public class NameCount
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Percent { get; set; }
}

public class AnalyticsDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int Conversations { get; set; }
    public int Messages { get; set; }
    public double AverageMessagesPerConversation { get; set; }
    public double EscalationRate { get; set; }
    public double AiResolutionRate { get; set; }
    public double? AverageResolutionMinutes { get; set; }
    public double? AverageEscalationWaitMinutes { get; set; }
    public double? AverageRating { get; set; }
    public Dictionary<int, int> RatingDistribution { get; set; } = new();
    public List<DailyStat> Daily { get; set; } = new();
    public List<IntentStat> Intents { get; set; } = new();
    public List<NameCount> Sentiments { get; set; } = new();
    public List<NameCount> EscalationTriggers { get; set; } = new();
    public List<AgentStat> Agents { get; set; } = new();
}

public class DailyStat
{
    public DateTime Date { get; set; }
    public int Conversations { get; set; }
    public int Messages { get; set; }
    public int Escalations { get; set; }
}

public class IntentStat
{
    public CustomerIntent Intent { get; set; }
    public int Count { get; set; }
    public int Escalated { get; set; }
    public double EscalationRate { get; set; }
}

public class AgentStat
{
    public int AgentId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public int Assigned { get; set; }
    public int Resolved { get; set; }
    public double? AverageResolveMinutes { get; set; }
}
