using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Moderation;

public enum CaseTrigger { New, Edit, Report, RandomAudit, Appeal }
public enum CaseQueue { Normal, Priority, Audit }
public enum CaseStatus { Open, Resolved }
public enum Decision { Approve, Reject, RequestChanges, Remove, Dismiss }

public class ModerationCase
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ListingId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public CaseTrigger Trigger { get; set; }
    [BsonRepresentation(BsonType.String)] public CaseQueue Queue { get; set; }
    [BsonRepresentation(BsonType.String)] public CaseStatus Status { get; set; } = CaseStatus.Open;
    public int RiskScore { get; set; }
    public List<string> Signals { get; set; } = [];
    [BsonRepresentation(BsonType.String)] public Decision? Decision { get; set; }
    public string? ReasonCode { get; set; }
    public string? Note { get; set; }
    public string? DecidedById { get; set; }
    public string? DecidedByName { get; set; }
    /// <summary>Với khiếu nại: người xử lý lần đầu, người xét lại phải khác (BR-MOD-05).</summary>
    public string? OriginalDeciderId { get; set; }
    public DateTime SlaDueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public enum ReportReason { Scam, AlreadySold, WrongPrice, FakePhoto, Prohibited, Duplicate, WrongCategory, Unreachable, Other }

public class ListingReport
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ListingId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    public string ReporterId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public ReportReason Reason { get; set; }
    public string? Note { get; set; }
    public double Weight { get; set; }
    public bool FromEscrowBuyer { get; set; }
    public string Status { get; set; } = "Open"; // Open | Accepted | Dismissed
    public DateTime CreatedAt { get; set; }
}

