using AISupport.API.Common;
using AISupport.API.DTOs;
using AISupport.API.Models;
using AISupport.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AISupport.API.Controllers;

[ApiController]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
public abstract class ApiControllerBase : ControllerBase
{
    protected CurrentUser Me => User.ToCurrentUser();
    protected IActionResult OkResponse<T>(T data, string message = "Request completed successfully.") => Ok(ApiResponse<T>.Ok(data, message));
}

// =====================================================================
/// <summary>Registration, login and profile.</summary>
[Route("api/auth")]
public class AuthController : ApiControllerBase
{
    private readonly IAuthService _auth;
    public AuthController(IAuthService auth) => _auth = auth;

    /// <summary>Register a customer account.</summary>
    [HttpPost("register"), AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, ApiResponse<AuthResponse>.Ok(await _auth.RegisterAsync(request, ct), "Registration successful."));

    /// <summary>Login and receive a JWT token.</summary>
    /// <remarks>Copy the <c>token</c>, click <b>Authorize</b> 🔒 and paste it.</remarks>
    [HttpPost("login"), AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct) =>
        OkResponse(await _auth.LoginAsync(request, ct), "Login successful.");

    /// <summary>Current user profile.</summary>
    [HttpGet("me"), Authorize]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMe(CancellationToken ct) => OkResponse(await _auth.GetProfileAsync(Me.Id, ct));
}

// =====================================================================
/// <summary>One-call chat endpoint – the easiest way to talk to the AI assistant.</summary>
[Route("api/chat")]
[Authorize(Roles = Roles.Customer)]
public class ChatController : ApiControllerBase
{
    private readonly IConversationService _conversations;
    public ChatController(IConversationService conversations) => _conversations = conversations;

