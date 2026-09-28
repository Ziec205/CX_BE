using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver.GeoJsonObjectModel;

namespace ChamXanh.Api.Modules.Gardens;

public enum GardenType { Garden, Shop }
public enum VerificationStatus { Submitted, NeedsInfo, Verified, Rejected, Revoked }
public enum VerificationMethod { BusinessLicense, VideoCall, FieldVisit }

/// <summary>Dữ liệu CCCD chủ vườn — lưu mã hóa, số CCCD chỉ giữ HMAC để kiểm tra trùng (BR-AUTH-02, BR-AUTH-04).</summary>
public class KycData
{
    public string IdNumberHmac { get; set; } = default!;
    public string IdNumberEnc { get; set; } = default!;
    public string FullNameEnc { get; set; } = default!;
    public string FullNameNormalized { get; set; } = default!;
    public string DobEnc { get; set; } = default!;
    public List<string> DocumentMediaIds { get; set; } = []; // kho "secure"
    public string Provider { get; set; } = "manual";
    /// <summary>Kết quả eKYC tự động (OCR + so khớp khuôn mặt) nếu có nhà cung cấp; người duyệt vẫn ra quyết định cuối.</summary>
    public KycAutoCheck? AutoCheck { get; set; }
}

public class KycAutoCheck
{
    public string Provider { get; set; } = default!;
    public bool Passed { get; set; }
    public double? Score { get; set; }
    public string? Reason { get; set; }
    public DateTime CheckedAt { get; set; }
}

public class BankAccount
{
    public string BankCode { get; set; } = default!;
    public string AccountNoEnc { get; set; } = default!;
    public string AccountNoMasked { get; set; } = default!;
    public string AccountName { get; set; } = default!;
}

/// <summary>Gói Nhà vườn — máy trạng thái riêng, tách khỏi xác minh (04 §4.3, sửa L31).</summary>
public class GardenPlan
{
    public int Months { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime GraceEndAt { get; set; }
    public bool AutoRenew { get; set; }
    public long LastPriceXu { get; set; }
    public int PriceBookVersion { get; set; }
}

public class GardenProfile
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string OwnerId { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string Name { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public GardenType Type { get; set; }
    public string? Description { get; set; }
    public string Address { get; set; } = default!;
    public string ProvinceId { get; set; } = default!;
    public string? WardId { get; set; }
    public GeoJsonPoint<GeoJson2DGeographicCoordinates> Location { get; set; } = default!;
    public string? OpeningHours { get; set; }
    public bool AllowVisit { get; set; }
    public List<string> PhotoMediaIds { get; set; } = [];
    public string? CoverMediaId { get; set; }
    public List<string> PinnedListingIds { get; set; } = [];

    [BsonRepresentation(BsonType.String)] public VerificationMethod Method { get; set; }
    public string? BusinessLicenseNo { get; set; }
    public KycData Kyc { get; set; } = default!;
    public BankAccount? Bank { get; set; }
    [BsonRepresentation(BsonType.String)] public VerificationStatus Status { get; set; } = VerificationStatus.Submitted;
    public string? ReviewNote { get; set; }
    public string? ReviewedById { get; set; }
    public DateTime? VerifiedAt { get; set; }

    public GardenPlan? Plan { get; set; }
    public bool IsFounding { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool PlanActive(DateTime now) => Status == VerificationStatus.Verified && Plan is { } p && p.EndAt > now;
    public bool InGrace(DateTime now) => Plan is { } p && p.EndAt <= now && p.GraceEndAt > now;
}
