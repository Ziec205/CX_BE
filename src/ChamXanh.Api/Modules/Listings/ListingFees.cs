using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Plans;
using ChamXanh.Api.Modules.Wallet;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Listings;

/// <summary>Một lượt đăng tin đã tính: dùng hạn mức tháng của gói (Xu = 0) hoặc trả Xu khi hết lượt.
/// Mỗi lần đăng mới hoặc gia hạn tin là một lượt (Cycle tăng dần trên cùng tin).</summary>
public class ListingCharge
{
    [BsonId] public string Id { get; set; } = default!; // {listingId}:{cycle}
    public string UserId { get; set; } = default!;
    public string ListingId { get; set; } = default!;
    public int Cycle { get; set; }
    /// <summary>Tháng theo giờ Việt Nam, dạng yyyy-MM.</summary>
    public string Month { get; set; } = default!;
    [BsonRepresentation(MongoDB.Bson.BsonType.String)] public PlanCode Plan { get; set; }
    public bool FromQuota { get; set; }
    public long Xu { get; set; }
    public int ListingDays { get; set; }
    public DateTime At { get; set; }
}

public record ListingQuota(PlanCode Plan, string PlanName, int MonthlyQuota, int UsedThisMonth, int Remaining, long ExtraListingXu,
    long NextCostXu, int ListingDays, long WalletBalance, string Month);

public class ListingFeeService(IMongoDatabase db, IMongoClient client, PlanService plans, WalletService wallets, TimeProvider clock)
{
    public IMongoCollection<ListingCharge> Charges { get; } = db.GetCollection<ListingCharge>("listingCharges");

    public Task EnsureIndexesAsync() => Charges.Indexes.CreateOneAsync(new CreateIndexModel<ListingCharge>(
        Builders<ListingCharge>.IndexKeys.Ascending(c => c.UserId).Ascending(c => c.Month).Ascending(c => c.FromQuota)));

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Hạn mức tính theo tháng dương lịch giờ Việt Nam.</summary>
    public static string MonthOf(DateTime utc) => utc.AddHours(7).ToString("yyyy-MM");

    public async Task<ListingQuota> GetQuotaAsync(string userId, CancellationToken ct)
    {
        var (plan, _) = await plans.GetEffectiveAsync(userId, ct);
        var month = MonthOf(Now);
        var used = (int)await Charges.CountDocumentsAsync(c => c.UserId == userId && c.Month == month && c.FromQuota, cancellationToken: ct);
        var remaining = Math.Max(0, plan.ListingsPerMonth - used);
        var wallet = await wallets.GetAsync(userId, ct);
        return new(plan.Code, plan.Name, plan.ListingsPerMonth, used, remaining, plan.ExtraListingXu,
            remaining > 0 ? 0 : plan.ExtraListingXu, plan.ListingDays, wallet.Balance(Now), month);
    }

    /// <summary>Tính một lượt đăng cho tin. Còn lượt trong tháng thì miễn phí; hết lượt thì trừ Xu,
    /// nhưng chỉ khi người dùng đã đồng ý mức phí (acceptFeeXu) để không trừ tiền bất ngờ.</summary>
    public async Task<ListingCharge> ChargeAsync(string userId, string listingId, int cycle, long? acceptFeeXu, CancellationToken ct)
    {
        var id = $"{listingId}:{cycle}";
        if (await Charges.Find(c => c.Id == id).FirstOrDefaultAsync(ct) is { } done) return done;

        var (plan, _) = await plans.GetEffectiveAsync(userId, ct);
        var month = MonthOf(Now);
        ListingCharge? result = null;
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        await session.WithTransactionAsync(async (s, token) =>
        {
            var used = await Charges.CountDocumentsAsync(s, c => c.UserId == userId && c.Month == month && c.FromQuota, cancellationToken: token);
            var fromQuota = used < plan.ListingsPerMonth;
            var xu = fromQuota ? 0 : plan.ExtraListingXu;
            if (!fromQuota)
            {
                if ((acceptFeeXu ?? 0) < xu)
                    throw new DomainException("LISTING_FEE_REQUIRED",
                        $"Bạn đã dùng hết {plan.ListingsPerMonth} tin của gói {plan.Name} tháng này. Tin này tốn {xu} Xu ({xu * 1000:N0}đ)",
                        StatusCodes.Status402PaymentRequired, new { xu, quota = plan.ListingsPerMonth, used });
                await wallets.DebitAsync(s, userId, xu, $"listing:{id}", "listingFee", listingId,
                    cycle == 1 ? "Phí đăng tin" : "Phí gia hạn tin", token);
            }
            result = new ListingCharge
            {
                Id = id, UserId = userId, ListingId = listingId, Cycle = cycle, Month = month, Plan = plan.Code,
                FromQuota = fromQuota, Xu = xu, ListingDays = plan.ListingDays, At = Now,
            };
            await Charges.InsertOneAsync(s, result, cancellationToken: token);
            return true;
        }, cancellationToken: ct);
        return result!;
    }
}
