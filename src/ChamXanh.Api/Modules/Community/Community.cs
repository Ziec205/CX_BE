using System.Security.Claims;
using System.Text.RegularExpressions;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Notifications;
using ChamXanh.Api.Modules.Platform;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Community;

// Cộng đồng — tài liệu 05 §3.

public enum PostType { Question, Showcase, Guide }
public enum PostStatus { Visible, Hidden, Removed }

public class Post
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string AuthorId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public PostType Type { get; set; }
    public string Title { get; set; } = default!;
    public string Body { get; set; } = default!;
    public List<string> MediaIds { get; set; } = [];
    public List<string> SpeciesIds { get; set; } = [];
    public List<string> Topics { get; set; } = [];
    public string SearchText { get; set; } = "";
    [BsonRepresentation(BsonType.String)] public PostStatus Status { get; set; }
    /// <summary>BR-COM-01: nghi rao bán (có giá/SĐT/"inbox giá") — gợi ý chuyển thành tin đăng.</summary>
    public bool SaleFlag { get; set; }
    public string? BestCommentId { get; set; }
    public int Likes { get; set; }
    public int Comments { get; set; }
    public int ReportCount { get; set; }
    public List<string> ReportedBy { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
}

public class Comment
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string PostId { get; set; } = default!;
    public string AuthorId { get; set; } = default!;
    public string Body { get; set; } = default!;
    public List<string> MediaIds { get; set; } = [];
    public int Likes { get; set; }
    public bool IsBest { get; set; }
    [BsonRepresentation(BsonType.String)] public PostStatus Status { get; set; }
    public int ReportCount { get; set; }
    public List<string> ReportedBy { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public class PostLike
{
    [BsonId] public string Id { get; set; } = default!; // {userId}:{targetId}
    public string UserId { get; set; } = default!;
    public string TargetId { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}

public class CommunityProfile
{
    [BsonId] public string UserId { get; set; } = default!;
    public int Points { get; set; }
    public string? LikePointsDay { get; set; } // yyyy-MM-dd theo giờ VN
    public int LikePointsToday { get; set; }
    /// <summary>BR-COM-04: huy hiệu Chuyên gia do Chạm Xanh cấp.</summary>
    public bool Expert { get; set; }
    public string? ExpertTitle { get; set; }
}

public record PostRequest(PostType Type, string Title, string Body, List<string>? MediaIds, List<string>? SpeciesIds, List<string>? Topics);
public record CommentRequest(string Body, List<string>? MediaIds);
public record ReportRequest(string Reason);
public record ExpertRequest(bool Expert, string? Title);
public record ModeratePostRequest(PostStatus Status, string? Reason);

public static partial class CommunityRules
{
    [GeneratedRegex(@"(\d[\d\.,]*\s*(k|tr|triệu|trieu|nghìn|nghin|đ|vnd|vnđ)\b)|inbox\s*giá|ib\s*giá|cần\s*bán|can\s*ban|giá\s*\d|thanh\s*lý", RegexOptions.IgnoreCase)]
    private static partial Regex SaleLike();
    [GeneratedRegex(@"https?://|www\.|\.(com|vn|net)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    public static bool LooksLikeSale(string text) => SaleLike().IsMatch(text) || ListingRules.ContainsContactInfo(text);
    public static bool HasLink(string text) => Link().IsMatch(text);

    /// <summary>BR-COM-03: Mầm → Chồi → Cây → Cổ thụ.</summary>
    public static string Badge(int points) => points switch { >= 1000 => "Cổ thụ", >= 300 => "Cây", >= 50 => "Chồi", _ => "Mầm" };
}

public class CommunityService(IMongoDatabase db, TimeProvider clock, UserService users, MediaService media, NotificationService notifications,
    Gardens.GardenService gardens)
{
    public IMongoCollection<Post> Posts { get; } = db.GetCollection<Post>("communityPosts");
    public IMongoCollection<Comment> Comments { get; } = db.GetCollection<Comment>("communityComments");
    public IMongoCollection<PostLike> Likes { get; } = db.GetCollection<PostLike>("communityLikes");
    public IMongoCollection<CommunityProfile> Profiles { get; } = db.GetCollection<CommunityProfile>("communityProfiles");
    DateTime Now => clock.GetUtcNow().UtcDateTime;
    const int HideAtReports = 3;

    public async Task EnsureIndexesAsync()
    {
        await Posts.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Post>(Builders<Post>.IndexKeys.Ascending(p => p.Status).Descending(p => p.LastActivityAt)),
            new CreateIndexModel<Post>(Builders<Post>.IndexKeys.Ascending(p => p.AuthorId).Descending(p => p.CreatedAt)),
            new CreateIndexModel<Post>(Builders<Post>.IndexKeys.Ascending(p => p.SpeciesIds)),
            new CreateIndexModel<Post>(Builders<Post>.IndexKeys.Ascending(p => p.Topics)),
        ]);
        await Comments.Indexes.CreateOneAsync(new CreateIndexModel<Comment>(Builders<Comment>.IndexKeys.Ascending(c => c.PostId).Ascending(c => c.CreatedAt)));
    }

    async Task<User> RequireAuthorAsync(string userId, string text, CancellationToken ct)
    {
        var u = await users.RequireActiveAsync(userId, ct);
        if (HasLinkAndNew(u, text)) throw new DomainException("LINK_NOT_ALLOWED", "Tài khoản mới dưới 24 giờ chưa được đăng link");
        return u;
    }

    bool HasLinkAndNew(User u, string text) => CommunityRules.HasLink(text) && u.CreatedAt > Now.AddHours(-24); // BR-COM-06

    async Task<List<string>> OwnedMediaAsync(List<string>? ids, string userId, int max, string target, CancellationToken ct)
    {
        var list = (ids ?? []).Take(max).ToList();
        await media.GetOwnedAsync(list, userId, ct);
        await media.AttachAsync(list, target, ct);
        return list;
    }

    public async Task<Post> CreateAsync(string userId, PostRequest req, CancellationToken ct)
    {
        var (title, body) = Validate(req);
        await RequireAuthorAsync(userId, title + " " + body, ct);
        if (await Posts.CountDocumentsAsync(p => p.AuthorId == userId && p.CreatedAt > Now.AddHours(-1), cancellationToken: ct) >= 10)
            throw DomainException.TooMany("Bạn đăng bài quá nhanh, thử lại sau");
        var p = new Post { AuthorId = userId, CreatedAt = Now };
        Apply(p, req, title, body);
        if (req.Type == PostType.Showcase && (req.MediaIds?.Count ?? 0) == 0) throw new DomainException("PHOTO_REQUIRED", "Bài khoe cây cần ít nhất 1 ảnh");
        p.MediaIds = await OwnedMediaAsync(req.MediaIds, userId, 10, $"post:{p.Id}", ct);
        await Posts.InsertOneAsync(p, cancellationToken: ct);
        return p;
    }

    public async Task<Post> UpdateAsync(string userId, string id, PostRequest req, CancellationToken ct)
    {
        var p = await GetAsync(id, ct);
        if (p.AuthorId != userId) throw DomainException.Forbidden("Chỉ tác giả sửa được bài");
        var (title, body) = Validate(req);
        await RequireAuthorAsync(userId, title + " " + body, ct);
        Apply(p, req, title, body);
        p.MediaIds = await OwnedMediaAsync(req.MediaIds, userId, 10, $"post:{p.Id}", ct);
        await Posts.ReplaceOneAsync(x => x.Id == id, p, cancellationToken: ct);
        return p;
    }

    static (string, string) Validate(PostRequest req)
    {
        var title = req.Title?.Trim() ?? "";
        var body = req.Body?.Trim() ?? "";
        if (title.Length is < 5 or > 150) throw new DomainException("INVALID_TITLE", "Tiêu đề dài 5–150 ký tự");
        if (body.Length < 10 || body.Length > (req.Type == PostType.Guide ? 30_000 : 5_000)) throw new DomainException("INVALID_BODY", "Nội dung quá ngắn hoặc quá dài");
        return (title, body);
    }

    void Apply(Post p, PostRequest req, string title, string body)
    {
        p.Type = req.Type; p.Title = title; p.Body = body;
        p.SpeciesIds = (req.SpeciesIds ?? []).Distinct().Take(5).ToList();
        p.Topics = (req.Topics ?? []).Select(t => VietnameseText.Slugify(t)).Where(t => t.Length > 0).Distinct().Take(5).ToList();
        p.SearchText = VietnameseText.Normalize($"{title} {body}");
        p.SaleFlag = CommunityRules.LooksLikeSale($"{title} {body}");
        p.UpdatedAt = Now; p.LastActivityAt = Now;
    }

    public async Task<Post> GetAsync(string id, CancellationToken ct) =>
        (ObjectId.TryParse(id, out _) ? await Posts.Find(p => p.Id == id && p.Status != PostStatus.Removed).FirstOrDefaultAsync(ct) : null)
        ?? throw DomainException.NotFound("bài viết");

    public async Task<Comment> CommentAsync(string userId, string postId, CommentRequest req, CancellationToken ct)
    {
        var p = await GetAsync(postId, ct);
        if (p.Status != PostStatus.Visible) throw DomainException.Conflict("POST_HIDDEN", "Bài đang bị ẩn");
        var body = req.Body?.Trim() ?? "";
        if (body.Length is < 1 or > 3000) throw new DomainException("INVALID_BODY", "Bình luận dài 1–3000 ký tự");
        await RequireAuthorAsync(userId, body, ct);
        var c = new Comment { PostId = postId, AuthorId = userId, Body = body, CreatedAt = Now };
        c.MediaIds = await OwnedMediaAsync(req.MediaIds, userId, 4, $"comment:{c.Id}", ct);
        await Comments.InsertOneAsync(c, cancellationToken: ct);
        await Posts.UpdateOneAsync(x => x.Id == postId, Builders<Post>.Update.Inc(x => x.Comments, 1).Set(x => x.LastActivityAt, Now), cancellationToken: ct);
        if (p.AuthorId != userId)
            await notifications.SendAsync(p.AuthorId, "community.comment", p.Type == PostType.Question ? "Có câu trả lời mới" : "Có bình luận mới", p.Title, $"/cong-dong/{p.Id}", ct);
        return c;
    }

    /// <summary>Tác giả câu hỏi chọn câu trả lời hay nhất (+10 điểm cho người trả lời).</summary>
    public async Task MarkBestAsync(string userId, string postId, string commentId, CancellationToken ct)
    {
        var p = await GetAsync(postId, ct);
        if (p.AuthorId != userId || p.Type != PostType.Question) throw DomainException.Forbidden("Chỉ người hỏi chọn được câu trả lời hay nhất");
        if (p.BestCommentId is not null) throw DomainException.Conflict("BEST_ALREADY_CHOSEN", "Đã chọn câu trả lời hay nhất");
        var c = await Comments.Find(x => x.Id == commentId && x.PostId == postId && x.Status == PostStatus.Visible).FirstOrDefaultAsync(ct)
                ?? throw DomainException.NotFound("câu trả lời");
        var res = await Posts.UpdateOneAsync(x => x.Id == postId && x.BestCommentId == null, Builders<Post>.Update.Set(x => x.BestCommentId, commentId), cancellationToken: ct);
        if (res.ModifiedCount == 0) return;
        await Comments.UpdateOneAsync(x => x.Id == commentId, Builders<Comment>.Update.Set(x => x.IsBest, true), cancellationToken: ct);
        if (c.AuthorId != userId)
        {
            await AddPointsAsync(c.AuthorId, 10, false, ct);
            await notifications.SendAsync(c.AuthorId, "community.best", "Câu trả lời của bạn được chọn hay nhất (+10 điểm)", p.Title, $"/cong-dong/{p.Id}", ct);
        }
    }

    public async Task<bool> ToggleLikeAsync(string userId, string targetId, bool isComment, bool like, CancellationToken ct)
    {
        string authorId;
        if (isComment)
        {
            var c = await Comments.Find(x => x.Id == targetId && x.Status == PostStatus.Visible).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("bình luận");
            authorId = c.AuthorId;
        }
        else authorId = (await GetAsync(targetId, ct)).AuthorId;

        var id = $"{userId}:{targetId}";
        int delta;
        if (like)
        {
            try { await Likes.InsertOneAsync(new PostLike { Id = id, UserId = userId, TargetId = targetId, CreatedAt = Now }, cancellationToken: ct); delta = 1; }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { delta = 0; }
        }
        else delta = (await Likes.DeleteOneAsync(x => x.Id == id, ct)).DeletedCount > 0 ? -1 : 0;
        if (delta == 0) return like;
        if (isComment) await Comments.UpdateOneAsync(x => x.Id == targetId, Builders<Comment>.Update.Inc(x => x.Likes, delta), cancellationToken: ct);
        else await Posts.UpdateOneAsync(x => x.Id == targetId, Builders<Post>.Update.Inc(x => x.Likes, delta), cancellationToken: ct);
        if (delta > 0 && authorId != userId) await AddPointsAsync(authorId, 1, true, ct);
        return like;
    }

    /// <summary>BR-COM-03: tim +1 (tối đa 20 điểm/ngày), hay nhất +10.</summary>
    async Task AddPointsAsync(string userId, int points, bool fromLike, CancellationToken ct)
    {
        if (!fromLike)
        {
            await Profiles.UpdateOneAsync(x => x.UserId == userId, Builders<CommunityProfile>.Update.Inc(x => x.Points, points), new UpdateOptions { IsUpsert = true }, ct);
            return;
        }
        var day = Now.AddHours(7).ToString("yyyy-MM-dd");
        await Profiles.UpdateOneAsync(x => x.UserId == userId, Builders<CommunityProfile>.Update.SetOnInsert(x => x.Points, 0), new UpdateOptions { IsUpsert = true }, ct);
        await Profiles.UpdateOneAsync(x => x.UserId == userId && x.LikePointsDay != day,
            Builders<CommunityProfile>.Update.Set(x => x.LikePointsDay, day).Set(x => x.LikePointsToday, 0), cancellationToken: ct);
        await Profiles.UpdateOneAsync(x => x.UserId == userId && x.LikePointsToday < 20,
            Builders<CommunityProfile>.Update.Inc(x => x.Points, points).Inc(x => x.LikePointsToday, points), cancellationToken: ct);
    }

    /// <summary>BR-COM-02: ≥ 3 báo cáo thì tự ẩn chờ xử lý.</summary>
    public async Task ReportAsync(string userId, string targetId, bool isComment, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("REASON_REQUIRED", "Chọn lý do báo cáo");
        if (isComment)
        {
            var res = await Comments.UpdateOneAsync(x => x.Id == targetId && !x.ReportedBy.Contains(userId),
                Builders<Comment>.Update.AddToSet(x => x.ReportedBy, userId).Inc(x => x.ReportCount, 1), cancellationToken: ct);
            if (res.ModifiedCount > 0)
                await Comments.UpdateOneAsync(x => x.Id == targetId && x.ReportCount >= HideAtReports && x.Status == PostStatus.Visible,
                    Builders<Comment>.Update.Set(x => x.Status, PostStatus.Hidden), cancellationToken: ct);
            return;
        }
        var r = await Posts.UpdateOneAsync(x => x.Id == targetId && !x.ReportedBy.Contains(userId),
            Builders<Post>.Update.AddToSet(x => x.ReportedBy, userId).Inc(x => x.ReportCount, 1), cancellationToken: ct);
        if (r.ModifiedCount > 0)
            await Posts.UpdateOneAsync(x => x.Id == targetId && x.ReportCount >= HideAtReports && x.Status == PostStatus.Visible,
                Builders<Post>.Update.Set(x => x.Status, PostStatus.Hidden), cancellationToken: ct);
    }

    public async Task<Dictionary<string, object>> AuthorsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var us = await users.Users.Find(u => list.Contains(u.Id)).ToListAsync(ct);
        var profiles = (await Profiles.Find(p => list.Contains(p.UserId)).ToListAsync(ct)).ToDictionary(p => p.UserId);
        var gardenList = await gardens.Gardens.Find(g => list.Contains(g.OwnerId)).ToListAsync(ct);
        return us.ToDictionary(u => u.Id, u =>
        {
            var p = profiles.GetValueOrDefault(u.Id);
            var g = gardenList.FirstOrDefault(x => x.OwnerId == u.Id);
            // BR-COM-05: nhà vườn có gói còn hạn hiện liên kết gian hàng dưới tên.
            return (object)new
            {
                u.Id, name = u.Status == UserStatus.Deleted ? "Người dùng đã xóa" : u.DisplayName, u.AvatarMediaId,
                points = p?.Points ?? 0, badge = CommunityRules.Badge(p?.Points ?? 0), expert = p?.Expert == true, p?.ExpertTitle,
                gardenSlug = u.Flags.HasActivePlan ? g?.Slug : null, verified = u.Flags.HasVerifiedGarden,
            };
        });
    }
}

public static class CommunityEndpoints
{
    public static void MapCommunity(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/community").WithTags("Community");

        pub.MapGet("/posts", async (PostType? type, string? q, string? speciesId, string? topic, string? authorId, string? sort, int? page,
            CommunityService svc, FeatureFlagService flags, CancellationToken ct) =>
        {
            await flags.RequireAsync(Flags.Community, ct);
            var f = Builders<Post>.Filter;
            var filter = f.Eq(p => p.Status, PostStatus.Visible);
            if (type is { } t) filter &= f.Eq(p => p.Type, t);
            if (!string.IsNullOrWhiteSpace(speciesId)) filter &= f.AnyEq(p => p.SpeciesIds, speciesId);
            if (!string.IsNullOrWhiteSpace(topic)) filter &= f.AnyEq(p => p.Topics, VietnameseText.Slugify(topic));
            if (!string.IsNullOrWhiteSpace(authorId)) filter &= f.Eq(p => p.AuthorId, authorId);
            if (!string.IsNullOrWhiteSpace(q))
                foreach (var word in VietnameseText.Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6))
                    filter &= f.Regex(p => p.SearchText, new BsonRegularExpression(Regex.Escape(word)));
            if (sort == "unanswered") filter &= f.Eq(p => p.Type, PostType.Question) & f.Eq(p => p.Comments, 0);
            var pageNo = Math.Max(1, page ?? 1);
            var find = svc.Posts.Find(filter);
            find = sort == "top" ? find.SortByDescending(p => p.Likes).ThenByDescending(p => p.CreatedAt) : find.SortByDescending(p => p.LastActivityAt);
            var list = await find.Skip((pageNo - 1) * 20).Limit(20).ToListAsync(ct);
            var authors = await svc.AuthorsAsync(list.Select(p => p.AuthorId), ct);
            return new
            {
                items = list.Select(p => new
                {
                    p.Id, p.Type, p.Title, excerpt = p.Body.Length > 200 ? p.Body[..200] + "…" : p.Body, p.SpeciesIds, p.Topics,
                    p.Likes, p.Comments, answered = p.BestCommentId != null, p.CreatedAt, p.LastActivityAt,
                    thumbUrl = p.MediaIds.Count > 0 ? $"/media/{p.MediaIds[0]}/thumb.webp" : null,
                    author = authors.GetValueOrDefault(p.AuthorId),
                }),
                page = pageNo,
            };
        });
        pub.MapGet("/posts/{id}", async (string id, ClaimsPrincipal user, CommunityService svc, MediaService media, CancellationToken ct) =>
        {
            var p = await svc.GetAsync(id, ct);
            var me = user.Identity?.IsAuthenticated == true ? user.UserId() : null;
            if (p.Status == PostStatus.Hidden && p.AuthorId != me) throw DomainException.NotFound("bài viết");
            var comments = await svc.Comments.Find(c => c.PostId == id && c.Status == PostStatus.Visible).SortBy(c => c.CreatedAt).Limit(500).ToListAsync(ct);
            var authors = await svc.AuthorsAsync(comments.Select(c => c.AuthorId).Append(p.AuthorId), ct);
            var mediaIds = p.MediaIds.Concat(comments.SelectMany(c => c.MediaIds)).ToList();
            var items = (await media.Items.Find(m => mediaIds.Contains(m.Id)).ToListAsync(ct)).ToDictionary(m => m.Id, m => MediaService.ToDto(m));
            var likedIds = me is null ? [] : (await svc.Likes.Find(l => l.UserId == me && (l.TargetId == id || comments.Select(c => c.Id).Contains(l.TargetId))).ToListAsync(ct))
                .Select(l => l.TargetId).ToHashSet();
            return new
            {
                post = new
                {
                    p.Id, p.Type, p.Title, p.Body, p.SpeciesIds, p.Topics, p.Likes, p.Comments, p.BestCommentId, p.Status, p.CreatedAt, p.UpdatedAt,
                    suggestListing = p.SaleFlag && p.AuthorId == me, liked = likedIds.Contains(p.Id),
                    photos = p.MediaIds.Where(items.ContainsKey).Select(m => items[m]), author = authors.GetValueOrDefault(p.AuthorId),
                },
                comments = comments.OrderByDescending(c => c.IsBest).ThenBy(c => c.CreatedAt).Select(c => new
                {
                    c.Id, c.Body, c.Likes, c.IsBest, c.CreatedAt, liked = likedIds.Contains(c.Id),
                    photos = c.MediaIds.Where(items.ContainsKey).Select(m => items[m]), author = authors.GetValueOrDefault(c.AuthorId),
                }),
            };
        });
        pub.MapGet("/users/{id}", async (string id, CommunityService svc, CancellationToken ct) =>
            (await svc.AuthorsAsync([id], ct)).GetValueOrDefault(id) ?? throw DomainException.NotFound("người dùng"));

        var g = app.MapGroup("/api/community").WithTags("Community").RequireAuthorization(Policies.Member);
        g.MapPost("/posts", async (PostRequest req, ClaimsPrincipal p, CommunityService svc, FeatureFlagService flags, CancellationToken ct) =>
        {
            await flags.RequireAsync(Flags.Community, ct);
            var post = await svc.CreateAsync(p.UserId(), req, ct);
            return new { post.Id, post.Status, suggestListing = post.SaleFlag };
        });
        g.MapPut("/posts/{id}", async (string id, PostRequest req, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) =>
        {
            var post = await svc.UpdateAsync(p.UserId(), id, req, ct);
            return new { post.Id, post.Status, suggestListing = post.SaleFlag };
        });
        g.MapDelete("/posts/{id}", async (string id, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var res = await svc.Posts.UpdateOneAsync(x => x.Id == id && x.AuthorId == userId, Builders<Post>.Update.Set(x => x.Status, PostStatus.Removed), cancellationToken: ct);
            return res.ModifiedCount > 0 ? Results.NoContent() : throw DomainException.NotFound("bài viết");
        });
        g.MapPost("/posts/{id}/comments", (string id, CommentRequest req, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) => svc.CommentAsync(p.UserId(), id, req, ct));
        g.MapDelete("/comments/{id}", async (string id, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var c = await svc.Comments.FindOneAndUpdateAsync<Comment>(x => x.Id == id && x.AuthorId == userId && x.Status == PostStatus.Visible,
                Builders<Comment>.Update.Set(x => x.Status, PostStatus.Removed), cancellationToken: ct) ?? throw DomainException.NotFound("bình luận");
            await svc.Posts.UpdateOneAsync(x => x.Id == c.PostId, Builders<Post>.Update.Inc(x => x.Comments, -1), cancellationToken: ct);
            return Results.NoContent();
        });
        g.MapPost("/posts/{id}/best/{commentId}", async (string id, string commentId, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) =>
        {
            await svc.MarkBestAsync(p.UserId(), id, commentId, ct);
            return Results.NoContent();
        });
        g.MapPut("/posts/{id}/like", async (string id, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) => new { liked = await svc.ToggleLikeAsync(p.UserId(), id, false, true, ct) });
        g.MapDelete("/posts/{id}/like", async (string id, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) => new { liked = await svc.ToggleLikeAsync(p.UserId(), id, false, false, ct) });
        g.MapPut("/comments/{id}/like", async (string id, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) => new { liked = await svc.ToggleLikeAsync(p.UserId(), id, true, true, ct) });
        g.MapDelete("/comments/{id}/like", async (string id, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) => new { liked = await svc.ToggleLikeAsync(p.UserId(), id, true, false, ct) });
        g.MapPost("/posts/{id}/report", async (string id, ReportRequest req, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) =>
        {
            await svc.ReportAsync(p.UserId(), id, false, req.Reason, ct);
            return Results.NoContent();
        });
        g.MapPost("/comments/{id}/report", async (string id, ReportRequest req, ClaimsPrincipal p, CommunityService svc, CancellationToken ct) =>
        {
            await svc.ReportAsync(p.UserId(), id, true, req.Reason, ct);
            return Results.NoContent();
        });

        // ---- Quản trị (UC-ADM-03: xử lý báo cáo bài cộng đồng; BR-COM-04: cấp huy hiệu Chuyên gia) ----
        var admin = app.MapGroup("/api/admin/community").WithTags("Admin Community").RequireAuthorization(Policies.ForPerm(Perm.ContentManage));
        admin.MapGet("/reported", async (CommunityService svc, CancellationToken ct) => new
        {
            posts = await svc.Posts.Find(p => p.ReportCount > 0 && p.Status != PostStatus.Removed || p.Status == PostStatus.Hidden).SortByDescending(p => p.ReportCount).Limit(100).ToListAsync(ct),
            comments = await svc.Comments.Find(c => c.ReportCount > 0 && c.Status != PostStatus.Removed).SortByDescending(c => c.ReportCount).Limit(100).ToListAsync(ct),
        });
        admin.MapPost("/posts/{id}/moderate", async (string id, ModeratePostRequest req, ClaimsPrincipal p, CommunityService svc, AuditService audit, CancellationToken ct) =>
        {
            var u = Builders<Post>.Update.Set(x => x.Status, req.Status);
            if (req.Status == PostStatus.Visible) u = u.Set(x => x.ReportCount, 0).Set(x => x.ReportedBy, []);
            await svc.Posts.UpdateOneAsync(x => x.Id == id, u, cancellationToken: ct);
            await audit.LogAsync(p, "community.moderate_post", "post", id, after: new { req.Status }, reason: req.Reason, ct: ct);
            return Results.NoContent();
        });
        admin.MapPost("/comments/{id}/moderate", async (string id, ModeratePostRequest req, ClaimsPrincipal p, CommunityService svc, AuditService audit, CancellationToken ct) =>
        {
            var u = Builders<Comment>.Update.Set(x => x.Status, req.Status);
            if (req.Status == PostStatus.Visible) u = u.Set(x => x.ReportCount, 0).Set(x => x.ReportedBy, []);
            await svc.Comments.UpdateOneAsync(x => x.Id == id, u, cancellationToken: ct);
            await audit.LogAsync(p, "community.moderate_comment", "comment", id, after: new { req.Status }, reason: req.Reason, ct: ct);
            return Results.NoContent();
        });
        admin.MapPut("/experts/{userId}", async (string userId, ExpertRequest req, ClaimsPrincipal p, CommunityService svc, UserService users, AuditService audit, CancellationToken ct) =>
        {
            await users.GetAsync(userId, ct);
            await svc.Profiles.UpdateOneAsync(x => x.UserId == userId, Builders<CommunityProfile>.Update.Set(x => x.Expert, req.Expert).Set(x => x.ExpertTitle, req.Title?.Trim()),
                new UpdateOptions { IsUpsert = true }, ct);
            await audit.LogAsync(p, "community.expert", "user", userId, after: req, ct: ct);
            return Results.NoContent();
        });
    }
}