public class Violation
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public int Points { get; set; }
    public string ReasonCode { get; set; } = default!;
    public string? ListingId { get; set; }
    public string ConfirmedById { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Bộ lý do chuẩn có mã (BR-MOD-06) kèm điểm vi phạm mặc định (BR-MOD-03).</summary>
public static class ReasonCodes
{
    public static readonly IReadOnlyDictionary<string, (string Label, int Points)> All = new Dictionary<string, (string, int)>
    {
        ["WRONG_CATEGORY"] = ("Sai danh mục", 1),
        ["DUPLICATE"] = ("Tin trùng lặp", 1),
        ["LOW_QUALITY"] = ("Ảnh/mô tả không đạt yêu cầu", 0),
        ["CONTACT_IN_TEXT"] = ("Có SĐT/link trong nội dung", 0),
        ["MISSING_DOCUMENTS"] = ("Thiếu giấy tờ cho hàng hạn chế", 0),
        ["STOLEN_PHOTO"] = ("Ảnh mạng / ảnh của người khác", 3),
        ["WRONG_PRICE"] = ("Sai giá cố ý", 3),
        ["MISLEADING"] = ("Mô tả sai sự thật", 3),
        ["PROHIBITED"] = ("Hàng cấm", 10),
        ["SCAM"] = ("Lừa đảo", 10),
        ["OTHER"] = ("Khác", 0),
    };

    public static void EnsureValid(string code)
    {
        if (!All.ContainsKey(code)) throw new DomainException("INVALID_REASON", $"Mã lý do không hợp lệ. Hợp lệ: {string.Join(", ", All.Keys)}");
    }
}

public class ModerationService(IMongoDatabase db, TimeProvider clock, UserService users)
{
    public IMongoCollection<ModerationCase> Cases { get; } = db.GetCollection<ModerationCase>("moderationCases");
    public IMongoCollection<ListingReport> Reports { get; } = db.GetCollection<ListingReport>("reports");
    public IMongoCollection<Violation> Violations { get; } = db.GetCollection<Violation>("violations");
    readonly IMongoCollection<ModerationConfig> _config = db.GetCollection<ModerationConfig>("moderationConfig");

    public async Task EnsureIndexesAsync()
    {
        await Cases.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ModerationCase>(Builders<ModerationCase>.IndexKeys.Ascending(c => c.Status).Ascending(c => c.Queue).Ascending(c => c.SlaDueAt)),
            new CreateIndexModel<ModerationCase>(Builders<ModerationCase>.IndexKeys.Ascending(c => c.ListingId)),
        ]);
        await Reports.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ListingReport>(Builders<ListingReport>.IndexKeys.Ascending(r => r.ListingId).Ascending(r => r.Status)),
            new CreateIndexModel<ListingReport>(Builders<ListingReport>.IndexKeys.Ascending(r => r.ListingId).Ascending(r => r.ReporterId), new CreateIndexOptions { Unique = true }),
        ]);
        await Violations.Indexes.CreateOneAsync(new CreateIndexModel<Violation>(Builders<Violation>.IndexKeys.Ascending(v => v.UserId).Ascending(v => v.ExpiresAt)));
    }

    public async Task<ModerationConfig> GetConfigAsync(CancellationToken ct) =>
        await _config.Find(c => c.Id == "default").FirstOrDefaultAsync(ct) ?? new ModerationConfig();

    public Task SaveConfigAsync(ModerationConfig cfg, CancellationToken ct)
    {
        cfg.Id = "default";
        return _config.ReplaceOneAsync(c => c.Id == "default", cfg, new ReplaceOptions { IsUpsert = true }, ct);
    }

    /// <summary>A-06: xử lý ≤ 2 giờ trong khung 7h–22h (giờ VN); ngoài khung thì tính từ 7h sáng hôm sau.</summary>
    public static DateTime ComputeSla(DateTime utcNow, CaseQueue queue)
    {
        var vn = utcNow.AddHours(7);
        var start = vn.Hour switch
        {
            < 7 => vn.Date.AddHours(7),
            >= 22 => vn.Date.AddDays(1).AddHours(7),
            _ => vn,
        };
        var due = start.AddHours(queue == CaseQueue.Priority ? 1 : 2);
        return due.AddHours(-7);
    }

    public async Task<ModerationCase> OpenCaseAsync(string listingId, string sellerId, CaseTrigger trigger, CaseQueue queue, int score,
        List<string> signals, CancellationToken ct, string? originalDeciderId = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Không mở trùng: nếu tin đã có hồ sơ mở cùng loại thì cập nhật hồ sơ đó.
        var existing = await Cases.Find(c => c.ListingId == listingId && c.Status == CaseStatus.Open && c.Trigger == trigger).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            await Cases.UpdateOneAsync(c => c.Id == existing.Id, Builders<ModerationCase>.Update
                .Set(c => c.RiskScore, score).Set(c => c.Signals, signals).Set(c => c.Queue, queue), cancellationToken: ct);
            return existing;
        }
        var mc = new ModerationCase
        {
            ListingId = listingId, SellerId = sellerId, Trigger = trigger, Queue = queue, RiskScore = score, Signals = signals,
            CreatedAt = now, SlaDueAt = ComputeSla(now, queue), OriginalDeciderId = originalDeciderId,
        };
        await Cases.InsertOneAsync(mc, cancellationToken: ct);
        return mc;
    }

    public async Task<ModerationCase> GetOpenCaseAsync(string caseId, CancellationToken ct)
    {
        var c = ObjectId.TryParse(caseId, out _) ? await Cases.Find(x => x.Id == caseId).FirstOrDefaultAsync(ct) : null;
        if (c is null) throw DomainException.NotFound("hồ sơ kiểm duyệt");
        if (c.Status != CaseStatus.Open) throw DomainException.Conflict("CASE_CLOSED", "Hồ sơ đã được xử lý");
        return c;
    }

    public async Task ResolveAsync(ModerationCase c, Decision decision, string? reasonCode, string? note, string actorId, string actorName, CancellationToken ct)
    {
        var res = await Cases.UpdateOneAsync(x => x.Id == c.Id && x.Status == CaseStatus.Open, Builders<ModerationCase>.Update
            .Set(x => x.Status, CaseStatus.Resolved).Set(x => x.Decision, decision).Set(x => x.ReasonCode, reasonCode)
            .Set(x => x.Note, note).Set(x => x.DecidedById, actorId).Set(x => x.DecidedByName, actorName)
            .Set(x => x.DecidedAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("CASE_CLOSED", "Hồ sơ đã được người khác xử lý");
    }

    public Task CloseOpenCasesForListingAsync(string listingId, string note, CancellationToken ct) =>
        Cases.UpdateManyAsync(c => c.ListingId == listingId && c.Status == CaseStatus.Open, Builders<ModerationCase>.Update
            .Set(c => c.Status, CaseStatus.Resolved).Set(c => c.Decision, Decision.Dismiss).Set(c => c.Note, note)
            .Set(c => c.DecidedAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);

    public async Task<int> RecentRejectionsAsync(string sellerId, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        return (int)await Cases.CountDocumentsAsync(c => c.SellerId == sellerId && c.DecidedAt > since
            && (c.Decision == Decision.Reject || c.Decision == Decision.Remove), cancellationToken: ct);
    }

    /// <summary>Cộng điểm vi phạm (chỉ khi có người xác nhận — sửa L09) và áp chế tài theo ngưỡng (BR-MOD-04).</summary>
    public async Task<int> AddViolationAsync(string userId, string reasonCode, int points, string? listingId, string confirmedById, CancellationToken ct)
    {
        if (points <= 0) return await ActivePointsAsync(userId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        await Violations.InsertOneAsync(new Violation
        {
            UserId = userId, Points = points, ReasonCode = reasonCode, ListingId = listingId, ConfirmedById = confirmedById,
            CreatedAt = now, ExpiresAt = now.AddDays(90),
        }, cancellationToken: ct);
        var total = await ActivePointsAsync(userId, ct);

        var u = Builders<User>.Update.Set(x => x.ActiveViolationPoints, total);
        if (total >= 10) u = u.Set(x => x.Status, UserStatus.Locked).Set(x => x.LockedUntil, now.AddDays(30));
        else if (total >= 6) u = u.Set(x => x.Status, UserStatus.Restricted).Set(x => x.PostingRestrictedUntil, now.AddDays(7));
        await users.Users.UpdateOneAsync(x => x.Id == userId, u, cancellationToken: ct);
        return total;
    }

    public async Task<int> ActivePointsAsync(string userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var list = await Violations.Find(v => v.UserId == userId && v.ExpiresAt > now).Project(v => v.Points).ToListAsync(ct);
        return list.Sum();
    }

    /// <summary>BR-MOD-01: trọng số báo cáo theo độ tin cậy tài khoản (sửa L19).</summary>
    public static double ReportWeight(User reporter, DateTime now) =>
        reporter.Flags.HasVerifiedGarden || (reporter.CreatedAt <= now.AddDays(-30) && reporter.ActiveViolationPoints == 0) ? 1
        : reporter.CreatedAt > now.AddDays(-7) ? 0.3 : 0.5;

    /// <summary>BR-MOD-02: người báo cáo sai ≥ 5 lần trong 30 ngày mất quyền báo cáo 30 ngày.</summary>
    public async Task EnsureCanReportAsync(string reporterId, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var dismissed = await Reports.CountDocumentsAsync(r => r.ReporterId == reporterId && r.Status == "Dismissed" && r.CreatedAt > since, cancellationToken: ct);
        if (dismissed >= 5) throw DomainException.Forbidden("Bạn tạm thời không thể báo cáo do nhiều báo cáo không chính xác");
    }

    public async Task<(double Weight, int Reporters, bool HasEscrowScam)> ReportStatsAsync(string listingId, CancellationToken ct)
    {
        var open = await Reports.Find(r => r.ListingId == listingId && r.Status == "Open").ToListAsync(ct);
        return (open.Sum(r => r.Weight), open.Select(r => r.ReporterId).Distinct().Count(),
            open.Any(r => r.FromEscrowBuyer && r.Reason == ReportReason.Scam));
    }

    public Task SetReportsStatusAsync(string listingId, string status, CancellationToken ct) =>
        Reports.UpdateManyAsync(r => r.ListingId == listingId && r.Status == "Open",
            Builders<ListingReport>.Update.Set(r => r.Status, status), cancellationToken: ct);
}
