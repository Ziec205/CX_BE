using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Platform;

/// <summary>Cờ tính năng bật/tắt lúc chạy (tài liệu 05 §5). Giá trị trong DB ghi đè cấu hình mặc định.</summary>
public class FeatureFlag
{
    [BsonId] public string Key { get; set; } = default!;
    public bool Enabled { get; set; }
    public string? Note { get; set; }
    public string UpdatedBy { get; set; } = default!;
    public DateTime UpdatedAt { get; set; }
}

public static class Flags
{
    public const string Escrow = "escrow";
    public const string Community = "community";
    public const string AiIdentify = "ai.identify";
    public const string ImageSearch = "search.image";

    /// <summary>Mặc định khi chưa có bản ghi trong DB. Escrow tắt cho tới khi ký đối tác thanh toán (BR-ESC-05).</summary>
    public static readonly Dictionary<string, bool> Defaults = new()
    {
        [Escrow] = false, [Community] = true, [AiIdentify] = true, [ImageSearch] = true,
    };
}

public class FeatureFlagService(IMongoDatabase db, IConfiguration config, TimeProvider clock)
{
    public IMongoCollection<FeatureFlag> Items { get; } = db.GetCollection<FeatureFlag>("featureFlags");

    bool Default(string key) => config.GetValue<bool?>($"Features:{key.Replace('.', '_')}") ?? Flags.Defaults.GetValueOrDefault(key);

    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default) =>
        (await Items.Find(f => f.Key == key).FirstOrDefaultAsync(ct))?.Enabled ?? Default(key);

    public async Task RequireAsync(string key, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(key, ct)) throw new DomainException("FEATURE_DISABLED", "Tính năng này chưa được bật", StatusCodes.Status403Forbidden);
    }

    public async Task<Dictionary<string, bool>> AllAsync(CancellationToken ct)
    {
        var stored = (await Items.Find(_ => true).ToListAsync(ct)).ToDictionary(f => f.Key, f => f.Enabled);
        return Flags.Defaults.Keys.ToDictionary(k => k, k => stored.TryGetValue(k, out var v) ? v : Default(k));
    }

    public Task SetAsync(string key, bool enabled, string? note, string actor, CancellationToken ct) =>
        Items.ReplaceOneAsync(f => f.Key == key, new FeatureFlag { Key = key, Enabled = enabled, Note = note, UpdatedBy = actor, UpdatedAt = clock.GetUtcNow().UtcDateTime },
            new ReplaceOptions { IsUpsert = true }, ct);
}

public record SetFlagRequest(bool Enabled, string? Note);

public static class FeatureFlagEndpoints
{
    public static void MapFeatureFlags(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/features", (FeatureFlagService svc, CancellationToken ct) => svc.AllAsync(ct)).WithTags("Features");

        var admin = app.MapGroup("/api/admin/features").WithTags("Admin Features").RequireAuthorization(Policies.ForPerm(Perm.AdminManage));
        admin.MapGet("/", (FeatureFlagService svc, CancellationToken ct) => svc.AllAsync(ct));
        admin.MapPut("/{key}", async (string key, SetFlagRequest req, ClaimsPrincipal p, FeatureFlagService svc, AuditService audit, CancellationToken ct) =>
        {
            if (!Flags.Defaults.ContainsKey(key)) throw DomainException.NotFound("cờ tính năng");
            var before = await svc.IsEnabledAsync(key, ct);
            await svc.SetAsync(key, req.Enabled, req.Note, p.UserId(), ct);
            await audit.LogAsync(p, "feature.set", "feature", key, before: new { enabled = before }, after: new { enabled = req.Enabled }, reason: req.Note, ct: ct);
            return Results.NoContent();
        });
    }
}
