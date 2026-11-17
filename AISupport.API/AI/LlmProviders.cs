using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AISupport.API.AI;

/// <summary>Shared HTTP logic for LLM providers.</summary>
public abstract class LlmSupportAiBase : ISupportAi
{
    public const string HttpClientName = "llm";

    protected readonly IHttpClientFactory HttpFactory;
    protected readonly AiSettings Settings;
    protected readonly ILogger Logger;

    protected LlmSupportAiBase(IHttpClientFactory httpFactory, IOptions<AiSettings> settings, ILogger logger)
    {
        HttpFactory = httpFactory;
        Settings = settings.Value;
        Logger = logger;
    }

    public string ProviderName => Settings.DisplayName;

    public async Task<AiResult> RespondAsync(AiRequest request, CancellationToken ct = default)
    {
        var system = SupportPrompt.BuildSystemPrompt(Settings, request);
        var watch = Stopwatch.StartNew();

        try
        {
            using var message = CreateRequest(system, request);
            using var response = await HttpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            watch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("LLM error {Status}: {Body}", (int)response.StatusCode, body);
                return Fallback(watch.ElapsedMilliseconds, (int)response.StatusCode == 429
                    ? "AI rate limit reached"
                    : "AI service returned an error");
            }

            using var json = JsonDocument.Parse(body);
            var (text, tokens) = Read(json.RootElement);
            var result = SupportPrompt.Parse(text);
            result.TokensUsed = tokens;
            result.LatencyMs = watch.ElapsedMilliseconds;
            result.Provider = ProviderName;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            Logger.LogError(ex, "LLM request failed");
            return Fallback(watch.ElapsedMilliseconds, "AI service is unavailable");
        }
    }

    private AiResult Fallback(long latency, string reason) => new()
    {
        Intent = Models.CustomerIntent.Other,
        Confidence = 0,
        ShouldEscalate = true,
        EscalationReason = reason,
        Reply = "I'm having trouble answering right now, so I'm connecting you with a member of our support team who will reply shortly.",
        LatencyMs = latency,
        Provider = ProviderName
    };

    protected abstract HttpRequestMessage CreateRequest(string systemPrompt, AiRequest request);
    protected abstract (string? Text, int Tokens) Read(JsonElement root);
}

/// <summary>Google Gemini – free API key from https://aistudio.google.com/apikey</summary>
public class GeminiSupportAi : LlmSupportAiBase
{
    public GeminiSupportAi(IHttpClientFactory f, IOptions<AiSettings> s, ILogger<GeminiSupportAi> l) : base(f, s, l) { }

    protected override HttpRequestMessage CreateRequest(string systemPrompt, AiRequest request)
    {
        var contents = request.History
            .Select(t => new { role = t.Role == "assistant" ? "model" : "user", parts = new[] { new { text = t.Content } } })
            .Append(new { role = "user", parts = new[] { new { text = request.Message } } })
            .ToArray();

        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents,
            generationConfig = new { temperature = 0.3, responseMimeType = "application/json" }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Settings.EffectiveModel}:generateContent";
        var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        msg.Headers.Add("x-goog-api-key", Settings.ApiKey);
        return msg;
    }

    protected override (string? Text, int Tokens) Read(JsonElement root)
    {
        var text = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
        var tokens = root.TryGetProperty("usageMetadata", out var u) && u.TryGetProperty("totalTokenCount", out var t) ? t.GetInt32() : 0;
        return (text, tokens);
    }
}

/// <summary>OpenAI or any OpenAI-compatible API (Groq, OpenRouter, Azure-compatible gateways…).</summary>
public class OpenAiSupportAi : LlmSupportAiBase
{
    public OpenAiSupportAi(IHttpClientFactory f, IOptions<AiSettings> s, ILogger<OpenAiSupportAi> l) : base(f, s, l) { }

    protected override HttpRequestMessage CreateRequest(string systemPrompt, AiRequest request)
    {
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(request.History.Select(t => (object)new { role = t.Role == "assistant" ? "assistant" : "user", content = t.Content }));
        messages.Add(new { role = "user", content = request.Message });

        var payload = new
        {
            model = Settings.EffectiveModel,
            temperature = 0.3,
            response_format = new { type = "json_object" },
            messages
        };

        var msg = new HttpRequestMessage(HttpMethod.Post, $"{Settings.OpenAiBaseUrl.TrimEnd('/')}/chat/completions") { Content = JsonContent.Create(payload) };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.ApiKey);
        return msg;
    }

    protected override (string? Text, int Tokens) Read(JsonElement root)
    {
        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        var tokens = root.TryGetProperty("usage", out var u) && u.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0;
        return (text, tokens);
    }
}
