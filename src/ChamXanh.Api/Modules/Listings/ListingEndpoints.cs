using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Moderation;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Listings;

public record ListingDetail(ListingCard Card, string Description, Dictionary<string, object> Attributes, List<string> PhotoUrls,
    string? VerificationPhotoUrl, List<string> PickupOptions, string? WantInExchange, DateTime? NeededBy, int Quantity, int Views,
    DateTime? FirstPublishedAt, DateTime? ExpiresAt, bool IsOwner, string? RejectReason, object? PendingRevision, string? RevisionRejectReason,
    DateTime? PriorityUntil, bool AppealUsed, double? Lat, double? Lng, List<string> Uses);

public record SaveListingRequest(ListingInput Listing, bool Submit = true);
public record RestockRequest(int Quantity);
public record ReportRequest(ReportReason Reason, string? Note);
public record AppealRequest(string Reason);
public record ProxyListingRequest(string SellerId, ListingInput Listing);

public class ProxyAuthorization
{
    [BsonId] public string SellerId { get; set; } = default!;
    public DateTime GrantedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class PhoneView
{
    [BsonId] public ObjectId Id { get; set; }
    public string ViewerId { get; set; } = default!;
    public string ListingId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    public DateTime At { get; set; }
}

public static class ListingEndpoints
{
    const int MaxPhoneViewsPerDay = 30; // BR-PHN-02

    public static void MapListings(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/listings").WithTags("Listings");

        pub.MapGet("/", ([AsParameters] ListingQueryParams p, HttpContext http, ListingSearch search, CancellationToken ct) =>
            search.SearchAsync(p.ToQuery(http.Request.Query), ct));

        pub.MapGet("/{id}", async (string id, ClaimsPrincipal p, ListingService svc, ListingSearch search, TimeProvider clock, CancellationToken ct) =>
        {
            var l = await svc.GetAsync(id, ct);
            var isOwner = p.Identity?.IsAuthenticated == true && !p.IsAdmin() && p.UserId() == l.SellerId;
            if (!isOwner && !ListingRules.IsPubliclyVisible(l.Status) && !(p.IsAdmin() && p.HasClaim(Claims.Perm, Perm.ListingModerate)))
                throw DomainException.NotFound("tin đăng");
            if (!isOwner) await svc.Listings.UpdateOneAsync(x => x.Id == id, Builders<Listing>.Update.Inc(x => x.Views, 1), cancellationToken: ct);
            var sellers = await search.SellerSummariesAsync([l.SellerId], ct);
            var now = clock.GetUtcNow().UtcDateTime;
            return new ListingDetail(search.ToCard(l, sellers[l.SellerId], now, isPriority: l.PriorityUntil > now), l.Description, l.Attributes,
                l.Media.Select(m => $"/media/{m.MediaId}/full.webp").ToList(),
                l.VerificationMediaId is null ? null : $"/media/{l.VerificationMediaId}/full.webp",
                l.PickupOptions, l.WantInExchange, l.NeededBy, l.Quantity, l.Views, l.FirstPublishedAt, l.ExpiresAt, isOwner,
                isOwner ? l.RejectReason : null, isOwner ? l.PendingRevision : null, isOwner ? l.RevisionRejectReason : null,
                l.PriorityUntil, l.AppealUsed, l.Location?.Coordinates.Latitude, l.Location?.Coordinates.Longitude, l.Uses);
        });

        var member = app.MapGroup("/api").WithTags("Listings").RequireAuthorization(Policies.Member);

        member.MapPost("/listings", async (SaveListingRequest req, ClaimsPrincipal p, ListingService svc, CancellationToken ct) =>
            Results.Ok(await svc.CreateAsync(p.UserId(), req.Listing, req.Submit, ct)));
        member.MapPut("/listings/{id}", async (string id, SaveListingRequest req, ClaimsPrincipal p, ListingService svc, CancellationToken ct) =>
            Results.Ok(await svc.UpdateAsync(id, p.UserId(), req.Listing, ct)));
        member.MapPost("/listings/{id}/submit", (string id, ClaimsPrincipal p, ListingService svc, CancellationToken ct) => svc.SubmitAsync(id, p.UserId(), ct));
        member.MapPost("/listings/{id}/hide", (string id, ClaimsPrincipal p, ListingService svc, CancellationToken ct) => svc.HideAsync(id, p.UserId(), ct));
        member.MapPost("/listings/{id}/unhide", (string id, ClaimsPrincipal p, ListingService svc, CancellationToken ct) => svc.UnhideAsync(id, p.UserId(), ct));
        member.MapPost("/listings/{id}/mark-sold", (string id, ClaimsPrincipal p, ListingService svc, CancellationToken ct) => svc.MarkSoldAsync(id, p.UserId(), ct));
        member.MapPost("/listings/{id}/restock", (string id, RestockRequest req, ClaimsPrincipal p, ListingService svc, CancellationToken ct) =>
            svc.RestockAsync(id, p.UserId(), req.Quantity, ct));
        member.MapPost("/listings/{id}/renew", (string id, ClaimsPrincipal p, ListingService svc, CancellationToken ct) => svc.RenewAsync(id, p.UserId(), ct));
        member.MapDelete("/listings/{id}", async (string id, ClaimsPrincipal p, ListingService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, p.UserId(), ct);
            return Results.NoContent();
        });
        member.MapPost("/listings/{id}/appeal", async (string id, AppealRequest req, ClaimsPrincipal p, ListingService svc, CancellationToken ct) =>
        {
            var c = await svc.AppealAsync(id, p.UserId(), req.Reason, ct);
            return Results.Ok(new { caseId = c.Id });
        });
        member.MapPost("/listings/{id}/reports", async (string id, ReportRequest req, ClaimsPrincipal p, ListingService svc, CancellationToken ct) =>
        {
            await svc.ReportAsync(id, p.UserId(), req.Reason, req.Note, ct);
            return Results.Ok(new { reported = true });
        });

