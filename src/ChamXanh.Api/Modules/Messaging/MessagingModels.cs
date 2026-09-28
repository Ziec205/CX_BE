using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Messaging;

public enum MessageType { Text, Image, Location, Offer, System }
public enum OfferStatus { Pending, Accepted, Rejected, Countered }

public class ListingSnapshot
{
    public string Title { get; set; } = default!;
    public string? ThumbUrl { get; set; }
    public long? Price { get; set; }
    public string Type { get; set; } = default!;
}

public class LastMessage
{
    public string SenderId { get; set; } = default!;
    public string Preview { get; set; } = default!;
    public DateTime At { get; set; }
}

/// <summary>Hội thoại = (người mua, người bán, tin). Vai trò theo cung–cầu: với tin Cần mua, chủ tin là người mua (BR-REV-08).</summary>
public class Conversation
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ListingId { get; set; } = default!;
    public string BuyerId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    public string InitiatorId { get; set; } = default!;
    public ListingSnapshot Listing { get; set; } = default!;
    public LastMessage? Last { get; set; }
    public Dictionary<string, int> Unread { get; set; } = [];
    public List<string> BlockedBy { get; set; } = [];
    public List<string> ArchivedBy { get; set; } = [];
    /// <summary>Lần đầu bên kia trả lời — dùng tính tỉ lệ và thời gian phản hồi (BR-CHAT-05).</summary>
    public DateTime? FirstReplyAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public string Other(string userId) => userId == BuyerId ? SellerId : BuyerId;
    public bool Has(string userId) => userId == BuyerId || userId == SellerId;
}

public class Offer
{
    public long Amount { get; set; }
    [BsonRepresentation(BsonType.String)] public OfferStatus Status { get; set; } = OfferStatus.Pending;
    public DateTime? RespondedAt { get; set; }
}

public class ChatMessage
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ConversationId { get; set; } = default!;
    public string SenderId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public MessageType Type { get; set; }
    public string? Text { get; set; }
    public List<string> MediaIds { get; set; } = [];
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public Offer? Offer { get; set; }
    /// <summary>Cảnh báo hiển thị cho NGƯỜI NHẬN (không chặn tin nhắn) — BR-CHAT-01.</summary>
    public string? Warning { get; set; }
    public DateTime CreatedAt { get; set; }
}
