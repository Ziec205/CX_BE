using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Messaging;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Notifications;

/// <summary>Thông báo trong app (tài liệu 05 §5). Đẩy realtime qua hub chat (sự kiện "notification"),
/// push qua <see cref="IPushSender"/> và SMS/ZNS qua <see cref="ISmsSender"/> cho bước quan trọng.</summary>
public class Notification
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public string Type { get; set; } = default!; // vd: care.watering, escrow, community.comment
    public string Title { get; set; } = default!;
    public string? Body { get; set; }
    public string? Link { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PushDevice
{
    [BsonId] public string Token { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public string Platform { get; set; } = default!; // ios | android | web
    public DateTime UpdatedAt { get; set; }
}

/// <summary>BR-NTF-01: bật/tắt từng nhóm. Nhóm giao dịch và bảo mật không tắt được.</summary>
public class NotificationSettings
{
    [BsonId] public string UserId { get; set; } = default!;
    public Dictionary<string, bool> Groups { get; set; } = [];
    public DateTime? LastMarketingPushAt { get; set; }
}

public static class NotificationGroups
{
    public const string Transaction = "transaction", Security = "security", Chat = "chat", Listing = "listing",
        Discovery = "discovery", Community = "community", Care = "care", Marketing = "marketing";
    public static readonly string[] Optional = [Chat, Listing, Discovery, Community, Care, Marketing];
    public static readonly string[] Mandatory = [Transaction, Security];

    public static string Of(string type) => type.Split('.')[0] switch
    {
        "escrow" or "wallet" or "garden" or "plan" => Transaction,
        "account" or "sanction" => Security,
        "chat" => Chat,
        "listing" => Listing,
        "search" or "favorite" or "follow" => Discovery,
        "community" => Community,
        "care" => Care,
        _ => Marketing,
    };
}

public interface IPushSender
{
    Task SendAsync(IReadOnlyList<PushDevice> devices, string title, string? body, string? link, CancellationToken ct);
}

public interface ISmsSender
{
    Task SendAsync(string phone, string text, CancellationToken ct);
}

/// <summary>Dùng khi chưa ký nhà cung cấp (FCM/APNs, SMS brandname/ZNS): chỉ ghi log.</summary>
public class LogPushSender(ILogger<LogPushSender> logger) : IPushSender, ISmsSender
{
    public Task SendAsync(IReadOnlyList<PushDevice> devices, string title, string? body, string? link, CancellationToken ct)
    {
        if (devices.Count > 0) logger.LogInformation("[push] {Count} thiết bị: {Title}", devices.Count, title);
        return Task.CompletedTask;
    }

    public Task SendAsync(string phone, string text, CancellationToken ct)
    {
        logger.LogInformation("[sms] {Phone}: {Text}", phone[..Math.Min(4, phone.Length)] + "***", text);
        return Task.CompletedTask;
    }
}

public class NotificationService(IMongoDatabase db, TimeProvider clock, IHubContext<ChatHub> hub, IPushSender push, ISmsSender sms)
{
    public IMongoCollection<Notification> Items { get; } = db.GetCollection<Notification>("notifications");
    public IMongoCollection<PushDevice> Devices { get; } = db.GetCollection<PushDevice>("pushDevices");
    public IMongoCollection<NotificationSettings> Settings { get; } = db.GetCollection<NotificationSettings>("notificationSettings");

    public async Task EnsureIndexesAsync()
    {
        await Items.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Notification>(Builders<Notification>.IndexKeys.Ascending(n => n.UserId).Descending(n => n.CreatedAt)),
            new CreateIndexModel<Notification>(Builders<Notification>.IndexKeys.Ascending(n => n.CreatedAt), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(90) }),
        ]);
        await Devices.Indexes.CreateOneAsync(new CreateIndexModel<PushDevice>(Builders<PushDevice>.IndexKeys.Ascending(d => d.UserId)));
    }

    public async Task<Dictionary<string, bool>> GetGroupsAsync(string userId, CancellationToken ct)
    {
        var s = await Settings.Find(x => x.UserId == userId).FirstOrDefaultAsync(ct);
        var groups = NotificationGroups.Optional.ToDictionary(g => g, g => s?.Groups.GetValueOrDefault(g, true) ?? true);
        foreach (var g in NotificationGroups.Mandatory) groups[g] = true;
        return groups;
    }

    /// <param name="phone">Truyền SĐT để gửi kèm SMS/ZNS (chỉ dùng cho bước quan trọng của đơn đảm bảo, cảnh báo tài khoản).</param>
    public async Task<Notification> SendAsync(string userId, string type, string title, string? body, string? link, CancellationToken ct, string? phone = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var n = new Notification { UserId = userId, Type = type, Title = title, Body = body, Link = link, CreatedAt = now };
        await Items.InsertOneAsync(n, cancellationToken: ct);
        await hub.Clients.Group(ChatHub.UserGroup(userId)).SendAsync("notification", n, ct);

        var group = NotificationGroups.Of(type);
        if ((await GetGroupsAsync(userId, ct))[group] && await MarketingAllowedAsync(userId, group, now, ct))
        {
            var devices = await Devices.Find(d => d.UserId == userId).ToListAsync(ct);
            await push.SendAsync(devices, title, body, link, ct);
        }
        if (phone is not null) await sms.SendAsync(phone, $"Cham Xanh: {title}", ct);
        return n;
    }

    /// <summary>BR-NTF-02/03: không push marketing 22h–7h, tối đa 1 lần/ngày.</summary>
    async Task<bool> MarketingAllowedAsync(string userId, string group, DateTime now, CancellationToken ct)
    {
        if (group != NotificationGroups.Marketing) return true;
        var hour = now.AddHours(7).Hour;
        if (hour is >= 22 or < 7) return false;
        var res = await Settings.UpdateOneAsync(s => s.UserId == userId && (s.LastMarketingPushAt == null || s.LastMarketingPushAt < now.AddDays(-1)),
            Builders<NotificationSettings>.Update.Set(s => s.LastMarketingPushAt, now), new UpdateOptions { IsUpsert = false }, ct);
        if (res.MatchedCount > 0) return true;
        if (await Settings.Find(s => s.UserId == userId).AnyAsync(ct)) return false;
        await Settings.InsertOneAsync(new NotificationSettings { UserId = userId, LastMarketingPushAt = now }, cancellationToken: ct);
        return true;
    }
}