        // BR-PHN-01..04: SĐT chỉ hiện khi đăng nhập, tối đa 30 lượt/ngày, người bán có thể ẩn, mọi lượt xem được ghi log.
        member.MapGet("/listings/{id}/phone", async (string id, ClaimsPrincipal p, ListingService svc, UserService users,
            IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
        {
            var viewerId = p.UserId();
            var listing = await svc.GetAsync(id, ct);
            if (!ListingRules.IsPubliclyVisible(listing.Status)) throw DomainException.NotFound("tin đăng");
            var seller = await users.GetAsync(listing.SellerId, ct);
            if (seller.HidePhone || seller.Phone is null) throw DomainException.Forbidden("Người bán chỉ nhận liên hệ qua chat");
            var views = db.GetCollection<PhoneView>("phoneViews");
            var now = clock.GetUtcNow().UtcDateTime;
            var already = await views.Find(v => v.ViewerId == viewerId && v.ListingId == id && v.At > now.AddDays(-1)).AnyAsync(ct);
            if (!already)
            {
                var today = await views.CountDocumentsAsync(v => v.ViewerId == viewerId && v.At > now.AddDays(-1), cancellationToken: ct);
                if (today >= MaxPhoneViewsPerDay) throw DomainException.TooMany("Bạn đã xem quá nhiều số điện thoại hôm nay, hãy dùng chat");
                await views.InsertOneAsync(new PhoneView { ViewerId = viewerId, ListingId = id, SellerId = seller.Id, At = now }, cancellationToken: ct);
            }
            return Results.Ok(new { phone = seller.Phone });
        });

        member.MapGet("/me/listings", async (string? status, ClaimsPrincipal p, ListingService svc, ListingSearch search, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var f = Builders<Listing>.Filter.Eq(l => l.SellerId, userId) & Builders<Listing>.Filter.Ne(l => l.Status, ListingStatus.Deleted);
            if (Enum.TryParse<ListingStatus>(status, true, out var s)) f &= Builders<Listing>.Filter.Eq(l => l.Status, s);
            var items = await svc.Listings.Find(f).SortByDescending(l => l.UpdatedAt).Limit(200).ToListAsync(ct);
            var sellers = await search.SellerSummariesAsync([userId], ct);
            var now = clock.GetUtcNow().UtcDateTime;
            return items.Select(l => new { card = search.ToCard(l, sellers[userId], now), l.RejectReason, hasPendingRevision = l.PendingRevision is not null, l.ExpiresAt });
        });

        // Ủy quyền đăng tin hộ một lần, hiệu lực 90 ngày (BR-LST-14).
        member.MapPost("/me/proxy-authorization", async (ClaimsPrincipal p, IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var auth = new ProxyAuthorization { SellerId = p.UserId(), GrantedAt = now, ExpiresAt = now.AddDays(90) };
            await db.GetCollection<ProxyAuthorization>("proxyAuthorizations").ReplaceOneAsync(a => a.SellerId == auth.SellerId, auth, new ReplaceOptions { IsUpsert = true }, ct);
            return auth;
        });
        member.MapDelete("/me/proxy-authorization", async (ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await db.GetCollection<ProxyAuthorization>("proxyAuthorizations").DeleteOneAsync(a => a.SellerId == userId, ct);
            return Results.NoContent();
        });

        app.MapPost("/api/admin/listings/proxy", async (ProxyListingRequest req, ClaimsPrincipal p, ListingService svc, IMongoDatabase db,
            AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var ok = await db.GetCollection<ProxyAuthorization>("proxyAuthorizations").Find(a => a.SellerId == req.SellerId && a.ExpiresAt > now).AnyAsync(ct);
            if (!ok) throw DomainException.Forbidden("Người bán chưa ủy quyền đăng tin hộ hoặc ủy quyền đã hết hạn");
            var listing = await svc.CreateAsync(req.SellerId, req.Listing, submit: true, ct, proxyActor: p.ActorName());
            await audit.LogAsync(p, "listing.proxy_post", "listing", listing.Id, after: new { req.SellerId, listing.Title }, ct: ct);
            return listing;
        }).WithTags("Admin Listings").RequireAuthorization(Policies.ForPerm(Perm.ListingProxyPost));
    }
}

/// <summary>Tham số tìm kiếm trên query string. Bộ lọc động dạng attr.{key}=giá trị.</summary>
public record ListingQueryParams(string? Q, ListingType? Type, string? CategoryId, string? RootCategoryId, string? SpeciesId, string? ProvinceId,
    long? PriceMin, long? PriceMax, bool? GardenOnly, bool? EscrowOnly, bool? RealPhotoOnly, string? Pickup, int? PostedWithinDays,
    double? Lat, double? Lng, double? RadiusKm, ListingSort? Sort, int? Page, int? PageSize, string? SellerId, string? Use)
{
    public ListingQuery ToQuery(IQueryCollection query) => new(Q, Type, CategoryId, RootCategoryId, SpeciesId, ProvinceId, PriceMin, PriceMax,
        GardenOnly, EscrowOnly, RealPhotoOnly, Pickup, PostedWithinDays, Lat, Lng, RadiusKm, Sort ?? ListingSort.Newest, Page ?? 1, PageSize ?? 20,
        query.Where(kv => kv.Key.StartsWith("attr.")).ToDictionary(kv => kv.Key[5..], kv => kv.Value.ToString()), SellerId, Use);
}
