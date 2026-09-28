using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Platform;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Ai;

// AI nhận diện cây — tài liệu 05 §2.

public record RecognizedName(string ScientificName, double Probability);

/// <summary>Adapter nhà cung cấp nhận diện (Plant.id, Pl@ntNet...). Đổi nhà cung cấp không sửa nghiệp vụ.</summary>
public interface IPlantRecognizer
{
    Task<List<RecognizedName>> RecognizeAsync(byte[] image, CancellationToken ct);
}

public class AiOptions
{
    public string Provider { get; set; } = "none"; // none | plantid
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "https://plant.id/api/v3/identification";
    public double MinConfidence { get; set; } = 0.6;   // BR-AI-01
    public int MemberDailyLimit { get; set; } = 30;    // BR-AI-03
    public int GuestDailyLimit { get; set; } = 5;
}

/// <summary>Chưa cấu hình nhà cung cấp: không nhận ra gì, người dùng tự chọn loài (BR-AI-01).</summary>
public class NullPlantRecognizer : IPlantRecognizer
{
    public Task<List<RecognizedName>> RecognizeAsync(byte[] image, CancellationToken ct) => Task.FromResult(new List<RecognizedName>());
}

/// <summary>Kindwise Plant.id v3.</summary>
public class PlantIdRecognizer(HttpClient http, AiOptions options) : IPlantRecognizer
{
    public async Task<List<RecognizedName>> RecognizeAsync(byte[] image, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = JsonContent.Create(new { images = new[] { "data:image/jpeg;base64," + Convert.ToBase64String(image) }, similar_images = false }),
        };
        req.Headers.Add("Api-Key", options.ApiKey);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<RecognizedName>();
        if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("classification", out var c) && c.TryGetProperty("suggestions", out var s))
            foreach (var item in s.EnumerateArray())
                list.Add(new(item.GetProperty("name").GetString() ?? "", item.GetProperty("probability").GetDouble()));
        return list;
    }
}

public class RecognitionRequest
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string? UserId { get; set; }
    public string? Ip { get; set; }
    public string? MediaId { get; set; }
    public string Purpose { get; set; } = "listing"; // listing | search | moderation
    public List<RecognizedName> Raw { get; set; } = [];
    public List<string> SuggestedSpeciesIds { get; set; } = [];
    public List<string> Unmapped { get; set; } = [];
    /// <summary>BR-AI-02: lựa chọn cuối của người dùng, lưu làm nhãn huấn luyện.</summary>
    public string? LabelSpeciesId { get; set; }
    public bool? LabelMatchedSuggestion { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record SpeciesSuggestion(string SpeciesId, string CommonName, string? ScientificName, double Confidence);
public record IdentifyResult(string RequestId, bool Recognized, List<SpeciesSuggestion> Suggestions, string? Message);
public record LabelRequest(string SpeciesId);

public class RecognitionService(IMongoDatabase db, IPlantRecognizer recognizer, AiOptions options, CatalogService catalog, TimeProvider clock,
    ILogger<RecognitionService> logger)
{
    public IMongoCollection<RecognitionRequest> Requests { get; } = db.GetCollection<RecognitionRequest>("aiRecognitions");

    public Task EnsureIndexesAsync() => Requests.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<RecognitionRequest>(Builders<RecognitionRequest>.IndexKeys.Ascending(r => r.UserId).Descending(r => r.CreatedAt)),
        new CreateIndexModel<RecognitionRequest>(Builders<RecognitionRequest>.IndexKeys.Ascending(r => r.Ip).Descending(r => r.CreatedAt)),
        new CreateIndexModel<RecognitionRequest>(Builders<RecognitionRequest>.IndexKeys.Ascending(r => r.Unmapped)),
    ]);

    public async Task<IdentifyResult> IdentifyAsync(byte[] image, string? userId, string? ip, string? mediaId, string purpose, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-1);
        var used = userId is not null
            ? await Requests.CountDocumentsAsync(r => r.UserId == userId && r.CreatedAt > since, cancellationToken: ct)
            : await Requests.CountDocumentsAsync(r => r.UserId == null && r.Ip == ip && r.CreatedAt > since, cancellationToken: ct);
        if (used >= (userId is null ? options.GuestDailyLimit : options.MemberDailyLimit))
            throw DomainException.TooMany(userId is null ? "Khách được nhận diện 5 lần/ngày. Đăng nhập để dùng thêm" : "Bạn đã dùng hết 30 lượt nhận diện hôm nay");

        List<RecognizedName> raw;
        try { raw = await recognizer.RecognizeAsync(image, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Nhà cung cấp nhận diện lỗi");
            raw = [];
        }

        // BR-AI-04: ánh xạ tên khoa học sang loài trong Thư viện, không ánh xạ được thì ghi log cho biên tập.
        var suggestions = new List<SpeciesSuggestion>();
        var unmapped = new List<string>();
        foreach (var r in raw.OrderByDescending(x => x.Probability).Take(5))
        {
            var name = r.ScientificName.Trim();
            var exact = new BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(name)}$", "i");
            var sp = await catalog.Species.Find(Builders<Species>.Filter.Regex(s => s.ScientificName, exact)).FirstOrDefaultAsync(ct);
            // Nhà cung cấp chỉ nhận ra chi (1 từ): lấy loài đầu tiên cùng chi trong Thư viện.
            if (sp is null && !name.Contains(' '))
                sp = await catalog.Species.Find(Builders<Species>.Filter.Regex(s => s.ScientificName,
                    new BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(name)}\\s", "i"))).FirstOrDefaultAsync(ct);
            if (sp is null) { unmapped.Add(r.ScientificName); continue; }
            if (suggestions.All(x => x.SpeciesId != sp.Id)) suggestions.Add(new(sp.Id, sp.CommonName, sp.ScientificName, Math.Round(r.Probability, 3)));
        }
        suggestions = suggestions.Take(3).ToList();
        var confident = suggestions.Count > 0 && suggestions[0].Confidence >= options.MinConfidence;

        var req = new RecognitionRequest
        {
            UserId = userId, Ip = userId is null ? ip : null, MediaId = mediaId, Purpose = purpose, Raw = raw, Unmapped = unmapped,
            SuggestedSpeciesIds = suggestions.Select(s => s.SpeciesId).ToList(), CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await Requests.InsertOneAsync(req, cancellationToken: ct);
        // BR-AI-01: AI không tự chọn thay người dùng — chỉ trả gợi ý khi đủ tin cậy.
        return confident
            ? new(req.Id, true, suggestions, null)
            : new(req.Id, false, [], "Chưa nhận ra, bạn chọn giúp nhé");
    }

    public async Task LabelAsync(string requestId, string? userId, string speciesId, CancellationToken ct)
    {
        await catalog.GetSpeciesAsync(speciesId, ct);
        var r = await Requests.Find(x => x.Id == requestId && x.UserId == userId).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("lượt nhận diện");
        await Requests.UpdateOneAsync(x => x.Id == r.Id, Builders<RecognitionRequest>.Update
            .Set(x => x.LabelSpeciesId, speciesId).Set(x => x.LabelMatchedSuggestion, r.SuggestedSpeciesIds.Contains(speciesId)), cancellationToken: ct);
    }
}

