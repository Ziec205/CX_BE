using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Escrow;

// Giao dịch đảm bảo — tài liệu 04 §2. Tiền do đối tác trung gian thanh toán giữ, sàn không tự giữ tiền (L4).

public enum OrderStatus
{
    AwaitingPayment, AwaitingSellerConfirm, Paid, Shipping, Delivered, Disputed, AwaitingReturn,
    Completed, PartiallyRefunded, Refunded, Settled, Cancelled, CancelledRefunded,
}

public enum DeliveryMethod { SellerDelivery, BuyerPickup, SelfArrangedCarrier, PlatformCarrier }
public enum OrderSource { Listing, Offer, Quote }
public enum DisputeReason { NotReceived, DeadOrWilted, DamagedInTransit, WrongSpecies, WrongSize, MissingQuantity, Other }
public enum SellerDisputeResponse { AcceptFullRefund, OfferPartialRefund, OfferReplacement, Reject }
public enum DisputeOutcome { RejectClaim, PartialRefund, FullRefund, FullRefundWithReturn }

public class ShipmentInfo
{
    public string? Carrier { get; set; }
    public string? TrackingCode { get; set; }
    public List<string> MediaIds { get; set; } = [];
    public DateTime? ExpectedArrival { get; set; }
}

public class Dispute
{
    [BsonRepresentation(BsonType.String)] public DisputeReason Reason { get; set; }
    public string Description { get; set; } = default!;
    public List<string> MediaIds { get; set; } = [];
    public bool HasUnboxingVideo { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime SellerDueAt { get; set; }
    [BsonRepresentation(BsonType.String)] public SellerDisputeResponse? SellerResponse { get; set; }
    public long? SellerOfferAmount { get; set; }
    public string? SellerNote { get; set; }
    public List<string> SellerMediaIds { get; set; } = [];
    public DateTime? SellerRespondedAt { get; set; }
    /// <summary>Người mua đồng ý đề xuất của người bán (hòa giải).</summary>
    public bool? BuyerAccepted { get; set; }
    [BsonRepresentation(BsonType.String)] public DisputeOutcome? Outcome { get; set; }
    public long? ResolvedRefund { get; set; }
    public string? ResolutionNote { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public bool SellerLost { get; set; }
    public ShipmentInfo? Return { get; set; }
    public DateTime? ReturnShippedAt { get; set; }
}

public class OrderEvent
{
    public string Status { get; set; } = default!;
    public string ActorId { get; set; } = default!;
    public string? Note { get; set; }
    public DateTime At { get; set; }
}

public class EscrowOrder
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string Code { get; set; } = default!;
    public string ListingId { get; set; } = default!;
    public string ListingTitle { get; set; } = default!;
    public string? ThumbMediaId { get; set; }
    public string BuyerId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public OrderSource Source { get; set; }
    public string? OfferMessageId { get; set; }
    public string CreatedBy { get; set; } = default!;

    public int Quantity { get; set; }
    public long UnitPrice { get; set; }
    public long ItemAmount { get; set; }
    public long ShippingFee { get; set; }
    public long Total { get; set; }
    /// <summary>Phí dịch vụ tạm tính theo bảng giá lúc tạo đơn; tính lại khi hoàn một phần (BR-DSP-06).</summary>
    public long Fee { get; set; }
    public string FeePayer { get; set; } = "SELLER";
    public int PriceBookVersion { get; set; }

    [BsonRepresentation(BsonType.String)] public DeliveryMethod Delivery { get; set; }
    public int ShipWithinDays { get; set; } = 3;
    public DateTime? PickupDate { get; set; }
    public string? DeliveryAddress { get; set; }
    /// <summary>Nội dung QR khi nhận tại vườn. Không trả trong JSON; người mua lấy qua /pickup-code.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string PickupCode { get; set; } = default!;

    [BsonRepresentation(BsonType.String)] public OrderStatus Status { get; set; }
    public DateTime? PaymentDueAt { get; set; }
    public DateTime? SellerConfirmDueAt { get; set; }
    public DateTime? ShipDueAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? GatewayRef { get; set; }
    public ShipmentInfo? Shipment { get; set; }
    public DateTime? ShippedAt { get; set; }
    public List<string> DeliveryProofMediaIds { get; set; } = [];
    public DateTime? DeliveredAt { get; set; }
    public DateTime? InspectionDueAt { get; set; }
    public int InspectionRemindersSent { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Dispute? Dispute { get; set; }

    public long RefundAmount { get; set; }
    public string? RefundRef { get; set; }
    public string? CancelReason { get; set; }
    public bool CancelledBySeller { get; set; }
    public bool BuyerNoShow { get; set; }

    /// <summary>Tạm dừng quyết toán khi tin bị gỡ vì lừa đảo / người bán bị khóa (BR-ESC-20).</summary>
    public bool SettlementHold { get; set; }
    public DateTime? PayoutEligibleAt { get; set; }
    public long? PayoutAmount { get; set; }
    public string? PayoutApprovedBy { get; set; }
    public string? PayoutRef { get; set; }
    public DateTime? SettledAt { get; set; }
    public bool Reviewed { get; set; }

    public List<OrderEvent> History { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }
}

public record CreateOrderRequest(string ListingId, int? Quantity, DeliveryMethod Delivery, long? ShippingFee, string? DeliveryAddress,
    DateTime? PickupDate, int? ShipWithinDays);
public record CreateFromOfferRequest(string OfferMessageId, int? Quantity, DeliveryMethod Delivery, long? ShippingFee, string? DeliveryAddress,
    DateTime? PickupDate, int? ShipWithinDays);
public record ShipRequest(string? Carrier, string? TrackingCode, List<string>? MediaIds, DateTime? ExpectedArrival);
public record DeliverRequest(List<string>? MediaIds);
public record PickupRequest(string Code);
public record CancelRequest(string? Reason);
public record OpenDisputeRequest(DisputeReason Reason, string Description, List<string>? MediaIds, bool HasUnboxingVideo);
public record DisputeResponseRequest(SellerDisputeResponse Response, long? Amount, string? Note, List<string>? MediaIds);
public record ResolveDisputeRequest(DisputeOutcome Outcome, long? RefundAmount, string Note);
public record ReturnShipRequest(string Carrier, string TrackingCode, List<string>? MediaIds);
public record OrderReviewRequest(int Stars, List<string>? Tags, string? Text);
public record EscrowWebhook(string OrderId, string Status, long AmountVnd, string GatewayRef);