    /// <summary>Send a message to the AI assistant.</summary>
    /// <remarks>
    /// Leave <c>conversationId</c> empty to start a new conversation, then send the returned id with follow-up messages.
    /// The response contains the AI reply plus the detected <b>intent</b>, <b>sentiment</b>, <b>confidence</b> and whether
    /// the conversation was <b>escalated</b> to a human agent.
    ///
    /// Try: <i>"Where is my order?"</i>, <i>"How long does a refund take?"</i>, <i>"This is the worst service ever!!"</i>, <i>"I want to talk to a human"</i>.
    /// </remarks>
    [HttpPost]
    [EnableRateLimiting("chat")]
    [ProducesResponseType(typeof(ApiResponse<ChatResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Chat([FromBody] QuickChatRequest request, CancellationToken ct)
    {
        var result = await _conversations.QuickChatAsync(Me, request, ct);
        return OkResponse(result, result.Escalated ? "Conversation escalated to a human agent." : "Reply generated.");
    }
}

// =====================================================================
/// <summary>Conversation / session management for customers.</summary>
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ApiControllerBase
{
    private readonly IConversationService _conversations;
    public ConversationsController(IConversationService conversations) => _conversations = conversations;

    /// <summary>[Customer] Start a new conversation (session).</summary>
    [HttpPost, Authorize(Roles = Roles.Customer)]
    [ProducesResponseType(typeof(ApiResponse<ConversationSummaryDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateConversationRequest request, CancellationToken ct)
    {
        var conversation = await _conversations.CreateAsync(Me, request, ct);
        return CreatedAtAction(nameof(GetById), new { id = conversation.Id }, ApiResponse<ConversationSummaryDto>.Ok(conversation, "Conversation started."));
    }

    /// <summary>[Customer] My conversation history.</summary>
    [HttpGet, Authorize(Roles = Roles.Customer)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ConversationSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMine([FromQuery] ConversationQuery query, CancellationToken ct) =>
        OkResponse(await _conversations.SearchAsync(query, Me.Id, ct));

    /// <summary>Full conversation with all messages and escalations (owner or staff).</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<ConversationDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken ct) => OkResponse(await _conversations.GetDetailAsync(Me, id, ct));

    /// <summary>[Customer] Send a message in a conversation (AI replies unless a human agent has taken over).</summary>
    [HttpPost("{id:int}/messages"), Authorize(Roles = Roles.Customer)]
    [EnableRateLimiting("chat")]
    [ProducesResponseType(typeof(ApiResponse<ChatResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendMessage(int id, [FromBody] SendMessageRequest request, CancellationToken ct) =>
        OkResponse(await _conversations.SendMessageAsync(Me, id, request.Content, ct));

    /// <summary>[Customer] Ask for a human agent.</summary>
    [HttpPost("{id:int}/escalate"), Authorize(Roles = Roles.Customer)]
    [ProducesResponseType(typeof(ApiResponse<ConversationDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Escalate(int id, [FromBody] EscalateRequest? request, CancellationToken ct) =>
        OkResponse(await _conversations.RequestHumanAsync(Me, id, request?.Reason, ct), "A human agent will join shortly.");

    /// <summary>Close a conversation.</summary>
    [HttpPost("{id:int}/close")]
    [ProducesResponseType(typeof(ApiResponse<ConversationDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Close(int id, CancellationToken ct) => OkResponse(await _conversations.CloseAsync(Me, id, ct), "Conversation closed.");

    /// <summary>[Customer] Rate the conversation (1-5) – used for CSAT analytics.</summary>
    [HttpPost("{id:int}/feedback"), Authorize(Roles = Roles.Customer)]
    [ProducesResponseType(typeof(ApiResponse<ConversationDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Feedback(int id, [FromBody] FeedbackRequest request, CancellationToken ct) =>
        OkResponse(await _conversations.SubmitFeedbackAsync(Me, id, request, ct), "Thank you for your feedback!");
}

// =====================================================================
/// <summary>Human agents: escalation queue, replies and resolution.</summary>
[Route("api/agent")]
[Authorize(Roles = Roles.Staff)]
public class AgentController : ApiControllerBase
{
    private readonly IAgentService _agents;
    private readonly IConversationService _conversations;

    public AgentController(IAgentService agents, IConversationService conversations)
    {
        _agents = agents;
        _conversations = conversations;
    }

    /// <summary>Escalation queue (ordered by status, priority, waiting time).</summary>
    /// <remarks>Scope: <c>all</c> (default), <c>mine</c>, <c>unassigned</c>.</remarks>
    [HttpGet("escalations")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<EscalationDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Escalations([FromQuery] EscalationQuery query, CancellationToken ct) =>
        OkResponse(await _agents.GetEscalationsAsync(Me, query, ct));

    /// <summary>Take ownership of an escalation.</summary>
    [HttpPost("escalations/{id:int}/claim")]
    [ProducesResponseType(typeof(ApiResponse<EscalationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Claim(int id, CancellationToken ct) => OkResponse(await _agents.ClaimAsync(id, Me, ct), "Escalation claimed.");

    /// <summary>Resolve an escalation – close the conversation or hand it back to the AI.</summary>
    [HttpPost("escalations/{id:int}/resolve")]
    [ProducesResponseType(typeof(ApiResponse<EscalationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resolve(int id, [FromBody] ResolveEscalationRequest request, CancellationToken ct) =>
        OkResponse(await _agents.ResolveAsync(id, Me, request, ct), "Escalation resolved.");

    /// <summary>View any conversation with full history and AI analysis.</summary>
    [HttpGet("conversations/{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<ConversationDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Conversation(int id, CancellationToken ct) => OkResponse(await _conversations.GetDetailAsync(Me, id, ct));

    /// <summary>Reply to the customer as a human agent.</summary>
    [HttpPost("conversations/{id:int}/reply")]
    [ProducesResponseType(typeof(ApiResponse<MessageDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reply(int id, [FromBody] AgentReplyRequest request, CancellationToken ct) =>
        OkResponse(await _agents.ReplyAsync(id, Me, request.Content, ct), "Reply sent.");
}

// =====================================================================
/// <summary>[Admin] Dashboard, analytics, conversations and users.</summary>
[Route("api/admin")]
[Authorize(Roles = Roles.Admin)]
public class AdminController : ApiControllerBase
{
    private readonly IAnalyticsService _analytics;
    private readonly IConversationService _conversations;
    private readonly IAuthService _auth;

    public AdminController(IAnalyticsService analytics, IConversationService conversations, IAuthService auth)
    {
        _analytics = analytics;
        _conversations = conversations;
        _auth = auth;
    }

    /// <summary>Admin dashboard: KPIs, escalation rate, AI resolution rate, CSAT, latency, tokens, top intents.</summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(ApiResponse<DashboardDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard(CancellationToken ct) => OkResponse(await _analytics.GetDashboardAsync(ct));

    /// <summary>Conversation analytics for the last N days (daily trend, intents, sentiment, triggers, agent performance).</summary>
    [HttpGet("analytics")]
    [ProducesResponseType(typeof(ApiResponse<AnalyticsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Analytics([FromQuery] int days = 14, CancellationToken ct = default) =>
        OkResponse(await _analytics.GetAnalyticsAsync(days, ct));

    /// <summary>Search all conversations (status, intent, sentiment, escalated, date range).</summary>
    [HttpGet("conversations")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ConversationSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Conversations([FromQuery] ConversationQuery query, CancellationToken ct) =>
        OkResponse(await _conversations.SearchAsync(query, null, ct));

    /// <summary>List users.</summary>
    [HttpGet("users")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<UserDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Users([FromQuery] UserRole? role, [FromQuery] string? search, [FromQuery] PaginationQuery page, CancellationToken ct) =>
        OkResponse(await _auth.GetUsersAsync(role, search, page, ct));

    /// <summary>Create a human support agent.</summary>
    [HttpPost("agents")]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAgent([FromBody] CreateAgentRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, ApiResponse<UserDto>.Ok(await _auth.CreateAgentAsync(request, ct), "Agent created."));
}

// =====================================================================
/// <summary>Knowledge base used by the AI to answer questions.</summary>
[Route("api/knowledge")]
[Authorize(Roles = Roles.Staff)]
public class KnowledgeController : ApiControllerBase
{
    private readonly IKnowledgeService _knowledge;
    public KnowledgeController(IKnowledgeService knowledge) => _knowledge = knowledge;

    /// <summary>List articles.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<KnowledgeArticleDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll([FromQuery] string? search, CancellationToken ct) => OkResponse(await _knowledge.GetAllAsync(search, ct));

    /// <summary>Get an article.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<KnowledgeArticleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetById(int id, CancellationToken ct) => OkResponse(await _knowledge.GetByIdAsync(id, ct));

    /// <summary>Test retrieval: which articles would the AI use for this question?</summary>
    [HttpGet("search-relevant")]
    [ProducesResponseType(typeof(ApiResponse<List<string>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Relevant([FromQuery] string question, CancellationToken ct) =>
        OkResponse((await _knowledge.FindRelevantAsync(question ?? string.Empty, 3, ct)).Select(a => a.Title).ToList());

    /// <summary>[Admin] Create an article.</summary>
    [HttpPost, Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(ApiResponse<KnowledgeArticleDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] KnowledgeArticleRequest request, CancellationToken ct)
    {
        var article = await _knowledge.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = article.Id }, ApiResponse<KnowledgeArticleDto>.Ok(article, "Article created."));
    }

    /// <summary>[Admin] Update an article.</summary>
    [HttpPut("{id:int}"), Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(ApiResponse<KnowledgeArticleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] KnowledgeArticleRequest request, CancellationToken ct) =>
        OkResponse(await _knowledge.UpdateAsync(id, request, ct), "Article updated.");

    /// <summary>[Admin] Delete an article.</summary>
    [HttpDelete("{id:int}"), Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _knowledge.DeleteAsync(id, ct);
        return NoContent();
    }
}
