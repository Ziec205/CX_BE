using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver.GeoJsonObjectModel;

namespace ChamXanh.Api.Modules.Listings;

public enum ListingType { Sell, Buy, Rent, Give }

/// <summary>Vòng đời tin — tài liệu 03 §3.</summary>
public enum ListingStatus { Draft, PendingReview, Active, Rejected, Hidden, SoldOut, Expired, TempHidden, Removed, Deleted }

public enum PriceMode { Fixed, Negotiable }

public enum RentUnit { Day, Week, Month, TetSeason }

public class ListingOptions
{
    public int ExpiryDays { get; set; } = 60;                         // A-01
    public int ProSellerThreshold { get; set; } = 20;                 // A-04
    public DateTime? LaunchDate { get; set; }                         // BR-LST-02: tin seed tính hạn từ ngày ra mắt
    public int MaxNewPerHour { get; set; } = 30;                      // BR-LST-01
    public int MaxNewPerHourGarden { get; set; } = 100;
    public int MaxActiveGive { get; set; } = 10;                      // BR-GIV-02
    public long VerificationPhotoThresholdVnd { get; set; } = 20_000_000; // BR-LST-10
    public long ManualReviewPriceVnd { get; set; } = 50_000_000;
    public long HighValuePriceVnd { get; set; } = 5_000_000;          // BR-AUTH-07
    public int NewAccountDays { get; set; } = 7;
    public int NewAccountMaxHighValue { get; set; } = 5;
    public int AutoApproveBelow { get; set; } = 30;                   // 03 §4.1
    public int PriorityQueueFrom { get; set; } = 70;
    public double RandomAuditRate { get; set; } = 0.05;
    public int RealPhotoMaxAgeDays { get; set; } = 30;                // BR-LST-09
    public int DuplicateLookbackDays { get; set; } = 30;              // BR-LST-06
    public int DuplicateMaxHamming { get; set; } = 4;
    public int BuyRequestMaxDays { get; set; } = 30;                  // BR-RFQ-04
}

public class ListingMediaRef
{
    public string MediaId { get; set; } = default!;
    public long PHash { get; set; }
    public bool CapturedInApp { get; set; }
    public DateTime? CapturedAt { get; set; }
}

public class RentTerms
{
    [BsonRepresentation(BsonType.String)] public RentUnit Unit { get; set; }
    public long PricePerUnit { get; set; }
    public long Deposit { get; set; }
    public int MinUnits { get; set; } = 1;
    public List<string> Services { get; set; } = [];
}

/// <summary>Bản sửa chờ duyệt: bản đang hiển thị giữ nguyên cho đến khi duyệt (BR-LST-03).</summary>
public class ListingRevision
{
    public string Title { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string CategoryId { get; set; } = default!;
    public string? SpeciesId { get; set; }
    public List<ListingMediaRef> Media { get; set; } = [];
    public long? Price { get; set; }
    public Dictionary<string, object> Attributes { get; set; } = [];
    public string? ModerationCaseId { get; set; }
    public DateTime SubmittedAt { get; set; }
}

public class Listing
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string SellerId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public ListingType Type { get; set; }
    [BsonRepresentation(BsonType.String)] public ListingStatus Status { get; set; } = ListingStatus.Draft;

    public string CategoryId { get; set; } = default!;
    public string CategoryRootId { get; set; } = default!;
    public string? SpeciesId { get; set; }
    public string? SpeciesFamily { get; set; }
    public List<string> CollectionIds { get; set; } = [];

    public string Title { get; set; } = default!;
    public string Description { get; set; } = default!;
    /// <summary>Tiêu đề + mô tả + tên loài đã bỏ dấu, dùng cho tìm kiếm tiếng Việt không dấu.</summary>
    public string SearchText { get; set; } = "";

    public long? Price { get; set; }
    /// <summary>Giá dùng để lọc/sắp xếp: giá bán, cận dưới giá tham khảo, giá thuê, ngân sách tối đa, hoặc 0 với tin tặng.</summary>
    public long SortPrice { get; set; }
    public string? RevisionRejectReason { get; set; }
    [BsonRepresentation(BsonType.String)] public PriceMode PriceMode { get; set; }
    public bool PriceNegotiable { get; set; }
    public long? PriceRefMin { get; set; }
    public long? PriceRefMax { get; set; }
    public long? BudgetMin { get; set; }
    public long? BudgetMax { get; set; }
    public DateTime? NeededBy { get; set; }
    public RentTerms? Rent { get; set; }
    public string? WantInExchange { get; set; }

    public int Quantity { get; set; } = 1;
    public int Reserved { get; set; }
    public int Sold { get; set; }
    public string Unit { get; set; } = "cây";
    public Dictionary<string, object> Attributes { get; set; } = [];

    /// <summary>Công dụng do người bán chọn từ PlantUses.All, dùng cho bộ lọc Chợ cây.</summary>
    public List<string> Uses { get; set; } = [];

    public string ProvinceId { get; set; } = default!;
    public string? WardId { get; set; }
    public GeoJsonPoint<GeoJson2DGeographicCoordinates>? Location { get; set; }
    public List<string> PickupOptions { get; set; } = [];
    public bool EscrowEnabled { get; set; }

    public List<ListingMediaRef> Media { get; set; } = [];
    public string? VerificationMediaId { get; set; }
    public ListingRevision? PendingRevision { get; set; }

    public int RiskScore { get; set; }
    public string? RejectReason { get; set; }
    public bool AppealUsed { get; set; }
    public bool SellerHasActivePlan { get; set; }

    public DateTime? FirstPublishedAt { get; set; }
    /// <summary>Chỉ đổi khi hiển thị lần đầu và khi đẩy tin trả phí (BR-LST-15).</summary>
    public DateTime? BumpedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? PriorityUntil { get; set; }
    public string? HighlightLabel { get; set; }
    public DateTime? HighlightUntil { get; set; }
    public int Views { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int Version { get; set; }

    [BsonIgnore] public int Available => Math.Max(0, Quantity - Reserved - Sold);
    [BsonIgnore] public long? EffectivePrice => PriceMode == PriceMode.Negotiable ? PriceRefMax : Price;
}
