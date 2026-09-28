using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Wallet;

public enum LotKind { Paid, Bonus }
public enum LedgerType { TopUp, Bonus, Spend, Refund, Adjust, Expire }
public enum TopUpStatus { Pending, Paid, Failed, Expired }

/// <summary>Lô Xu. Xu thưởng tiêu trước, lô sắp hết hạn tiêu trước (FEFO) — tài liệu 04 §3.2.</summary>
public class XuLot
{
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.String)] public LotKind Kind { get; set; }
    public long Amount { get; set; }
    public long Remaining { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class XuWallet
{
    [BsonId] public string UserId { get; set; } = default!;
    public List<XuLot> Lots { get; set; } = [];
    public bool Frozen { get; set; }
    public int Version { get; set; }

    public long Balance(DateTime now) => Lots.Where(l => l.ExpiresAt is null || l.ExpiresAt > now).Sum(l => l.Remaining);
}

public class LotAllocation
{
    public string LotId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public LotKind Kind { get; set; }
    public long Amount { get; set; }
}

/// <summary>Sổ cái bất biến: chỉ ghi thêm, sửa sai bằng bút toán mới (BR-XU-01).</summary>
public class LedgerEntry
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public long Amount { get; set; }
    [BsonRepresentation(BsonType.String)] public LedgerType Type { get; set; }
    public string? RefType { get; set; }
    public string? RefId { get; set; }
    public string IdempotencyKey { get; set; } = default!;
    public List<LotAllocation> Allocations { get; set; } = [];
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TopUpSnapshot
{
    public int PriceBookVersion { get; set; }
    public string PackageCode { get; set; } = default!;
    public long PriceVnd { get; set; }
    public long Xu { get; set; }
    public long BonusXu { get; set; }
    public int BonusExpiryDays { get; set; }
}

public class TopUp
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public TopUpSnapshot Snapshot { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public TopUpStatus Status { get; set; } = TopUpStatus.Pending;
    public string Gateway { get; set; } = default!;
    public string? GatewayRef { get; set; }
    public string? InvoiceInfo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? PaidAt { get; set; }
}

public class AdjustmentRequest
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public long Amount { get; set; }
    [BsonRepresentation(BsonType.String)] public LotKind Kind { get; set; }
    public string Reason { get; set; } = default!;
    public string RequestedById { get; set; } = default!;
    public string? ApprovedById { get; set; }
    public string Status { get; set; } = "Pending"; // Pending | Applied | Rejected
    public DateTime CreatedAt { get; set; }
}
