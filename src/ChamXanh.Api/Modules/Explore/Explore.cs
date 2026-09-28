using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Media;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Explore;

public enum ArticleStatus { Draft, Published, Archived }

/// <summary>Bài "Khám phá": giới thiệu nhanh một loài cây ít người biết, đội nội dung đăng mỗi ngày.
/// Gồm khu vực ảnh (ảnh bìa + tối đa 8 ảnh) và khu vực nội dung.</summary>
public class Article
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string Slug { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string? Summary { get; set; }
    public string Body { get; set; } = default!; // văn bản thuần, đoạn cách nhau bằng dòng trống
    public List<string> MediaIds { get; set; } = []; // phần tử đầu là ảnh bìa
    public string? SpeciesId { get; set; }
    public List<string> Tags { get; set; } = [];
    [BsonRepresentation(BsonType.String)] public ArticleStatus Status { get; set; }
    /// <summary>Hiện công khai từ thời điểm này — cho phép xếp lịch trước bài cho các ngày tới.</summary>
    public DateTime? PublishAt { get; set; }
    public long Views { get; set; }
    public string CreatedBy { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public record ArticleRequest(string Title, string? Summary, string Body, List<string>? MediaIds, string? SpeciesId, List<string>? Tags,
    ArticleStatus Status, DateTime? PublishAt, string? Slug);

public class ExploreService(IMongoDatabase db, TimeProvider clock, MediaService media, CatalogService catalog)
{
    public IMongoCollection<Article> Articles { get; } = db.GetCollection<Article>("articles");

    public async Task EnsureIndexesAsync()
    {
        await Articles.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Article>(Builders<Article>.IndexKeys.Ascending(a => a.Slug), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<Article>(Builders<Article>.IndexKeys.Ascending(a => a.Status).Descending(a => a.PublishAt)),
        ]);
    }

    public FilterDefinition<Article> Visible()
    {
        var f = Builders<Article>.Filter;
        return f.Eq(a => a.Status, ArticleStatus.Published) & f.Lte(a => a.PublishAt, clock.GetUtcNow().UtcDateTime);
    }

    public async Task<Article> SaveAsync(string? id, ArticleRequest req, ClaimsPrincipal actor, CancellationToken ct)
    {
        var title = req.Title?.Trim() ?? "";
        if (title.Length is < 5 or > 150) throw new DomainException("INVALID_TITLE", "Tiêu đề dài 5–150 ký tự");
        var body = req.Body?.Trim() ?? "";
        if (body.Length is < 50 or > 20_000) throw new DomainException("INVALID_BODY", "Nội dung dài 50–20.000 ký tự");
        if (req.Summary?.Length > 300) throw new DomainException("INVALID_SUMMARY", "Tóm tắt tối đa 300 ký tự");
        var mediaIds = req.MediaIds ?? [];
        if (mediaIds.Count > 9) throw new DomainException("TOO_MANY_PHOTOS", "Tối đa 9 ảnh");
        if (req.Status == ArticleStatus.Published && mediaIds.Count == 0) throw new DomainException("COVER_REQUIRED", "Bài đăng cần ít nhất 1 ảnh");
        var items = mediaIds.Count == 0 ? [] : await media.Items.Find(m => mediaIds.Contains(m.Id)).ToListAsync(ct);
        if (items.Count != mediaIds.Distinct().Count() || items.Any(m => m.Secure || m.FilesDeleted)) throw new DomainException("INVALID_MEDIA", "Có ảnh không hợp lệ");
        if (!string.IsNullOrWhiteSpace(req.SpeciesId)) await catalog.GetSpeciesAsync(req.SpeciesId, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var a = id is null
            ? new Article { CreatedBy = actor.UserId(), CreatedAt = now }
            : await Articles.Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("bài viết");
        var slug = VietnameseText.Slugify(string.IsNullOrWhiteSpace(req.Slug) ? title : req.Slug);
        if (slug.Length == 0) throw new DomainException("INVALID_SLUG", "Đường dẫn không hợp lệ");
        if (await Articles.Find(x => x.Slug == slug && x.Id != a.Id).AnyAsync(ct))
            throw DomainException.Conflict("SLUG_TAKEN", "Đường dẫn đã được dùng cho bài khác");

        a.Title = title; a.Summary = req.Summary?.Trim(); a.Body = body; a.MediaIds = mediaIds;
        a.SpeciesId = string.IsNullOrWhiteSpace(req.SpeciesId) ? null : req.SpeciesId;
        a.Tags = (req.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(10).ToList();
        a.Slug = slug; a.Status = req.Status; a.UpdatedAt = now;
        a.PublishAt = req.Status == ArticleStatus.Published ? req.PublishAt ?? a.PublishAt ?? now : req.PublishAt;
        await Articles.ReplaceOneAsync(x => x.Id == a.Id, a, new ReplaceOptions { IsUpsert = true }, ct);
        await media.AttachAsync(mediaIds, $"article:{a.Id}", ct);
        return a;
    }

    public async Task<object> ToDtoAsync(Article a, bool full, CancellationToken ct)
    {
        var items = await media.Items.Find(m => a.MediaIds.Contains(m.Id)).ToListAsync(ct);
        var photos = a.MediaIds.Select(id => items.FirstOrDefault(m => m.Id == id)).Where(m => m is not null).Select(m => MediaService.ToDto(m!)).ToList();
        return new
        {
            a.Id, a.Slug, a.Title, a.Summary, body = full ? a.Body : null, a.SpeciesId, a.Tags, a.Status, a.PublishAt, a.Views,
            cover = photos.FirstOrDefault(), photos = full ? photos : null,
        };
    }
}

public static class ExploreEndpoints
{
    public static void MapExplore(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/explore").WithTags("Explore");

        pub.MapGet("/", async (int? page, int? pageSize, ExploreService svc, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 12, 1, 50);
            var p = Math.Max(1, page ?? 1);
            var filter = svc.Visible();
            var list = await svc.Articles.Find(filter).SortByDescending(a => a.PublishAt).Skip((p - 1) * size).Limit(size).ToListAsync(ct);
            var total = await svc.Articles.CountDocumentsAsync(filter, cancellationToken: ct);
            var items = new List<object>();
            foreach (var a in list) items.Add(await svc.ToDtoAsync(a, false, ct));
            return new { items, total, page = p, pageSize = size };
        });
        // Bài của hôm nay cho trang chủ: bài mới nhất đã đến giờ đăng.
        pub.MapGet("/today", async (ExploreService svc, CancellationToken ct) =>
        {
            var a = await svc.Articles.Find(svc.Visible()).SortByDescending(x => x.PublishAt).FirstOrDefaultAsync(ct);
            return a is null ? Results.NoContent() : Results.Ok(await svc.ToDtoAsync(a, true, ct));
        });
        pub.MapGet("/{slug}", async (string slug, ExploreService svc, CancellationToken ct) =>
        {
            var a = await svc.Articles.Find(svc.Visible() & Builders<Article>.Filter.Eq(x => x.Slug, slug)).FirstOrDefaultAsync(ct)
                    ?? throw DomainException.NotFound("bài viết");
            await svc.Articles.UpdateOneAsync(x => x.Id == a.Id, Builders<Article>.Update.Inc(x => x.Views, 1), cancellationToken: ct);
            return await svc.ToDtoAsync(a, true, ct);
        });

        var admin = app.MapGroup("/api/admin/explore").WithTags("Admin Explore").RequireAuthorization(Policies.ForPerm(Perm.ContentManage));
        admin.MapGet("/", async (ArticleStatus? status, ExploreService svc, CancellationToken ct) =>
        {
            var f = status is { } s ? Builders<Article>.Filter.Eq(a => a.Status, s) : Builders<Article>.Filter.Empty;
            var list = await svc.Articles.Find(f).SortByDescending(a => a.PublishAt).ThenByDescending(a => a.UpdatedAt).Limit(200).ToListAsync(ct);
            var items = new List<object>();
            foreach (var a in list) items.Add(await svc.ToDtoAsync(a, false, ct));
            return items;
        });
        admin.MapGet("/{id}", async (string id, ExploreService svc, CancellationToken ct) =>
        {
            var a = await svc.Articles.Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("bài viết");
            return await svc.ToDtoAsync(a, true, ct);
        });
        admin.MapPost("/", async (ArticleRequest req, ClaimsPrincipal p, ExploreService svc, AuditService audit, CancellationToken ct) =>
        {
            var a = await svc.SaveAsync(null, req, p, ct);
            await audit.LogAsync(p, "explore.create", "article", a.Id, after: new { a.Title, a.Status, a.PublishAt }, ct: ct);
            return a;
        });
        admin.MapPut("/{id}", async (string id, ArticleRequest req, ClaimsPrincipal p, ExploreService svc, AuditService audit, CancellationToken ct) =>
        {
            var a = await svc.SaveAsync(id, req, p, ct);
            await audit.LogAsync(p, "explore.update", "article", a.Id, after: new { a.Title, a.Status, a.PublishAt }, ct: ct);
            return a;
        });
        admin.MapDelete("/{id}", async (string id, ClaimsPrincipal p, ExploreService svc, MediaService media, AuditService audit, CancellationToken ct) =>
        {
            var a = await svc.Articles.FindOneAndDeleteAsync(x => x.Id == id, cancellationToken: ct) ?? throw DomainException.NotFound("bài viết");
            await media.AttachAsync(a.MediaIds, null!, ct);
            await audit.LogAsync(p, "explore.delete", "article", id, before: new { a.Title }, ct: ct);
            return Results.NoContent();
        });

        // Ảnh do quản trị viên tải lên (bài Khám phá, banner...): không đóng watermark.
        app.MapPost("/api/admin/media", async ([FromForm] IFormFile file, ClaimsPrincipal p, MediaService media, CancellationToken ct) =>
        {
            if (file.Length is 0 or > 15 * 1024 * 1024) throw new DomainException("INVALID_FILE", "Ảnh rỗng hoặc lớn hơn 15MB");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var item = await media.UploadImageAsync(p.UserId(), MediaKind.ArticlePhoto, ms.ToArray(), null, false, null, ct);
            return MediaService.ToDto(item);
        }).WithTags("Admin Explore").RequireAuthorization(Policies.ForPerm(Perm.ContentManage)).DisableAntiforgery();
    }
}
