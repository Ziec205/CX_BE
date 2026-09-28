using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Listings;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Discovery;

public class Favorite
{
    [BsonId] public string Id { get; set; } = default!; // {userId}:{listingId}
    public string UserId { get; set; } = default!;
    public string ListingId { get; set; } = default!;
    public long? PriceWhenSaved { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Follow
{
    [BsonId] public string Id { get; set; } = default!; // {followerId}:{sellerId}
    public string FollowerId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Tìm kiếm đã lưu — thông báo khi có tin mới khớp (gộp ≤ 1 lần/giờ, tài liệu 03 §5.4).</summary>
public class SavedSearch
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public ListingQuery Query { get; set; } = default!;
    public DateTime LastNotifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record SaveSearchRequest(string Name, ListingQuery Query);

public static class DiscoveryEndpoints
{
    const int MaxSavedSearches = 20;

    public static async Task EnsureIndexesAsync(IMongoDatabase db)
    {
        await db.GetCollection<Favorite>("favorites").Indexes.CreateOneAsync(new CreateIndexModel<Favorite>(Builders<Favorite>.IndexKeys.Ascending(f => f.UserId).Descending(f => f.CreatedAt)));
        await db.GetCollection<Favorite>("favorites").Indexes.CreateOneAsync(new CreateIndexModel<Favorite>(Builders<Favorite>.IndexKeys.Ascending(f => f.ListingId)));
        await db.GetCollection<Follow>("follows").Indexes.CreateOneAsync(new CreateIndexModel<Follow>(Builders<Follow>.IndexKeys.Ascending(f => f.SellerId)));
        await db.GetCollection<SavedSearch>("savedSearches").Indexes.CreateOneAsync(new CreateIndexModel<SavedSearch>(Builders<SavedSearch>.IndexKeys.Ascending(s => s.UserId)));
    }

    public static void MapDiscovery(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me").WithTags("Discovery").RequireAuthorization(Policies.Member);

        g.MapPut("/favorites/{listingId}", async (string listingId, ClaimsPrincipal p, IMongoDatabase db, ListingService listings, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var l = await listings.GetAsync(listingId, ct);
            if (!ListingRules.IsPubliclyVisible(l.Status)) throw DomainException.NotFound("tin đăng");
            var fav = new Favorite { Id = $"{userId}:{listingId}", UserId = userId, ListingId = listingId, PriceWhenSaved = l.EffectivePrice, CreatedAt = clock.GetUtcNow().UtcDateTime };
            await db.GetCollection<Favorite>("favorites").ReplaceOneAsync(f => f.Id == fav.Id, fav, new ReplaceOptions { IsUpsert = true }, ct);
            return Results.NoContent();
        });
        g.MapDelete("/favorites/{listingId}", async (string listingId, ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var id = $"{p.UserId()}:{listingId}";
            await db.GetCollection<Favorite>("favorites").DeleteOneAsync(f => f.Id == id, ct);
            return Results.NoContent();
        });
        g.MapGet("/favorites", async (ClaimsPrincipal p, IMongoDatabase db, ListingService listings, ListingSearch search, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var favs = await db.GetCollection<Favorite>("favorites").Find(f => f.UserId == userId).SortByDescending(f => f.CreatedAt).Limit(200).ToListAsync(ct);
            var ids = favs.Select(f => f.ListingId).ToList();
            var items = await listings.Listings.Find(l => ids.Contains(l.Id) && l.Status != ListingStatus.Deleted).ToListAsync(ct);
            var sellers = await search.SellerSummariesAsync(items.Select(i => i.SellerId), ct);
            var now = clock.GetUtcNow().UtcDateTime;
            return favs.Select(f => items.FirstOrDefault(i => i.Id == f.ListingId) is { } l
                ? new { card = search.ToCard(l, sellers[l.SellerId], now), f.PriceWhenSaved, priceDropped = l.EffectivePrice < f.PriceWhenSaved }
                : null).Where(x => x is not null);
        });

        g.MapPut("/follows/{sellerId}", async (string sellerId, ClaimsPrincipal p, IMongoDatabase db, Identity.UserService users, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            if (sellerId == userId) throw new DomainException("CANNOT_FOLLOW_SELF", "Không thể theo dõi chính mình");
            await users.GetAsync(sellerId, ct);
            var f = new Follow { Id = $"{userId}:{sellerId}", FollowerId = userId, SellerId = sellerId, CreatedAt = clock.GetUtcNow().UtcDateTime };
            await db.GetCollection<Follow>("follows").ReplaceOneAsync(x => x.Id == f.Id, f, new ReplaceOptions { IsUpsert = true }, ct);
            return Results.NoContent();
        });
        g.MapDelete("/follows/{sellerId}", async (string sellerId, ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var id = $"{p.UserId()}:{sellerId}";
            await db.GetCollection<Follow>("follows").DeleteOneAsync(f => f.Id == id, ct);
            return Results.NoContent();
        });
        g.MapGet("/follows", async (ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await db.GetCollection<Follow>("follows").Find(f => f.FollowerId == userId).ToListAsync(ct);
        });
        app.MapGet("/api/users/{id}/followers/count", async (string id, IMongoDatabase db, CancellationToken ct) =>
            new { count = await db.GetCollection<Follow>("follows").CountDocumentsAsync(f => f.SellerId == id, cancellationToken: ct) }).WithTags("Discovery");

        g.MapPost("/saved-searches", async (SaveSearchRequest req, ClaimsPrincipal p, IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var coll = db.GetCollection<SavedSearch>("savedSearches");
            if (await coll.CountDocumentsAsync(s => s.UserId == userId, cancellationToken: ct) >= MaxSavedSearches)
                throw new DomainException("LIMIT_REACHED", $"Tối đa {MaxSavedSearches} tìm kiếm đã lưu");
            var now = clock.GetUtcNow().UtcDateTime;
            var s = new SavedSearch { UserId = userId, Name = req.Name.Trim(), Query = req.Query with { Page = 1 }, CreatedAt = now, LastNotifiedAt = now };
            await coll.InsertOneAsync(s, cancellationToken: ct);
            return s;
        });
        g.MapGet("/saved-searches", async (ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await db.GetCollection<SavedSearch>("savedSearches").Find(s => s.UserId == userId).ToListAsync(ct);
        });
        g.MapDelete("/saved-searches/{id}", async (string id, ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await db.GetCollection<SavedSearch>("savedSearches").DeleteOneAsync(s => s.Id == id && s.UserId == userId, ct);
            return Results.NoContent();
        });
        // Tin mới khớp tìm kiếm đã lưu kể từ lần xem gần nhất (thông báo đẩy sẽ nối vào module Thông báo).
        g.MapGet("/saved-searches/{id}/new", async (string id, ClaimsPrincipal p, IMongoDatabase db, ListingSearch search, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var coll = db.GetCollection<SavedSearch>("savedSearches");
            var s = await coll.Find(x => x.Id == id && x.UserId == userId).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("tìm kiếm đã lưu");
            var days = Math.Max(1, (int)Math.Ceiling((clock.GetUtcNow().UtcDateTime - s.LastNotifiedAt).TotalDays));
            var res = await search.SearchAsync(s.Query with { PostedWithinDays = days, Sort = ListingSort.Newest }, ct);
            await coll.UpdateOneAsync(x => x.Id == id, Builders<SavedSearch>.Update.Set(x => x.LastNotifiedAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
            return res.Items;
        });
    }
}