public static class AiEndpoints
{
    const long MaxBytes = 10 * 1024 * 1024;

    static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is 0 or > MaxBytes) throw new DomainException("INVALID_FILE", "Ảnh rỗng hoặc lớn hơn 10MB");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    static async Task<byte[]> MediaBytesAsync(MediaService media, string mediaId, string userId, CancellationToken ct)
    {
        var item = (await media.GetOwnedAsync([mediaId], userId, ct))[0];
        var file = await media.OpenAsync(item, "full", ct) ?? throw DomainException.NotFound("ảnh");
        await using var s = file.Stream;
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public static void MapAi(this IEndpointRouteBuilder app)
    {
        // Nhận diện ảnh đã tải lên (luồng đăng tin): top 3 loài + độ tin cậy.
        app.MapPost("/api/ai/identify", async ([FromBody] IdentifyMediaRequest req, ClaimsPrincipal p, RecognitionService svc, MediaService media,
            FeatureFlagService flags, CancellationToken ct) =>
        {
            await flags.RequireAsync(Flags.AiIdentify, ct);
            var userId = p.UserId();
            var bytes = await MediaBytesAsync(media, req.MediaId, userId, ct);
            return await svc.IdentifyAsync(bytes, userId, null, req.MediaId, "listing", ct);
        }).WithTags("AI").RequireAuthorization(Policies.Member);

        app.MapPost("/api/ai/identify/{id}/label", async (string id, LabelRequest req, ClaimsPrincipal p, RecognitionService svc, CancellationToken ct) =>
        {
            await svc.LabelAsync(id, p.UserId(), req.SpeciesId, ct);
            return Results.NoContent();
        }).WithTags("AI").RequireAuthorization(Policies.Member);

        // Tìm bằng ảnh: nhận ra loài rồi trả tin đang bán loài đó. Khách dùng được (5 lượt/ngày theo IP).
        app.MapPost("/api/search/by-image", async ([FromForm] IFormFile file, ClaimsPrincipal p, HttpContext http, RecognitionService svc,
            ListingSearch search, FeatureFlagService flags, CancellationToken ct) =>
        {
            await flags.RequireAsync(Flags.ImageSearch, ct);
            var userId = p.FindFirst(Claims.Kind)?.Value == Claims.Member ? p.UserId() : null;
            var result = await svc.IdentifyAsync(await ReadAsync(file, ct), userId, http.Connection.RemoteIpAddress?.ToString(), null, "search", ct);
            var listings = result.Recognized
                ? (await search.SearchAsync(new ListingQuery(SpeciesId: result.Suggestions[0].SpeciesId, PageSize: 20), ct)).Items
                : [];
            return new { identify = result, listings };
        }).WithTags("AI").DisableAntiforgery();

        // Biên tập: tên khoa học chưa ánh xạ được (BR-AI-04).
        app.MapGet("/api/admin/ai/unmapped", async (RecognitionService svc, CancellationToken ct) =>
        {
            var agg = await svc.Requests.Aggregate().Match(r => r.Unmapped.Count > 0).Unwind<RecognitionRequest, BsonDocument>(r => r.Unmapped)
                .Group(new BsonDocument { { "_id", "$unmapped" }, { "count", new BsonDocument("$sum", 1) } })
                .Sort(new BsonDocument("count", -1)).Limit(100).ToListAsync(ct);
            return agg.Select(d => new { scientificName = d["_id"].AsString, count = d["count"].AsInt32 });
        }).WithTags("Admin AI").RequireAuthorization(Policies.ForPerm(Perm.CatalogManage));
    }
}

public record IdentifyMediaRequest(string MediaId);
