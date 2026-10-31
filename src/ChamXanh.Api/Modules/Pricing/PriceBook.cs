using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Pricing;

// Một document = một phiên bản bảng giá gói nạp Xu. Phí đăng tin và hạn mức tin theo gói nằm ở PlanCatalog.
public enum PriceBookStatus { Draft, PendingApproval, Scheduled, Active, Expired, Rejected }

public class PriceBook
{
    [BsonId] public string Id { get; set; } = default!;
    public int Version { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public PriceBookStatus Status { get; set; } = PriceBookStatus.Draft;
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public List<TopUpPackage> TopUpPackages { get; set; } = [];

    public string CreatedBy { get; set; } = default!;
    public string? ApprovedBy { get; set; }
    public string? ChangeNote { get; set; }
    public string? RejectReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

public class TopUpPackage
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public long PriceVnd { get; set; }
    public long Xu { get; set; }
    public long BonusXu { get; set; }
    public int BonusExpiryDays { get; set; } = 60;
    public bool Popular { get; set; }
    public bool Enabled { get; set; } = true;
}
