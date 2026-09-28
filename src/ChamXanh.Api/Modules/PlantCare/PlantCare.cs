using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Notifications;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.PlantCare;

/// <summary>Cây trong Hồ sơ vườn của người dùng (không phải gian hàng Nhà vườn).</summary>
public class MyPlant
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? SpeciesId { get; set; }
    public string? SpeciesName { get; set; }
    public List<string> MediaIds { get; set; } = [];
    public string? Location { get; set; } // vd: Ban công, phòng khách
    public DateTime? AcquiredAt { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum ReminderKind { Watering, Fertilizing, Pruning, Repotting, Other }
public enum ReminderRepeat { None, Daily, Weekly, Monthly, Yearly }

public class CareReminder
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public string PlantId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public ReminderKind Kind { get; set; }
    public string? Note { get; set; }
    /// <summary>Mốc đầu tiên (UTC). Các lần lặp tính từ mốc này theo giờ Việt Nam để không trôi ngày.</summary>
    public DateTime AnchorAt { get; set; }
    [BsonRepresentation(BsonType.String)] public ReminderRepeat Repeat { get; set; }
    public int Interval { get; set; } = 1;
    public DateTime? NextAt { get; set; }
    public DateTime? LastFiredAt { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public record PlantRequest(string Name, string? SpeciesId, List<string>? MediaIds, string? Location, DateTime? AcquiredAt, string? Note);
/// <summary>At: thời điểm giờ-ngày-tháng-năm kèm múi giờ (ISO 8601, vd 2026-10-01T07:30:00+07:00).</summary>
public record ReminderRequest(ReminderKind Kind, DateTimeOffset At, ReminderRepeat Repeat, int? Interval, string? Note, bool? Enabled);

/// <summary>Mốc + chu kỳ lặp của một lời nhắc (tách khỏi document để tính toán thuần).</summary>
public readonly record struct Recurrence(DateTime AnchorAt, ReminderRepeat Repeat, int Interval);

public static class ReminderSchedule
{
    public static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);

    /// <summary>Lần thứ n (n ≥ 0) tính từ mốc, cộng theo lịch giờ VN (Tháng: 31/1 → 28/2 → 31/3, không trôi).</summary>
    public static DateTime Occurrence(CareReminder r, int n) => Occurrence(new Recurrence(r.AnchorAt, r.Repeat, r.Interval), n);
    public static DateTime? NextAfter(CareReminder r, DateTime after) => NextAfter(new Recurrence(r.AnchorAt, r.Repeat, r.Interval), after);

    public static DateTime Occurrence(Recurrence r, int n)
    {
        var local = new DateTimeOffset(DateTime.SpecifyKind(r.AnchorAt, DateTimeKind.Utc)).ToOffset(VnOffset);
        var step = n * Math.Max(1, r.Interval);
        var next = r.Repeat switch
        {
            ReminderRepeat.Daily => local.AddDays(step),
            ReminderRepeat.Weekly => local.AddDays(7 * step),
            ReminderRepeat.Monthly => local.AddMonths(step),
            ReminderRepeat.Yearly => local.AddYears(step),
            _ => local,
        };
        return next.UtcDateTime;
    }

    /// <summary>Lần kế tiếp sau thời điểm <paramref name="after"/>; null nếu không lặp và đã qua.</summary>
    public static DateTime? NextAfter(Recurrence r, DateTime after)
    {
        if (r.AnchorAt > after) return r.AnchorAt;
        if (r.Repeat == ReminderRepeat.None) return null;
        // Ước lượng n rồi chỉnh, tránh lặp hàng nghìn lần với nhắc hằng ngày lâu năm.
        var days = r.Repeat switch { ReminderRepeat.Daily => 1.0, ReminderRepeat.Weekly => 7, ReminderRepeat.Monthly => 30.4, _ => 365.25 } * Math.Max(1, r.Interval);
        var n = Math.Max(0, (int)((after - r.AnchorAt).TotalDays / days) - 1);
        while (Occurrence(r, n) <= after) n++;
        return Occurrence(r, n);
    }

    public static string Label(ReminderKind k) => k switch
    {
        ReminderKind.Watering => "Tưới cây", ReminderKind.Fertilizing => "Bón phân", ReminderKind.Pruning => "Cắt tỉa",
        ReminderKind.Repotting => "Thay chậu", _ => "Chăm cây",
    };
}

public class PlantCareService(IMongoDatabase db, TimeProvider clock, CatalogService catalog, MediaService media, NotificationService notifications)
{
    const int MaxPlants = 300, MaxRemindersPerPlant = 10;
    public IMongoCollection<MyPlant> Plants { get; } = db.GetCollection<MyPlant>("myPlants");
    public IMongoCollection<CareReminder> Reminders { get; } = db.GetCollection<CareReminder>("careReminders");

    public async Task EnsureIndexesAsync()
    {
        await Plants.Indexes.CreateOneAsync(new CreateIndexModel<MyPlant>(Builders<MyPlant>.IndexKeys.Ascending(p => p.UserId).Descending(p => p.CreatedAt)));
        await Reminders.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<CareReminder>(Builders<CareReminder>.IndexKeys.Ascending(r => r.UserId).Ascending(r => r.PlantId)),
            new CreateIndexModel<CareReminder>(Builders<CareReminder>.IndexKeys.Ascending(r => r.Enabled).Ascending(r => r.NextAt)),
        ]);
    }

    public async Task<MyPlant> GetOwnAsync(string userId, string id, CancellationToken ct) =>
        await Plants.Find(p => p.Id == id && p.UserId == userId).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("cây");

    async Task Apply(MyPlant p, PlantRequest req, string userId, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 80) throw new DomainException("INVALID_NAME", "Tên cây dài 1–80 ký tự");
        if (req.Note?.Length > 2000) throw new DomainException("INVALID_NOTE", "Ghi chú tối đa 2000 ký tự");
        var mediaIds = req.MediaIds ?? [];
        if (mediaIds.Count > 10) throw new DomainException("TOO_MANY_PHOTOS", "Tối đa 10 ảnh mỗi cây");
        await media.GetOwnedAsync(mediaIds, userId, ct);
        var removed = p.MediaIds.Except(mediaIds).ToList();
        if (removed.Count > 0) await media.AttachAsync(removed, null!, ct); // ảnh bỏ ra sẽ được job dọn
        await media.AttachAsync(mediaIds, $"plant:{p.Id}", ct);
        p.Name = name;
        p.SpeciesId = string.IsNullOrWhiteSpace(req.SpeciesId) ? null : req.SpeciesId;
        p.SpeciesName = p.SpeciesId is null ? null : (await catalog.GetSpeciesAsync(p.SpeciesId, ct)).CommonName;
        p.MediaIds = mediaIds;
        p.Location = req.Location?.Trim();
        p.AcquiredAt = req.AcquiredAt;
        p.Note = req.Note?.Trim();
        p.UpdatedAt = clock.GetUtcNow().UtcDateTime;
    }

    public async Task<MyPlant> CreateAsync(string userId, PlantRequest req, CancellationToken ct)
    {
        if (await Plants.CountDocumentsAsync(p => p.UserId == userId, cancellationToken: ct) >= MaxPlants)
            throw new DomainException("LIMIT_REACHED", $"Hồ sơ vườn tối đa {MaxPlants} cây");
        var p = new MyPlant { UserId = userId, CreatedAt = clock.GetUtcNow().UtcDateTime };
        await Apply(p, req, userId, ct);
        await Plants.InsertOneAsync(p, cancellationToken: ct);
        return p;
    }

    public async Task<MyPlant> UpdateAsync(string userId, string id, PlantRequest req, CancellationToken ct)
    {
        var p = await GetOwnAsync(userId, id, ct);
        await Apply(p, req, userId, ct);
        await Plants.ReplaceOneAsync(x => x.Id == id, p, cancellationToken: ct);
        return p;
    }

    public async Task DeleteAsync(string userId, string id, CancellationToken ct)
    {
        var p = await GetOwnAsync(userId, id, ct);
        await Reminders.DeleteManyAsync(r => r.PlantId == id, ct);
        await Plants.DeleteOneAsync(x => x.Id == id, ct);
        await media.AttachAsync(p.MediaIds, null!, ct);
    }

    public async Task<CareReminder> UpsertReminderAsync(string userId, string plantId, string? reminderId, ReminderRequest req, CancellationToken ct)
    {
        await GetOwnAsync(userId, plantId, ct);
        var interval = req.Interval ?? 1;
        if (interval is < 1 or > 365) throw new DomainException("INVALID_INTERVAL", "Chu kỳ lặp từ 1 đến 365");
        if (req.Note?.Length > 300) throw new DomainException("INVALID_NOTE", "Ghi chú tối đa 300 ký tự");
        var now = clock.GetUtcNow().UtcDateTime;
        CareReminder r;
        if (reminderId is null)
        {
            if (await Reminders.CountDocumentsAsync(x => x.PlantId == plantId, cancellationToken: ct) >= MaxRemindersPerPlant)
                throw new DomainException("LIMIT_REACHED", $"Mỗi cây tối đa {MaxRemindersPerPlant} lời nhắc");
            r = new CareReminder { UserId = userId, PlantId = plantId, CreatedAt = now };
        }
        else r = await Reminders.Find(x => x.Id == reminderId && x.UserId == userId && x.PlantId == plantId).FirstOrDefaultAsync(ct)
                 ?? throw DomainException.NotFound("lời nhắc");

        r.Kind = req.Kind;
        r.Note = req.Note?.Trim();
        r.AnchorAt = req.At.UtcDateTime;
        r.Repeat = req.Repeat;
        r.Interval = interval;
        r.Enabled = req.Enabled ?? true;
        r.NextAt = ReminderSchedule.NextAfter(r, now);
        if (r.NextAt is null && r.Enabled) throw new DomainException("TIME_IN_PAST", "Thời điểm nhắc đã qua. Chọn thời điểm trong tương lai hoặc bật lặp lại");
        await Reminders.ReplaceOneAsync(x => x.Id == r.Id, r, new ReplaceOptions { IsUpsert = true }, ct);
        return r;
    }

    /// <summary>Gửi các lời nhắc đến hạn. Lỡ nhiều lần (server tắt) chỉ gửi 1 lần rồi nhảy tới lần kế tiếp.</summary>
    public async Task<int> FireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await Reminders.Find(r => r.Enabled && r.NextAt != null && r.NextAt <= now).Limit(500).ToListAsync(ct);
        foreach (var r in due)
        {
            var next = ReminderSchedule.NextAfter(r, now);
            // Chiếm lời nhắc trước khi gửi để 2 instance không gửi trùng.
            var claimed = await Reminders.UpdateOneAsync(x => x.Id == r.Id && x.NextAt == r.NextAt,
                Builders<CareReminder>.Update.Set(x => x.NextAt, next).Set(x => x.LastFiredAt, now).Set(x => x.Enabled, next != null), cancellationToken: ct);
            if (claimed.ModifiedCount == 0) continue;
            var plant = await Plants.Find(p => p.Id == r.PlantId).FirstOrDefaultAsync(ct);
            if (plant is null) continue;
            await notifications.SendAsync(r.UserId, "care." + r.Kind.ToString().ToLowerInvariant(),
                $"{ReminderSchedule.Label(r.Kind)}: {plant.Name}", r.Note, $"/vuon-cua-toi?cay={plant.Id}", ct);
        }
        return due.Count;
    }
}

