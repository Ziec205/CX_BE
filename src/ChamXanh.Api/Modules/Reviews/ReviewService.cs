using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Messaging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Reviews;

public enum ReviewTier { Contacted, Purchased }

public class ReviewReply
{
    public string Text { get; set; } = default!;
    public DateTime At { get; set; }
}

public class Review
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ReviewerId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public ReviewTier Tier { get; set; }
    public string? ListingId { get; set; }
    public string? ConversationId { get; set; }
    public string? OrderId { get; set; }
    public int Stars { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Text { get; set; }
    public List<string> MediaIds { get; set; } = [];
    public ReviewReply? Reply { get; set; }
    public int EditCount { get; set; }
    public bool Hidden { get; set; }
    public int ReportCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public record CreateReviewRequest(string SellerId, int Stars, List<string>? Tags, string? Text, List<string>? MediaIds);
public record ReplyRequest(string Text);

public class ReviewService(IMongoDatabase db, TimeProvider clock, ChatService chat, MediaService media, UserService users)
{
    public IMongoCollection<Review> Reviews { get; } = db.GetCollection<Review>("reviews");
    public static readonly string[] AllowedTags = ["Cây đúng mô tả", "Nhiệt tình", "Đóng gói kỹ", "Giá hợp lý", "Đúng hẹn"];
    public const int PurchasedWeight = 3;       // D-14
    public const int MinReviewsToShowScore = 3; // BR-REV-05

    public async Task EnsureIndexesAsync()
    {
        var k = Builders<Review>.IndexKeys;
        await Reviews.Indexes.CreateManyAsync(
        [
            // BR-REV-01: một đánh giá "Đã liên hệ" cho mỗi cặp người mua–người bán; một đánh giá "Đã mua" cho mỗi đơn
            new CreateIndexModel<Review>(k.Ascending(r => r.ReviewerId).Ascending(r => r.SellerId),
                new CreateIndexOptions<Review> { Unique = true, PartialFilterExpression = Builders<Review>.Filter.Eq(r => r.Tier, ReviewTier.Contacted) }),
            new CreateIndexModel<Review>(k.Ascending(r => r.OrderId),
                new CreateIndexOptions<Review> { Unique = true, PartialFilterExpression = Builders<Review>.Filter.Type(r => r.OrderId, BsonType.String) }),
            new CreateIndexModel<Review>(k.Ascending(r => r.SellerId).Descending(r => r.CreatedAt)),
        ]);
    }

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Điều kiện đánh giá tầng "Đã liên hệ": có hội thoại, người bán đã trả lời, hội thoại bắt đầu ≥ 24h và trong 30 ngày (BR-REV-03).</summary>
    async Task<Conversation?> EligibleConversationAsync(string reviewerId, string sellerId, DateTime? after, CancellationToken ct)
    {
        var from = Now.AddDays(-31);
        var to = Now.AddHours(-24);
        var f = Builders<Conversation>.Filter;
        var filter = f.Eq(c => c.BuyerId, reviewerId) & f.Eq(c => c.SellerId, sellerId) & f.Ne(c => c.FirstReplyAt, null)
            & f.Gte(c => c.CreatedAt, from) & f.Lte(c => c.CreatedAt, to) & f.Ne("listing.type", ListingType.Give.ToString());
        if (after is { } a) filter &= f.Gt(c => c.CreatedAt, a);
        var candidates = await chat.Conversations.Find(filter).SortByDescending(c => c.CreatedAt).ToListAsync(ct);
        // Người bán phải thực sự trả lời (không tính tin nhắn hệ thống)
        foreach (var c in candidates)
            if (await chat.Messages.Find(m => m.ConversationId == c.Id && m.SenderId == sellerId && m.Type != MessageType.System).AnyAsync(ct))
                return c;
        return null;
    }

    public async Task<Review> CreateOrUpdateAsync(string reviewerId, CreateReviewRequest req, CancellationToken ct)
    {
        await users.RequireActiveAsync(reviewerId, ct);
        if (req.SellerId == reviewerId) throw new DomainException("CANNOT_REVIEW_SELF", "Không thể tự đánh giá");
        if (req.Stars is < 1 or > 5) throw new DomainException("INVALID_STARS", "Chấm từ 1 đến 5 sao");
        var tags = (req.Tags ?? []).Distinct().ToList();
        if (tags.Any(t => !AllowedTags.Contains(t))) throw new DomainException("INVALID_TAG", $"Tiêu chí hợp lệ: {string.Join(", ", AllowedTags)}");
        var text = req.Text?.Trim();
        if (text?.Length > 1000) throw new DomainException("TEXT_TOO_LONG", "Nhận xét tối đa 1.000 ký tự");
        if (text is not null && ListingRules.ContainsContactInfo(text)) throw new DomainException("CONTACT_IN_TEXT", "Nhận xét không được chứa SĐT hoặc link");
        var mediaIds = req.MediaIds is { Count: > 0 } ? (await media.GetOwnedAsync(req.MediaIds.Take(6), reviewerId, ct)).Select(m => m.Id).ToList() : [];

        var existing = await Reviews.Find(r => r.ReviewerId == reviewerId && r.SellerId == req.SellerId && r.Tier == ReviewTier.Contacted).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            var conv = await EligibleConversationAsync(reviewerId, req.SellerId, null, ct)
                ?? throw DomainException.Forbidden("Bạn chỉ đánh giá được người bán đã trả lời bạn trong chat (sau 24 giờ, trong vòng 30 ngày)");
            var review = new Review
            {
                ReviewerId = reviewerId, SellerId = req.SellerId, Tier = ReviewTier.Contacted, ListingId = conv.ListingId, ConversationId = conv.Id,
                Stars = req.Stars, Tags = tags, Text = text, MediaIds = mediaIds, CreatedAt = Now, UpdatedAt = Now,
            };
            await Reviews.InsertOneAsync(review, cancellationToken: ct);
            return review;
        }

        // BR-REV-01/03: sửa 1 lần trong 7 ngày; hoặc cập nhật khi có hội thoại mới về tin khác.
        var newConv = await EligibleConversationAsync(reviewerId, req.SellerId, existing.UpdatedAt, ct);
        var canEdit = existing.EditCount == 0 && existing.CreatedAt > Now.AddDays(-7);
        if (newConv is null && !canEdit) throw DomainException.Conflict("REVIEW_LOCKED", "Bạn đã đánh giá người bán này và hết lượt sửa");
        var u = Builders<Review>.Update.Set(r => r.Stars, req.Stars).Set(r => r.Tags, tags).Set(r => r.Text, text).Set(r => r.MediaIds, mediaIds).Set(r => r.UpdatedAt, Now);
        u = newConv is not null
            ? u.Set(r => r.ConversationId, newConv.Id).Set(r => r.ListingId, newConv.ListingId).Set(r => r.EditCount, 0).Set(r => r.Reply, null)
            : u.Inc(r => r.EditCount, 1);
        await Reviews.UpdateOneAsync(r => r.Id == existing.Id, u, cancellationToken: ct);
        return await Reviews.Find(r => r.Id == existing.Id).FirstAsync(ct);
    }

    /// <summary>BR-REV-04: người bán trả lời công khai 1 lần.</summary>
    public async Task<Review> ReplyAsync(string reviewId, string sellerId, string text, CancellationToken ct)
    {
        text = text?.Trim() ?? "";
        if (text.Length is 0 or > 1000) throw new DomainException("INVALID_REPLY", "Trả lời dài 1–1.000 ký tự");
        var res = await Reviews.UpdateOneAsync(r => r.Id == reviewId && r.SellerId == sellerId && r.Reply == null,
            Builders<Review>.Update.Set(r => r.Reply, new ReviewReply { Text = text, At = Now }), cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("REPLY_NOT_ALLOWED", "Không tìm thấy đánh giá của bạn hoặc đã trả lời rồi");
        return await Reviews.Find(r => r.Id == reviewId).FirstAsync(ct);
    }

    /// <summary>Điểm hiển thị = trung bình có trọng số (Đã mua ×3), chỉ khi có ≥ 3 đánh giá (BR-REV-05).</summary>
    public static (double? Score, int Count, int Purchased) Score(IReadOnlyCollection<Review> reviews)
    {
        var visible = reviews.Where(r => !r.Hidden).ToList();
        if (visible.Count < MinReviewsToShowScore) return (null, visible.Count, visible.Count(r => r.Tier == ReviewTier.Purchased));
        double weight(Review r) => r.Tier == ReviewTier.Purchased ? PurchasedWeight : 1;
        var score = visible.Sum(r => r.Stars * weight(r)) / visible.Sum(weight);
        return (Math.Round(score, 1), visible.Count, visible.Count(r => r.Tier == ReviewTier.Purchased));
    }
}

public static class ReviewEndpoints
{
    public static void MapReviews(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/users/{id}/reviews", async (string id, ReviewService svc, CancellationToken ct) =>
        {
            var all = await svc.Reviews.Find(r => r.SellerId == id && !r.Hidden).SortByDescending(r => r.CreatedAt).Limit(500).ToListAsync(ct);
            var (score, count, purchased) = ReviewService.Score(all);
            return new
            {
                score, count, purchased,
                items = all.Take(50).Select(r => new { r.Id, r.ReviewerId, Tier = r.Tier.ToString(), r.Stars, r.Tags, r.Text, r.MediaIds, r.Reply, r.CreatedAt }),
            };
        }).WithTags("Reviews");

        var g = app.MapGroup("/api/reviews").WithTags("Reviews").RequireAuthorization(Policies.Member);
        g.MapPost("/", (CreateReviewRequest req, ClaimsPrincipal p, ReviewService svc, CancellationToken ct) => svc.CreateOrUpdateAsync(p.UserId(), req, ct));
        g.MapPost("/{id}/reply", (string id, ReplyRequest req, ClaimsPrincipal p, ReviewService svc, CancellationToken ct) => svc.ReplyAsync(id, p.UserId(), req.Text, ct));
        g.MapPost("/{id}/report", async (string id, ClaimsPrincipal p, ReviewService svc, CancellationToken ct) =>
        {
            await svc.Reviews.UpdateOneAsync(r => r.Id == id, Builders<Review>.Update.Inc(r => r.ReportCount, 1), cancellationToken: ct);
            return Results.Ok(new { reported = true });
        });

        app.MapPost("/api/admin/reviews/{id}/hide", async (string id, bool hidden, ClaimsPrincipal p, ReviewService svc, AuditService audit, CancellationToken ct) =>
        {
            await svc.Reviews.UpdateOneAsync(r => r.Id == id, Builders<Review>.Update.Set(r => r.Hidden, hidden), cancellationToken: ct);
            await audit.LogAsync(p, hidden ? "review.hide" : "review.unhide", "review", id, ct: ct);
            return Results.NoContent();
        }).WithTags("Admin Reviews").RequireAuthorization(Policies.ForPerm(Perm.ListingModerate));
    }
}
