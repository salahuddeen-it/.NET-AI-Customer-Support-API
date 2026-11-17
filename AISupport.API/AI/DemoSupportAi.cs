using System.Diagnostics;
using System.Text.RegularExpressions;
using AISupport.API.Models;
using Microsoft.Extensions.Options;

namespace AISupport.API.AI;

/// <summary>
/// Offline rule-based assistant used automatically when no LLM API key is configured,
/// so the whole system (intents, escalation, analytics) can be demonstrated without internet.
/// </summary>
public class DemoSupportAi : ISupportAi
{
    private readonly AiSettings _settings;

    public DemoSupportAi(IOptions<AiSettings> settings) => _settings = settings.Value;

    public string ProviderName => "Demo (offline rules)";

    private static readonly (CustomerIntent Intent, string Pattern)[] IntentRules =
    {
        (CustomerIntent.HumanAgentRequest, @"\b(human|real person|agent|representative|talk to (someone|a person)|speak to)\b"),
        (CustomerIntent.Refund, @"\b(refund|money back|reimburse)"),
        (CustomerIntent.Cancellation, @"\b(cancel|cancellation)"),
        (CustomerIntent.OrderStatus, @"\b(where is my order|order status|track|tracking|my order)\b"),
        (CustomerIntent.ShippingDelivery, @"\b(shipping|delivery|deliver|courier|arrive)"),
        (CustomerIntent.Billing, @"\b(payment|pay|charged|invoice|bill|card|cod|cash on delivery)"),
        (CustomerIntent.AccountHelp, @"\b(password|login|log in|account|sign in|email address)"),
        (CustomerIntent.TechnicalIssue, @"\b(error|bug|crash|not working|app|website|broken)"),
        (CustomerIntent.ProductInquiry, @"\b(product|size|stock|available|warranty|price)"),
        (CustomerIntent.Complaint, @"\b(complain|complaint|terrible|worst|disappointed|bad service)"),
        (CustomerIntent.Feedback, @"\b(thank|thanks|great|awesome|love)"),
        (CustomerIntent.Greeting, @"^\s*(hi|hello|hey|salam|assalam|good (morning|evening|afternoon))\b")
    };

    private static readonly Regex Angry = new(@"\b(angry|furious|ridiculous|worst|scam|fraud|useless|terrible|disgusting|unacceptable)\b|!!", RegexOptions.IgnoreCase);
    private static readonly Regex Negative = new(@"\b(not happy|disappointed|bad|late|delay|problem|issue|wrong|broken|damaged|still)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Positive = new(@"\b(thank|thanks|great|awesome|perfect|love|helpful)\b", RegexOptions.IgnoreCase);

    public async Task<AiResult> RespondAsync(AiRequest request, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        await Task.Delay(150, ct); // feel like a real model

        var text = request.Message;
        var intent = IntentRules.FirstOrDefault(r => Regex.IsMatch(text, r.Pattern, RegexOptions.IgnoreCase)).Intent;
        if (intent == default) intent = CustomerIntent.Other;

        var sentiment = Angry.IsMatch(text) ? Sentiment.Angry
                      : Negative.IsMatch(text) ? Sentiment.Negative
                      : Positive.IsMatch(text) ? Sentiment.Positive
                      : Sentiment.Neutral;

        var firstName = request.CustomerName.Split(' ')[0];
        var article = request.Knowledge.FirstOrDefault();
        var result = new AiResult { Intent = intent, Sentiment = sentiment, Provider = ProviderName };

        switch (intent)
        {
            case CustomerIntent.Greeting:
                result.Reply = $"Hi {firstName}! 👋 I'm {_settings.AssistantName}, the virtual assistant of {_settings.CompanyName}. How can I help you today?";
                result.Confidence = 0.95;
                result.SuggestedTitle = "General help";
                break;

            case CustomerIntent.Feedback:
                result.Reply = $"You're very welcome, {firstName}! 😊 Is there anything else I can help you with?";
                result.Confidence = 0.9;
                result.SuggestedTitle = "Customer feedback";
                break;

            case CustomerIntent.HumanAgentRequest:
                result.Reply = "Of course! I'm connecting you with a human support agent who will reply here shortly.";
                result.Confidence = 0.9;
                result.ShouldEscalate = true;
                result.EscalationReason = "Customer asked for a human agent";
                result.SuggestedTitle = "Request for human agent";
                break;

            default:
                if (article is not null)
                {
                    result.Reply = $"{(sentiment is Sentiment.Negative or Sentiment.Angry ? $"I'm sorry for the trouble, {firstName}. " : "")}{article.Content}\n\nDid that answer your question?";
                    result.Confidence = 0.75;
                    result.SuggestedTitle = article.Title;
                }
                else
                {
                    result.Reply = "I'm not completely sure about that one, so I'm passing your question to a human support agent who will reply here shortly.";
                    result.Confidence = 0.3;
                    result.ShouldEscalate = true;
                    result.EscalationReason = "No matching knowledge-base article";
                    result.SuggestedTitle = "General question";
                }
                break;
        }

        if (sentiment == Sentiment.Angry && !result.ShouldEscalate)
        {
            result.ShouldEscalate = true;
            result.EscalationReason = "Customer is very upset";
            result.Reply += "\n\nI can see this has been frustrating, so I've also asked a human agent to step in right away.";
        }

        watch.Stop();
        result.LatencyMs = watch.ElapsedMilliseconds;
        return result;
    }
}
