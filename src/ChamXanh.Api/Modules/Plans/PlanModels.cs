using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Plans;

// Gói sử dụng cho người dùng (khác gói Trang vườn trả bằng Xu): mở rộng Hồ sơ vườn và lượt dùng Trợ lý AI.

public enum PlanCode { Free, Plus, Pro }

public record PlanDefinition(
    PlanCode Code, string Name, string Tagline, long MonthlyVnd, long YearlyVnd,
    int GardenPlants, int AiPerDay, bool MarketCompare, bool SellerAi);

public static class PlanCatalog
{
    public static readonly PlanDefinition Free = new(PlanCode.Free, "Miễn phí", "Dùng thử Hồ sơ vườn và Trợ lý AI",
        0, 0, GardenPlants: 3, AiPerDay: 3, MarketCompare: false, SellerAi: false);
    public static readonly PlanDefinition Plus = new(PlanCode.Plus, "Xanh Plus", "Chăm nhiều cây hơn, hỏi AI mỗi ngày",
        39_000, 390_000, GardenPlants: 20, AiPerDay: 15, MarketCompare: false, SellerAi: false);
    /// <summary>Nhà vườn chuyên nghiệp / người dùng AI nhiều: thêm so sánh cây trong chợ và công cụ bán hàng bằng AI.</summary>
    public static readonly PlanDefinition Pro = new(PlanCode.Pro, "Xanh Pro", "Cho nhà vườn chuyên nghiệp và người dùng AI nhiều",
        69_000, 690_000, GardenPlants: 100, AiPerDay: 40, MarketCompare: true, SellerAi: true);

    public static readonly IReadOnlyList<PlanDefinition> All = [Free, Plus, Pro];

    /// <summary>Hai kỳ hạn: 1 tháng hoặc 12 tháng (bằng giá 10 tháng).</summary>
    public static readonly IReadOnlyList<int> Terms = [1, 12];

    public static PlanDefinition Get(PlanCode code) => code switch { PlanCode.Plus => Plus, PlanCode.Pro => Pro, _ => Free };

    public static long Price(PlanDefinition p, int months) => months switch
    {
        1 => p.MonthlyVnd,
        12 => p.YearlyVnd,
        _ => throw new ArgumentOutOfRangeException(nameof(months)),
    };

    /// <summary>Gói hiện tại sau khi thanh toán thêm một kỳ.
    /// Cùng gói: cộng nối thời hạn. Nâng gói: số ngày còn lại của gói cũ quy đổi theo tỉ lệ giá tháng rồi cộng vào gói mới.
    /// Mua gói thấp hơn khi gói cao còn hạn (chỉ xảy ra khi tiền về muộn): quy đổi kỳ mua sang ngày của gói cao, không mất tiền.</summary>
    public static (PlanCode Plan, DateTime StartAt, DateTime EndAt) Apply(Subscription? current, PlanCode bought, int months, DateTime now)
    {
        var term = now.AddMonths(months) - now;
        if (current is null || current.Plan == PlanCode.Free || current.EndAt <= now)
            return (bought, now, now + term);
        if (current.Plan == bought)
            return (bought, current.StartAt, current.EndAt.AddMonths(months));

        var currentPrice = Get(current.Plan).MonthlyVnd;
        var boughtPrice = Get(bought).MonthlyVnd;
        if (boughtPrice > currentPrice)
            return (bought, now, now + term + Scale(current.EndAt - now, currentPrice, boughtPrice));
        return (current.Plan, current.StartAt, current.EndAt + Scale(term, boughtPrice, currentPrice));
    }

    static TimeSpan Scale(TimeSpan span, long fromPrice, long toPrice) =>
        TimeSpan.FromMinutes(Math.Floor(span.TotalMinutes * fromPrice / toPrice));
}

/// <summary>Gói đang dùng của một người (mỗi người một bản ghi, _id = userId).</summary>
public class Subscription
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public PlanCode Plan { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Đã nhắc sắp hết hạn cho mốc EndAt này (đổi EndAt thì nhắc lại).</summary>
    public DateTime? RemindedEndAt { get; set; }
    public DateTime? ExpiredNoticeEndAt { get; set; }
}

public enum PlanPaymentStatus { Pending, Paid, Cancelled, Expired }

public class PlanPayment
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public PlanCode Plan { get; set; }
    public int Months { get; set; }
    public long AmountVnd { get; set; }
    /// <summary>Mã đơn số nguyên duy nhất theo yêu cầu của PayOS.</summary>
    public long OrderCode { get; set; }
    public string Gateway { get; set; } = default!;
    public string? PaymentLinkId { get; set; }
    public string? CheckoutUrl { get; set; }
    [BsonRepresentation(BsonType.String)] public PlanPaymentStatus Status { get; set; } = PlanPaymentStatus.Pending;
    public string? GatewayRef { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? PaidAt { get; set; }
    /// <summary>Kết quả áp dụng vào gói (để đối soát, hiển thị lịch sử).</summary>
    [BsonRepresentation(BsonType.String)] public PlanCode? AppliedPlan { get; set; }
    public DateTime? AppliedEndAt { get; set; }
}

public record CheckoutRequest(PlanCode Plan, int Months);
