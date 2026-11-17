using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AISupport.API.Models;

namespace AISupport.API.AI;

public class AiSettings
{
    public string Provider { get; set; } = "Gemini";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string CompanyName { get; set; } = "ShopCore";
    public string AssistantName { get; set; } = "Aria";
    public int MaxHistoryMessages { get; set; } = 10;
    public double LowConfidenceThreshold { get; set; } = 0.45;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    public bool IsOpenAi => Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase);
    public string EffectiveModel => !string.IsNullOrWhiteSpace(Model) ? Model : IsOpenAi ? "gpt-4o-mini" : "gemini-2.5-flash";
    public string DisplayName => IsConfigured ? $"{(IsOpenAi ? "OpenAI" : "Gemini")} ({EffectiveModel})" : "Demo (offline rules)";
}

/// <summary>One previous turn of the conversation sent to the LLM as memory.</summary>
public record ChatTurn(string Role, string Content);

public class AiRequest
{
    public string CustomerName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<ChatTurn> History { get; set; } = new();
    public List<KnowledgeArticle> Knowledge { get; set; } = new();
}

public class AiResult
{
    public CustomerIntent Intent { get; set; } = CustomerIntent.Other;
    public Sentiment Sentiment { get; set; } = Sentiment.Neutral;
    public double Confidence { get; set; }
    public bool ShouldEscalate { get; set; }
    public string? EscalationReason { get; set; }
    public string Reply { get; set; } = string.Empty;
    public string? SuggestedTitle { get; set; }
    public int TokensUsed { get; set; }
    public long LatencyMs { get; set; }
    public string Provider { get; set; } = string.Empty;
}

/// <summary>AI brain of the support bot: detects intent &amp; sentiment and writes the reply in a single call.</summary>
public interface ISupportAi
{
    string ProviderName { get; }
    Task<AiResult> RespondAsync(AiRequest request, CancellationToken ct = default);
}

public static class SupportPrompt
{
    public static readonly string IntentList = string.Join(", ", Enum.GetNames<CustomerIntent>());

    public static string BuildSystemPrompt(AiSettings s, AiRequest r)
    {
        var kb = new StringBuilder();
        if (r.Knowledge.Count == 0) kb.AppendLine("(no relevant articles found)");
        for (var i = 0; i < r.Knowledge.Count; i++)
            kb.AppendLine($"[{i + 1}] {r.Knowledge[i].Title} ({r.Knowledge[i].Category})\n{r.Knowledge[i].Content}\n");

        return $$"""
            You are {{s.AssistantName}}, the friendly AI customer-support assistant of {{s.CompanyName}} (an online store).
            You are chatting with a customer named {{r.CustomerName}}.

            RULES
            - Answer ONLY with information from the KNOWLEDGE BASE below and the conversation so far.
            - If the answer is not in the knowledge base, say so honestly and offer to connect a human agent. Never invent order details, prices, dates or policies.
            - Keep replies short (2-5 sentences), warm and professional. Use simple English.
            - Never ask for passwords, OTP codes or full card numbers.
            - Set "shouldEscalate" to true when: the customer asks for a human; the customer is angry or mentions legal action / fraud;
              the request needs actions you cannot perform (issue a refund, change or cancel a specific order, access account data);
              or you cannot answer confidently. When shouldEscalate is true, tell the customer that a human agent will take over shortly.

            Respond ONLY with one valid JSON object (no markdown):
            {
              "intent": "one of: {{IntentList}}",
              "sentiment": "Positive | Neutral | Negative | Angry",
              "confidence": 0.0-1.0 (how sure you are that your reply fully solves the request),
              "shouldEscalate": true | false,
              "escalationReason": "short reason or null",
              "title": "3-6 word summary of the customer's issue",
              "reply": "your message to the customer"
            }

            KNOWLEDGE BASE
            {{kb}}
            """;
    }

    private class RawResult
    {
        public string? Intent { get; set; }
        public string? Sentiment { get; set; }
        public double Confidence { get; set; }
        public bool ShouldEscalate { get; set; }
        public string? EscalationReason { get; set; }
        public string? Title { get; set; }
        public string? Reply { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Parses the model's JSON. Returns a safe fallback (escalates) if the output is invalid.</summary>
    public static AiResult Parse(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    var raw = JsonSerializer.Deserialize<RawResult>(text[start..(end + 1)], JsonOptions);
                    if (raw is not null && !string.IsNullOrWhiteSpace(raw.Reply))
                    {
                        return new AiResult
                        {
                            Intent = Enum.TryParse<CustomerIntent>(raw.Intent?.Replace(" ", ""), true, out var i) ? i : CustomerIntent.Other,
                            Sentiment = Enum.TryParse<Sentiment>(raw.Sentiment, true, out var s) ? s : Sentiment.Neutral,
                            Confidence = Math.Clamp(raw.Confidence, 0, 1),
                            ShouldEscalate = raw.ShouldEscalate,
                            EscalationReason = raw.EscalationReason,
                            SuggestedTitle = raw.Title,
                            Reply = raw.Reply.Trim()
                        };
                    }
                }
                catch (JsonException) { /* fall through */ }
            }
        }

        return new AiResult
        {
            Intent = CustomerIntent.Other,
            Confidence = 0,
            ShouldEscalate = true,
            EscalationReason = "AI response could not be understood",
            Reply = "I'm sorry, I couldn't process that properly. Let me connect you with a member of our support team."
        };
    }
}

/// <summary>Business rules that decide when a conversation must go to a human – on top of the AI's own judgement.</summary>
public static class EscalationPolicy
{
    private static readonly Regex SensitiveTopics = new(
        @"\b(lawyer|legal action|sue|court|police|fraud|scam|stolen|chargeback|consumer court|harass)\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record Decision(bool Escalate, string Reason, string Trigger, EscalationPriority Priority);

    public static Decision Evaluate(AiResult ai, string customerMessage, double? previousAiConfidence, double threshold)
    {
        var priority = ai.Sentiment switch
        {
            Sentiment.Angry => EscalationPriority.Urgent,
            Sentiment.Negative => EscalationPriority.High,
            _ => EscalationPriority.Normal
        };

        if (SensitiveTopics.IsMatch(customerMessage))
            return new(true, "Sensitive topic detected (legal / fraud)", "Rule:SensitiveTopic", EscalationPriority.Urgent);

        if (ai.Intent == CustomerIntent.HumanAgentRequest)
            return new(true, "Customer asked for a human agent", "Customer", priority);

        if (ai.ShouldEscalate)
            return new(true, string.IsNullOrWhiteSpace(ai.EscalationReason) ? "AI could not fully resolve the request" : ai.EscalationReason!, "AI", priority);

        if (ai.Sentiment == Sentiment.Angry)
            return new(true, "Customer is very upset", "Rule:Sentiment", EscalationPriority.Urgent);

        if (ai.Confidence < threshold && previousAiConfidence.HasValue && previousAiConfidence.Value < threshold)
            return new(true, "Low AI confidence twice in a row", "Rule:LowConfidence", priority);

        return new(false, string.Empty, string.Empty, priority);
    }
}
