using System.Security.Cryptography;
using System.Text;
using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Pricing;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Wallet;

public class PaymentOptions
{
    /// <summary>Khóa ký webhook của cổng thanh toán. Mỗi cổng thật (VNPay/MoMo/PayOS) có định dạng chữ ký riêng — sẽ viết adapter.</summary>
    public string WebhookSecret { get; set; } = "";
    public string Gateway { get; set; } = "VIETQR";
    public int TopUpExpiryMinutes { get; set; } = 30;
    public string BankAccountName { get; set; } = "CONG TY CHAM XANH";
}

public record CreateTopUpRequest(string PackageCode, int ExpectedPriceBookVersion);
public record PaymentWebhook(string TopUpId, string Status, long AmountVnd, string GatewayRef);

public class TopUpService(IMongoDatabase db, IMongoClient client, WalletService wallets, PricingService pricing, PaymentOptions options, TimeProvider clock)
{
    public IMongoCollection<TopUp> TopUps { get; } = db.GetCollection<TopUp>("topUps");

    public Task EnsureIndexesAsync() => TopUps.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<TopUp>(Builders<TopUp>.IndexKeys.Ascending(t => t.GatewayRef),
            new CreateIndexOptions<TopUp> { Unique = true, PartialFilterExpression = Builders<TopUp>.Filter.Type(t => t.GatewayRef, MongoDB.Bson.BsonType.String) }),
        new CreateIndexModel<TopUp>(Builders<TopUp>.IndexKeys.Ascending(t => t.UserId).Descending(t => t.CreatedAt)),
    ]);

    public async Task<object> CreateAsync(string userId, CreateTopUpRequest req, CancellationToken ct)
    {
        var book = await pricing.GetActiveAsync(ct) ?? throw new DomainException("NO_ACTIVE_PRICE_BOOK", "Chưa có bảng giá");
        var pkg = book.TopUpPackages.FirstOrDefault(p => p.Code == req.PackageCode && p.Enabled)
            ?? throw DomainException.NotFound("gói nạp");
        if (book.Version != req.ExpectedPriceBookVersion)
            throw DomainException.Conflict("PRICE_CHANGED", "Bảng giá vừa thay đổi, vui lòng xem lại gói nạp", new { book.Version, pkg }); // BR-PRC-06
        var now = clock.GetUtcNow().UtcDateTime;
        var topUp = new TopUp
        {
            UserId = userId, Gateway = options.Gateway, CreatedAt = now, ExpiresAt = now.AddMinutes(options.TopUpExpiryMinutes),
            Snapshot = new TopUpSnapshot
            {
                PriceBookVersion = book.Version, PackageCode = pkg.Code, PriceVnd = pkg.PriceVnd, Xu = pkg.Xu,
                BonusXu = pkg.BonusXu, BonusExpiryDays = pkg.BonusExpiryDays,
            },
        };
        await TopUps.InsertOneAsync(topUp, cancellationToken: ct);
        // Nội dung chuyển khoản chứa mã nạp để đối soát tự động (VietQR).
        return new { topUp.Id, topUp.Snapshot, topUp.ExpiresAt, transferContent = $"CX{topUp.Id[^10..].ToUpperInvariant()}", options.BankAccountName };
    }

    public static string Sign(string secret, string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    /// <summary>Webhook cổng thanh toán: kiểm chữ ký, idempotent theo gatewayRef, số tiền phải khớp snapshot.
    /// Tiền về sau khi lệnh nạp hết hạn vẫn được cộng Xu (người dùng đã trả tiền).</summary>
    public async Task<TopUp> HandleWebhookAsync(string rawBody, string? signature, PaymentWebhook payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.WebhookSecret) || signature is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(options.WebhookSecret, rawBody)), Encoding.UTF8.GetBytes(signature.ToLowerInvariant())))
            throw new DomainException("INVALID_SIGNATURE", "Chữ ký webhook không hợp lệ", StatusCodes.Status401Unauthorized);

        var topUp = (MongoDB.Bson.ObjectId.TryParse(payload.TopUpId, out _) ? await TopUps.Find(t => t.Id == payload.TopUpId).FirstOrDefaultAsync(ct) : null)
            ?? throw DomainException.NotFound("lệnh nạp");
        if (topUp.Status == TopUpStatus.Paid) return topUp; // webhook gửi lại
        if (payload.Status != "PAID")
        {
            await TopUps.UpdateOneAsync(t => t.Id == topUp.Id && t.Status == TopUpStatus.Pending, Builders<TopUp>.Update.Set(t => t.Status, TopUpStatus.Failed), cancellationToken: ct);
            return (await TopUps.Find(t => t.Id == topUp.Id).FirstAsync(ct));
        }
        if (payload.AmountVnd != topUp.Snapshot.PriceVnd)
            throw new DomainException("AMOUNT_MISMATCH", $"Số tiền {payload.AmountVnd} không khớp gói {topUp.Snapshot.PriceVnd} — chuyển kế toán đối soát");

        var now = clock.GetUtcNow().UtcDateTime;
        using var s = await client.StartSessionAsync(cancellationToken: ct);
        await s.WithTransactionAsync(async (session, token) =>
        {
            var upd = await TopUps.UpdateOneAsync(session, t => t.Id == topUp.Id && t.Status != TopUpStatus.Paid, Builders<TopUp>.Update
                .Set(t => t.Status, TopUpStatus.Paid).Set(t => t.GatewayRef, payload.GatewayRef).Set(t => t.PaidAt, now), cancellationToken: token);
            if (upd.ModifiedCount == 0) return true;
            await wallets.CreditAsync(session, topUp.UserId, topUp.Snapshot.Xu, LotKind.Paid, null, LedgerType.TopUp,
                $"topup:{topUp.Id}", "topUp", topUp.Id, $"Nạp gói {topUp.Snapshot.PackageCode}", token);
            if (topUp.Snapshot.BonusXu > 0)
                await wallets.CreditAsync(session, topUp.UserId, topUp.Snapshot.BonusXu, LotKind.Bonus, now.AddDays(topUp.Snapshot.BonusExpiryDays),
                    LedgerType.Bonus, $"topup-bonus:{topUp.Id}", "topUp", topUp.Id, "Xu thưởng khi nạp", token);
            return true;
        }, cancellationToken: ct);
        return await TopUps.Find(t => t.Id == topUp.Id).FirstAsync(ct);
    }
}