public record RegisterDeviceRequest(string Token, string Platform);
public record UpdateNotificationSettings(Dictionary<string, bool> Groups);

public static class NotificationEndpoints
{
    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me/notifications").WithTags("Notifications").RequireAuthorization(Policies.Member);

        g.MapGet("/", async (DateTime? before, ClaimsPrincipal p, NotificationService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var f = Builders<Notification>.Filter.Eq(n => n.UserId, userId);
            if (before is { } b) f &= Builders<Notification>.Filter.Lt(n => n.CreatedAt, b);
            var items = await svc.Items.Find(f).SortByDescending(n => n.CreatedAt).Limit(30).ToListAsync(ct);
            var unread = await svc.Items.CountDocumentsAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken: ct);
            return new { items, unread };
        });
        g.MapGet("/unread-count", async (ClaimsPrincipal p, NotificationService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return new { count = await svc.Items.CountDocumentsAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken: ct) };
        });
        g.MapPost("/{id}/read", async (string id, ClaimsPrincipal p, NotificationService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.Items.UpdateOneAsync(n => n.Id == id && n.UserId == userId && n.ReadAt == null,
                Builders<Notification>.Update.Set(n => n.ReadAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
            return Results.NoContent();
        });
        g.MapPost("/read-all", async (ClaimsPrincipal p, NotificationService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.Items.UpdateManyAsync(n => n.UserId == userId && n.ReadAt == null,
                Builders<Notification>.Update.Set(n => n.ReadAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
            return Results.NoContent();
        });

        g.MapGet("/settings", (ClaimsPrincipal p, NotificationService svc, CancellationToken ct) => svc.GetGroupsAsync(p.UserId(), ct));
        g.MapPut("/settings", async (UpdateNotificationSettings req, ClaimsPrincipal p, NotificationService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var groups = req.Groups.Where(kv => NotificationGroups.Optional.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var u = Builders<NotificationSettings>.Update.SetOnInsert(s => s.UserId, userId);
            foreach (var (k, v) in groups) u = u.Set($"groups.{k}", v);
            await svc.Settings.UpdateOneAsync(s => s.UserId == userId, u, new UpdateOptions { IsUpsert = true }, ct);
            return await svc.GetGroupsAsync(userId, ct);
        });

        g.MapPost("/devices", async (RegisterDeviceRequest req, ClaimsPrincipal p, NotificationService svc, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Token) || req.Token.Length > 4096 || req.Platform is not ("ios" or "android" or "web"))
                throw new DomainException("INVALID_DEVICE", "Thiết bị không hợp lệ");
            await svc.Devices.ReplaceOneAsync(d => d.Token == req.Token,
                new PushDevice { Token = req.Token, UserId = p.UserId(), Platform = req.Platform, UpdatedAt = clock.GetUtcNow().UtcDateTime },
                new ReplaceOptions { IsUpsert = true }, ct);
            return Results.NoContent();
        });
        g.MapDelete("/devices", async (string token, ClaimsPrincipal p, NotificationService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.Devices.DeleteOneAsync(d => d.Token == token && d.UserId == userId, ct);
            return Results.NoContent();
        });
    }
}