public class CareReminderWorker(IServiceScopeFactory scopes, ILogger<CareReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PlantCareService>().FireDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi job nhắc chăm cây");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class PlantCareEndpoints
{
    public static void MapPlantCare(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me/garden").WithTags("Plant Care").RequireAuthorization(Policies.Member);

        g.MapGet("/plants", async (ClaimsPrincipal p, PlantCareService svc, MediaService media, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var plants = await svc.Plants.Find(x => x.UserId == userId).SortByDescending(x => x.CreatedAt).ToListAsync(ct);
            var reminders = await svc.Reminders.Find(r => r.UserId == userId).ToListAsync(ct);
            var mediaIds = plants.SelectMany(x => x.MediaIds).ToList();
            var items = await media.Items.Find(m => mediaIds.Contains(m.Id)).ToListAsync(ct);
            return plants.Select(x => ToDto(x, reminders.Where(r => r.PlantId == x.Id), items));
        });
        g.MapGet("/plants/{id}", async (string id, ClaimsPrincipal p, PlantCareService svc, MediaService media, CancellationToken ct) =>
        {
            var plant = await svc.GetOwnAsync(p.UserId(), id, ct);
            var reminders = await svc.Reminders.Find(r => r.PlantId == id).ToListAsync(ct);
            var items = await media.Items.Find(m => plant.MediaIds.Contains(m.Id)).ToListAsync(ct);
            return ToDto(plant, reminders, items);
        });
        g.MapPost("/plants", async (PlantRequest req, ClaimsPrincipal p, PlantCareService svc, CancellationToken ct) =>
            await svc.CreateAsync(p.UserId(), req, ct));
        g.MapPut("/plants/{id}", async (string id, PlantRequest req, ClaimsPrincipal p, PlantCareService svc, CancellationToken ct) =>
            await svc.UpdateAsync(p.UserId(), id, req, ct));
        g.MapDelete("/plants/{id}", async (string id, ClaimsPrincipal p, PlantCareService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(p.UserId(), id, ct);
            return Results.NoContent();
        });

        g.MapPost("/plants/{id}/reminders", async (string id, ReminderRequest req, ClaimsPrincipal p, PlantCareService svc, CancellationToken ct) =>
            await svc.UpsertReminderAsync(p.UserId(), id, null, req, ct));
        g.MapPut("/plants/{id}/reminders/{rid}", async (string id, string rid, ReminderRequest req, ClaimsPrincipal p, PlantCareService svc, CancellationToken ct) =>
            await svc.UpsertReminderAsync(p.UserId(), id, rid, req, ct));
        g.MapDelete("/plants/{id}/reminders/{rid}", async (string id, string rid, ClaimsPrincipal p, PlantCareService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.Reminders.DeleteOneAsync(r => r.Id == rid && r.PlantId == id && r.UserId == userId, ct);
            return Results.NoContent();
        });
        // Lịch sắp tới của cả vườn (mặc định 7 ngày).
        g.MapGet("/upcoming", async (int? days, ClaimsPrincipal p, PlantCareService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var until = clock.GetUtcNow().UtcDateTime.AddDays(Math.Clamp(days ?? 7, 1, 60));
            var list = await svc.Reminders.Find(r => r.UserId == userId && r.Enabled && r.NextAt != null && r.NextAt <= until).SortBy(r => r.NextAt).Limit(100).ToListAsync(ct);
            var ids = list.Select(r => r.PlantId).Distinct().ToList();
            var names = (await svc.Plants.Find(x => ids.Contains(x.Id)).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
            return list.Select(r => new { r.Id, r.PlantId, plantName = names.GetValueOrDefault(r.PlantId), r.Kind, label = ReminderSchedule.Label(r.Kind), r.NextAt, r.Note });
        });
    }

    static object ToDto(MyPlant p, IEnumerable<CareReminder> reminders, List<MediaItem> media) => new
    {
        p.Id, p.Name, p.SpeciesId, p.SpeciesName, p.Location, p.AcquiredAt, p.Note, p.CreatedAt,
        photos = p.MediaIds.Select(id => media.FirstOrDefault(m => m.Id == id)).Where(m => m is not null).Select(m => MediaService.ToDto(m!)),
        reminders = reminders.OrderBy(r => r.NextAt ?? DateTime.MaxValue),
    };
}
