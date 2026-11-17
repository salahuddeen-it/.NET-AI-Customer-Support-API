namespace AISupport.API.Models;

// ======================= ENUMS =======================

public enum UserRole { Admin = 1, Agent = 2, Customer = 3 }

public enum ConversationStatus { Active = 1, Escalated = 2, Resolved = 3, Closed = 4 }

public enum MessageRole { User = 1, Assistant = 2, Agent = 3, System = 4 }

/// <summary>Customer intents detected by the AI.</summary>
public enum CustomerIntent
{
    Greeting = 1,
    OrderStatus = 2,
    ShippingDelivery = 3,
    Billing = 4,
    Refund = 5,
    Cancellation = 6,
    TechnicalIssue = 7,
    AccountHelp = 8,
    ProductInquiry = 9,
    Complaint = 10,
    HumanAgentRequest = 11,
    Feedback = 12,
    Other = 13
}

public enum Sentiment { Positive = 1, Neutral = 2, Negative = 3, Angry = 4 }

public enum EscalationStatus { Pending = 1, Assigned = 2, Resolved = 3 }

public enum EscalationPriority { Normal = 1, High = 2, Urgent = 3 }

public enum ResolvedBy { AI = 1, Human = 2 }

// ======================= ENTITIES =======================

public class User
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Customer;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Conversation
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Title { get; set; } = "New conversation";
    public string Channel { get; set; } = "Web";
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;

    public CustomerIntent? CurrentIntent { get; set; }
    public Sentiment? LastSentiment { get; set; }
    public bool IsEscalated { get; set; }
    public int? AssignedAgentId { get; set; }
    public User? AssignedAgent { get; set; }

    public int? Rating { get; set; }
    public string? FeedbackComment { get; set; }
    public ResolvedBy? ResolvedBy { get; set; }

    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<Escalation> Escalations { get; set; } = new List<Escalation>();

    public bool IsOpen => Status is ConversationStatus.Active or ConversationStatus.Escalated;
}

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public int? SenderUserId { get; set; }
    public string? SenderName { get; set; }

    // AI analysis metadata (on customer messages and AI replies)
    public CustomerIntent? Intent { get; set; }
    public Sentiment? Sentiment { get; set; }
    public double? Confidence { get; set; }
    public int? TokensUsed { get; set; }
    public long? LatencyMs { get; set; }
    public string? Provider { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Hand-off of a conversation from the AI to a human agent.</summary>
public class Escalation
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public string Reason { get; set; } = string.Empty;
    /// <summary>Who/what triggered it: AI, Customer, Rule:Sentiment, Rule:SensitiveTopic, Rule:LowConfidence.</summary>
    public string Trigger { get; set; } = string.Empty;
    public EscalationPriority Priority { get; set; } = EscalationPriority.Normal;
    public EscalationStatus Status { get; set; } = EscalationStatus.Pending;
    public int? AssignedAgentId { get; set; }
    public User? AssignedAgent { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AssignedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Company knowledge used to ground AI answers (RAG-style retrieval).</summary>
public class KnowledgeArticle
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Keywords { get; set; } = string.Empty;
    public bool IsPublished { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
