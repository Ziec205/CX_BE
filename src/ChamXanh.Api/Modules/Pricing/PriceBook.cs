using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Pricing;

// Tài liệu 08 — một document = một phiên bản bảng giá. Tiền luôn là số nguyên (Xu hoặc VNĐ).
public enum PriceBookStatus { Draft, PendingApproval, Scheduled, Active, Expired, Rejected }

public static class ListingServiceCodes
{
    public const string Bump = "BUMP";
    public const string AutoBump = "AUTO_BUMP";
    public const string Priority = "PRIORITY";
    public const string Label = "LABEL";
}

public class PriceBook
{
    [BsonId] public string Id { get; set; } = default!;
    public int Version { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public PriceBookStatus Status { get; set; } = PriceBookStatus.Draft;
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public List<PriceGroup> PriceGroups { get; set; } = [];
    public List<ListingServicePrice> ListingServices { get; set; } = [];
    public List<Multiplier> Multipliers { get; set; } = [];
    public PrioritySlots PrioritySlots { get; set; } = new();
    public List<GardenPlan> GardenPlans { get; set; } = [];
    public GardenPlanRules GardenPlanRules { get; set; } = new();
    public List<TopUpPackage> TopUpPackages { get; set; } = [];
    public EscrowFee EscrowFee { get; set; } = new();

    public string CreatedBy { get; set; } = default!;
    public string? ApprovedBy { get; set; }
    public string? ChangeNote { get; set; }
    public string? RejectReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

public class PriceGroup
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public List<string> CategoryIds { get; set; } = [];
}

public class ListingServicePrice
{
    public string Code { get; set; } = default!;
    public bool Enabled { get; set; } = true;
    /// <summary>Số ngày (Ưu tiên, Nhãn) hoặc số ngày chạy (Đẩy tự động). Null với Đẩy lẻ.</summary>
    public int? Days { get; set; }
    public int? PerDay { get; set; }
    public string? LabelName { get; set; }
    /// <summary>Giá theo mã nhóm giá, đơn vị Xu.</summary>
    public Dictionary<string, long> Prices { get; set; } = [];
}

public class Multiplier
{
    public string Name { get; set; } = default!;
    public List<string> PriceGroups { get; set; } = [];
    public List<string> CategoryIds { get; set; } = [];
    public List<string> CollectionIds { get; set; } = [];
    public List<string> Services { get; set; } = [];
    public decimal Factor { get; set; } = 1m;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

public class PrioritySlots
{
    public int Default { get; set; } = 30;
    public List<PrioritySlotOverride> Overrides { get; set; } = [];
}

public class PrioritySlotOverride
{
    public string PriceGroup { get; set; } = default!;
    public string ProvinceId { get; set; } = default!;
    public int Slots { get; set; }
}

public class GardenPlan
{
    public int Months { get; set; }
    public long PriceVnd { get; set; }
    public bool Enabled { get; set; } = true;
}

public class GardenPlanRules
{
    public int FounderDiscountPct { get; set; } = 30;
    public int GraceDays { get; set; } = 7;
    public bool AutoRenewAllowed { get; set; } = true;
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

public class EscrowFee
{
    public decimal Pct { get; set; } = 4m;
    public long MinVnd { get; set; } = 5_000;
    public long MaxVnd { get; set; } = 2_000_000;
    public string Payer { get; set; } = "SELLER";
    public long OrderMinVnd { get; set; } = 50_000;
    public long OrderMaxVnd { get; set; } = 500_000_000;
}
