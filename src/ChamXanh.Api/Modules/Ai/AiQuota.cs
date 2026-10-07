using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Plans;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Ai;

/// <summary>Số lượt AI đã dùng trong một ngày (giờ Việt Nam) của một người.</summary>
public class AiUsage
{
    [BsonId] public string Id { get; set; } = default!; // {userId}:{yyyy-MM-dd}
    public string UserId { get; set; } = default!;
    public string Day { get; set; } = default!;
    public int Count { get; set; }
    public DateTime ExpireAt { get; set; }
}

public record AiQuota(int Used, int Limit)
{
    public int Remaining => Math.Max(0, Limit - Used);
}

/// <summary>Mọi tính năng AI dùng chung một hạn mức/ngày theo gói; đặt lại lúc 0h giờ Việt Nam.</summary>
public class AiQuotaService(IMongoDatabase db, PlanService plans, TimeProvider clock)
{
    public IMongoCollection<AiUsage> Usage { get; } = db.GetCollection<AiUsage>("aiUsage");

    public Task EnsureIndexesAsync() => Usage.Indexes.CreateOneAsync(
        new CreateIndexModel<AiUsage>(Builders<AiUsage>.IndexKeys.Ascending(u => u.ExpireAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));

    (string Id, string Day, DateTime NextReset) Today(string userId)
    {
        var vn = clock.GetUtcNow().UtcDateTime.AddHours(7);
        var day = vn.ToString("yyyy-MM-dd");
        return ($"{userId}:{day}", day, vn.Date.AddDays(1).AddHours(-7));
    }

    public async Task<AiQuota> GetAsync(string userId, CancellationToken ct)
    {
        var (plan, _) = await plans.GetEffectiveAsync(userId, ct);
        var id = Today(userId).Id;
        var used = await Usage.Find(u => u.Id == id).Project(u => u.Count).FirstOrDefaultAsync(ct);
        return new(used, plan.AiPerDay);
    }

    /// <summary>Giữ trước 1 lượt (nguyên tử, không vượt hạn mức khi gửi đồng thời). Lỗi hoặc câu ngoài chủ đề thì gọi Refund.</summary>
    public async Task<AiQuota> ConsumeAsync(string userId, CancellationToken ct)
    {
        var (plan, _) = await plans.GetEffectiveAsync(userId, ct);
        var (id, day, reset) = Today(userId);
        try
        {
            var doc = await Usage.FindOneAndUpdateAsync(
                Builders<AiUsage>.Filter.Where(u => u.Id == id && u.Count < plan.AiPerDay),
                Builders<AiUsage>.Update.Inc(u => u.Count, 1).SetOnInsert(u => u.UserId, userId).SetOnInsert(u => u.Day, day)
                    .SetOnInsert(u => u.ExpireAt, reset.AddDays(2)),
                new FindOneAndUpdateOptions<AiUsage> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
            return new(doc.Count, plan.AiPerDay);
        }
        catch (MongoCommandException ex) when (ex.Code == 11000) { throw Exhausted(plan); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { throw Exhausted(plan); }
    }

    static DomainException Exhausted(PlanDefinition plan) => new("AI_QUOTA_EXCEEDED",
        plan.Code == PlanCode.Pro
            ? $"Bạn đã dùng hết {plan.AiPerDay} lượt AI hôm nay. Lượt mới có lúc 0 giờ."
            : $"Bạn đã dùng hết {plan.AiPerDay} lượt AI hôm nay. Nâng gói để có thêm lượt, hoặc quay lại sau 0 giờ.",
        StatusCodes.Status429TooManyRequests, new { limit = plan.AiPerDay, plan = plan.Code.ToString() });

    public async Task RefundAsync(string userId, CancellationToken ct)
    {
        var id = Today(userId).Id;
        await Usage.UpdateOneAsync(u => u.Id == id && u.Count > 0, Builders<AiUsage>.Update.Inc(u => u.Count, -1), cancellationToken: ct);
    }
}
