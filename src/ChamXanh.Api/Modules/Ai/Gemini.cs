using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChamXanh.Api.Modules.Ai;

/// <summary>Khóa Gemini chỉ đặt qua biến môi trường (Render): Gemini__ApiKey.</summary>
public class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-flash";
    /// <summary>Model dự phòng khi model chính quá tải hoặc hết hạn mức miễn phí theo phút.</summary>
    public string FallbackModel { get; set; } = "gemini-3.5-flash-lite";
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
    public int TimeoutSeconds { get; set; } = 45;
    public int MaxOutputTokens { get; set; } = 8192; // gồm cả token "suy nghĩ" của model Flash đời mới
}

public record AiImage(byte[] Data, string MimeType);
public record AiTurn(bool FromUser, string Text, IReadOnlyList<AiImage>? Images = null);

/// <summary>Một lần gọi mô hình. Có <see cref="Schema"/> thì mô hình trả JSON đúng khuôn.
/// <see cref="Simulated"/> là câu trả lời mẫu dùng khi chưa có khóa API (chế độ thử).</summary>
public record AiPrompt(string System, IReadOnlyList<AiTurn> Turns, JsonObject? Schema = null, double Temperature = 0.4, string? Simulated = null);

public class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Adapter mô hình ngôn ngữ. Đổi nhà cung cấp không sửa nghiệp vụ.</summary>
public interface IAiModel
{
    bool IsSimulated { get; }
    Task<string> GenerateAsync(AiPrompt prompt, CancellationToken ct);
}

public class GeminiModel(HttpClient http, GeminiOptions options, ILogger<GeminiModel> logger) : IAiModel
{
    public bool IsSimulated => false;

    public async Task<string> GenerateAsync(AiPrompt prompt, CancellationToken ct)
    {
        try { return await CallAsync(options.Model, prompt, ct); }
        catch (AiUnavailableException ex) when (options.FallbackModel.Length > 0 && options.FallbackModel != options.Model)
        {
            logger.LogWarning(ex, "Gemini {Model} lỗi, thử model dự phòng", options.Model);
            return await CallAsync(options.FallbackModel, prompt, ct);
        }
    }

    async Task<string> CallAsync(string model, AiPrompt prompt, CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var turn in prompt.Turns)
        {
            var parts = new JsonArray();
            foreach (var img in turn.Images ?? [])
                parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = img.MimeType, ["data"] = Convert.ToBase64String(img.Data) } });
            if (turn.Text.Length > 0) parts.Add(new JsonObject { ["text"] = turn.Text });
            contents.Add(new JsonObject { ["role"] = turn.FromUser ? "user" : "model", ["parts"] = parts });
        }
        var config = new JsonObject { ["temperature"] = prompt.Temperature, ["maxOutputTokens"] = options.MaxOutputTokens };
        if (prompt.Schema is not null)
        {
            config["responseMimeType"] = "application/json";
            config["responseSchema"] = prompt.Schema.DeepClone();
        }
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.System }) },
            ["contents"] = contents,
            ["generationConfig"] = config,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint.TrimEnd('/')}/{model}:generateContent") { Content = JsonContent.Create(body) };
        req.Headers.Add("x-goog-api-key", options.ApiKey);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, cts.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AiUnavailableException("Không kết nối được Gemini", ex);
        }
        using (res)
        {
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError
                or HttpStatusCode.NotFound or HttpStatusCode.GatewayTimeout)
                throw new AiUnavailableException($"Gemini {model} trả {(int)res.StatusCode}: {Trim(raw)}");
            if (!res.IsSuccessStatusCode)
            {
                logger.LogError("Gemini {Model} trả {Status}: {Body}", model, (int)res.StatusCode, Trim(raw));
                throw new AiUnavailableException($"Gemini trả lỗi {(int)res.StatusCode}");
            }
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0
                || !cands[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                throw new AiUnavailableException("Gemini không trả nội dung (có thể bị bộ lọc an toàn chặn)");
            var text = string.Concat(parts.EnumerateArray()
                .Where(p => !(p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
                .Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
            if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("Gemini trả nội dung rỗng");
            return text;
        }
    }

    static string Trim(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>Chưa có khóa Gemini: trả câu mẫu để thử giao diện và luồng tính lượt.</summary>
public class SimulatedAiModel : IAiModel
{
    public bool IsSimulated => true;
    public Task<string> GenerateAsync(AiPrompt prompt, CancellationToken ct) =>
        Task.FromResult(prompt.Simulated ?? "{}");
}
