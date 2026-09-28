using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Messaging;

public record SendMessageRequest(MessageType Type, string? Text, List<string>? MediaIds, double? Lat, double? Lng, long? OfferAmount);
public record RespondOfferRequest(bool Accept, long? CounterAmount);

public class ChatService(IMongoDatabase db, TimeProvider clock, UserService users, ListingService listings, MediaService media,
    IHubContext<ChatHub> hub)
{
    public IMongoCollection<Conversation> Conversations { get; } = db.GetCollection<Conversation>("conversations");
    public IMongoCollection<ChatMessage> Messages { get; } = db.GetCollection<ChatMessage>("messages");
    const int NewAccountMaxConversationsPerDay = 20; // BR-CHAT-02

    public async Task EnsureIndexesAsync()
    {
        var k = Builders<Conversation>.IndexKeys;
        await Conversations.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Conversation>(k.Ascending(c => c.ListingId).Ascending(c => c.BuyerId).Ascending(c => c.SellerId), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<Conversation>(k.Ascending(c => c.BuyerId).Descending("last.at")),
            new CreateIndexModel<Conversation>(k.Ascending(c => c.SellerId).Descending("last.at")),
        ]);
        await Messages.Indexes.CreateOneAsync(new CreateIndexModel<ChatMessage>(Builders<ChatMessage>.IndexKeys.Ascending(m => m.ConversationId).Descending(m => m.CreatedAt)));
    }

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Conversation> StartAsync(string userId, string listingId, CancellationToken ct)
    {
        var user = await users.RequireActiveAsync(userId, ct);
        var listing = await listings.GetAsync(listingId, ct);
        if (!ListingRules.IsPubliclyVisible(listing.Status)) throw DomainException.NotFound("tin đăng");
        if (listing.SellerId == userId) throw new DomainException("OWN_LISTING", "Không thể nhắn tin cho tin của chính mình");

        var (buyerId, sellerId) = listing.Type == ListingType.Buy ? (listing.SellerId, userId) : (userId, listing.SellerId);
        var existing = await Conversations.Find(c => c.ListingId == listingId && c.BuyerId == buyerId && c.SellerId == sellerId).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        if (user.CreatedAt > Now.AddHours(-24))
        {
            var today = await Conversations.CountDocumentsAsync(c => c.InitiatorId == userId && c.CreatedAt > Now.AddDays(-1), cancellationToken: ct);
            if (today >= NewAccountMaxConversationsPerDay) throw DomainException.TooMany("Tài khoản mới chỉ được bắt đầu tối đa 20 cuộc trò chuyện mỗi ngày");
        }
        var conv = new Conversation
        {
            ListingId = listingId, BuyerId = buyerId, SellerId = sellerId, InitiatorId = userId, CreatedAt = Now,
            Listing = new ListingSnapshot
            {
                Title = listing.Title, Price = listing.EffectivePrice, Type = listing.Type.ToString(),
                ThumbUrl = listing.Media.Count > 0 ? $"/media/{listing.Media[0].MediaId}/thumb.webp" : null,
            },
        };
        try { await Conversations.InsertOneAsync(conv, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await Conversations.Find(c => c.ListingId == listingId && c.BuyerId == buyerId && c.SellerId == sellerId).FirstAsync(ct);
        }
        return conv;
    }

    public async Task<Conversation> GetForMemberAsync(string conversationId, string userId, CancellationToken ct)
    {
        var c = ObjectId.TryParse(conversationId, out _) ? await Conversations.Find(x => x.Id == conversationId).FirstOrDefaultAsync(ct) : null;
        if (c is null || !c.Has(userId)) throw DomainException.NotFound("cuộc trò chuyện");
        return c;
    }

    public async Task<ChatMessage> SendAsync(string conversationId, string userId, SendMessageRequest req, CancellationToken ct)
    {
        var conv = await GetForMemberAsync(conversationId, userId, ct);
        var sender = await users.RequireActiveAsync(userId, ct);
        if (conv.BlockedBy.Count > 0) throw DomainException.Forbidden("Cuộc trò chuyện đã bị chặn");

        var msg = new ChatMessage { ConversationId = conv.Id, SenderId = userId, Type = req.Type, CreatedAt = Now };
        switch (req.Type)
        {
            case MessageType.Text:
                var text = req.Text?.Trim() ?? "";
                if (text.Length is 0 or > 2000) throw new DomainException("INVALID_MESSAGE", "Tin nhắn dài 1–2.000 ký tự");
                msg.Text = text;
                break;
            case MessageType.Image:
                var ids = req.MediaIds ?? [];
                if (ids.Count is 0 or > 10) throw new DomainException("INVALID_MESSAGE", "Gửi 1–10 ảnh mỗi lần");
                await media.GetOwnedAsync(ids, userId, ct);
                msg.MediaIds = ids;
                break;
            case MessageType.Location:
                if (req.Lat is not (>= -90 and <= 90) || req.Lng is not (>= -180 and <= 180)) throw new DomainException("INVALID_MESSAGE", "Tọa độ không hợp lệ");
                (msg.Lat, msg.Lng) = (req.Lat, req.Lng);
                break;
            case MessageType.Offer:
                // Thẻ "Đề nghị giá" chỉ người mua gửi; người bán trả giá bằng phản hồi (counter).
                if (userId != conv.BuyerId) throw new DomainException("INVALID_OFFER", "Chỉ người mua gửi đề nghị giá");
                if (req.OfferAmount is not > 0) throw new DomainException("INVALID_OFFER", "Giá đề nghị phải > 0");
                msg.Offer = new Offer { Amount = req.OfferAmount.Value };
                break;
            default:
                throw new DomainException("INVALID_MESSAGE", "Loại tin nhắn không hỗ trợ");
        }

        var listing = await listings.Listings.Find(l => l.Id == conv.ListingId).FirstOrDefaultAsync(ct);
        var isFirstFromSender = !await Messages.Find(m => m.ConversationId == conv.Id && m.SenderId == userId).AnyAsync(ct);
        msg.Warning = ScamDetector.Check(msg.Text, listing?.EscrowEnabled == true, sender.CreatedAt > Now.AddDays(-7), isFirstFromSender);

        await Messages.InsertOneAsync(msg, cancellationToken: ct);
        await TouchAsync(conv, msg, ct);
        return msg;
    }

    public async Task<ChatMessage> RespondOfferAsync(string conversationId, string messageId, string userId, RespondOfferRequest req, CancellationToken ct)
    {
        var conv = await GetForMemberAsync(conversationId, userId, ct);
        if (userId != conv.SellerId) throw DomainException.Forbidden("Chỉ người bán phản hồi đề nghị giá");
        var offer = await Messages.Find(m => m.Id == messageId && m.ConversationId == conv.Id && m.Type == MessageType.Offer).FirstOrDefaultAsync(ct)
            ?? throw DomainException.NotFound("đề nghị giá");
        var status = req.Accept ? OfferStatus.Accepted : req.CounterAmount is > 0 ? OfferStatus.Countered : OfferStatus.Rejected;
        var res = await Messages.UpdateOneAsync(m => m.Id == messageId && m.Offer!.Status == OfferStatus.Pending,
            Builders<ChatMessage>.Update.Set(m => m.Offer!.Status, status).Set(m => m.Offer!.RespondedAt, Now), cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("OFFER_CLOSED", "Đề nghị giá đã được phản hồi");

        var text = status switch
        {
            OfferStatus.Accepted => $"Người bán đã đồng ý giá {offer.Offer!.Amount:N0}đ",
            OfferStatus.Countered => $"Người bán trả giá {req.CounterAmount:N0}đ",
            _ => "Người bán đã từ chối đề nghị giá",
        };
        var sys = new ChatMessage { ConversationId = conv.Id, SenderId = userId, Type = MessageType.System, Text = text, CreatedAt = Now };
        await Messages.InsertOneAsync(sys, cancellationToken: ct);
        await TouchAsync(conv, sys, ct);
        // Đẩy lại tin đề nghị giá đã đổi trạng thái để mọi thiết bị của 2 bên cập nhật (client thay theo id).
        var updated = await Messages.Find(m => m.Id == messageId).FirstAsync(ct);
        await hub.Clients.Groups(ChatHub.UserGroup(conv.BuyerId), ChatHub.UserGroup(conv.SellerId)).SendAsync("message", updated, ct);
        return updated;
    }

    async Task TouchAsync(Conversation conv, ChatMessage msg, CancellationToken ct)
    {
        var other = conv.Other(msg.SenderId);
        var preview = msg.Type switch
        {
            MessageType.Image => "[Ảnh]", MessageType.Location => "[Vị trí]",
            MessageType.Offer => $"[Đề nghị giá {msg.Offer!.Amount:N0}đ]", _ => msg.Text!.Length > 80 ? msg.Text[..80] + "…" : msg.Text,
        };
        var u = Builders<Conversation>.Update.Set(c => c.Last, new LastMessage { SenderId = msg.SenderId, Preview = preview!, At = msg.CreatedAt })
            .Inc($"unread.{other}", 1).Pull(c => c.ArchivedBy, other);
        // Phản hồi đề nghị giá (tin hệ thống do người bán bấm) cũng tính là đã trả lời (BR-CHAT-05).
        if (conv.FirstReplyAt is null && msg.SenderId != conv.InitiatorId) u = u.Set(c => c.FirstReplyAt, msg.CreatedAt);
        await Conversations.UpdateOneAsync(c => c.Id == conv.Id, u, cancellationToken: ct);
        await hub.Clients.Group(ChatHub.UserGroup(other)).SendAsync("message", msg, ct);
        await hub.Clients.Group(ChatHub.UserGroup(msg.SenderId)).SendAsync("message", msg, ct);
    }

    public async Task MarkReadAsync(string conversationId, string userId, CancellationToken ct)
    {
        await GetForMemberAsync(conversationId, userId, ct);
        await Conversations.UpdateOneAsync(c => c.Id == conversationId, Builders<Conversation>.Update.Set($"unread.{userId}", 0), cancellationToken: ct);
    }

    public async Task BlockAsync(string conversationId, string userId, bool block, CancellationToken ct)
    {
        await GetForMemberAsync(conversationId, userId, ct);
        var u = block ? Builders<Conversation>.Update.AddToSet(c => c.BlockedBy, userId) : Builders<Conversation>.Update.Pull(c => c.BlockedBy, userId);
        await Conversations.UpdateOneAsync(c => c.Id == conversationId, u, cancellationToken: ct);
    }

    /// <summary>BR-CHAT-05: tỉ lệ phản hồi và thời gian phản hồi trung bình của người nhận tin nhắn đầu, 30 ngày gần nhất.</summary>
    public async Task<object> ResponseStatsAsync(string userId, CancellationToken ct)
    {
        var since = Now.AddDays(-30);
        var received = await Conversations.Find(c => (c.BuyerId == userId || c.SellerId == userId) && c.InitiatorId != userId && c.CreatedAt > since && c.Last != null)
            .Project(c => new { c.CreatedAt, c.FirstReplyAt }).ToListAsync(ct);
        if (received.Count == 0) return new { responseRate = (double?)null, avgResponseMinutes = (double?)null, sample = 0 };
        var replied = received.Where(r => r.FirstReplyAt is not null).ToList();
        return new
        {
            responseRate = Math.Round((double)replied.Count / received.Count, 2),
            avgResponseMinutes = replied.Count == 0 ? (double?)null : Math.Round(replied.Average(r => (r.FirstReplyAt!.Value - r.CreatedAt).TotalMinutes)),
            sample = received.Count,
        };
    }
}
