using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Listings;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Gardens;

public static class GardenEndpoints
{
    static object Public(GardenProfile g, DateTime now) => new
    {
        g.Id, g.Slug, g.Name, Type = g.Type.ToString(), g.Description, g.Address, g.ProvinceId, g.WardId,
        lat = g.Location.Coordinates.Latitude, lng = g.Location.Coordinates.Longitude, g.OpeningHours, g.AllowVisit,
        photos = g.PhotoMediaIds.Select(id => $"/media/{id}/card.webp"), cover = g.CoverMediaId is null ? null : $"/media/{g.CoverMediaId}/full.webp",
        g.PinnedListingIds, g.OwnerId, verified = g.Status == VerificationStatus.Verified,
        tick = g.PlanActive(now), founding = g.IsFounding,
    };

    static object Owner(GardenProfile g, DateTime now) => new
    {
        profile = Public(g, now), status = g.Status.ToString(), g.ReviewNote, g.Plan, inGrace = g.InGrace(now),
        bank = g.Bank is null ? null : new { g.Bank.BankCode, g.Bank.AccountNoMasked, g.Bank.AccountName },
    };

    public static void MapGardens(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/gardens").WithTags("Gardens");

        // Bản đồ nhà vườn: chỉ vườn đã xác minh VÀ gói còn hạn (BR-MAP-01); lọc theo loài đang có tin (BR-MAP-04).
        pub.MapGet("/map", async (double lat, double lng, double? radiusKm, string? speciesId, GardenService svc, ListingService listings,
            TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var f = Builders<GardenProfile>.Filter;
            var filter = f.Eq(g => g.Status, VerificationStatus.Verified) & f.Gt("plan.endAt", now)
                & f.GeoWithinCenterSphere(g => g.Location, lng, lat, Math.Clamp(radiusKm ?? 30, 1, 300) / 6378.1);
            if (!string.IsNullOrWhiteSpace(speciesId))
            {
                var owners = await listings.Listings.Distinct(l => l.SellerId,
                    Builders<Listing>.Filter.Eq(l => l.SpeciesId, speciesId) & Builders<Listing>.Filter.Eq(l => l.Status, ListingStatus.Active), cancellationToken: ct).ToListAsync(ct);
                filter &= f.In(g => g.OwnerId, owners);
            }
            var gardens = await svc.Gardens.Find(filter).Limit(500).ToListAsync(ct);
            return gardens.Select(g => new
            {
                g.Slug, g.Name, Type = g.Type.ToString(), lat = g.Location.Coordinates.Latitude, lng = g.Location.Coordinates.Longitude,
                g.AllowVisit, g.OpeningHours, thumb = (g.CoverMediaId ?? g.PhotoMediaIds.FirstOrDefault()) is { } m ? $"/media/{m}/thumb.webp" : null,
            });
        });

        pub.MapGet("/{slug}", async (string slug, GardenService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var g = await svc.Gardens.Find(x => x.Slug == slug).FirstOrDefaultAsync(ct);
            if (g is null || g.Status != VerificationStatus.Verified) throw DomainException.NotFound("nhà vườn");
            var now = clock.GetUtcNow().UtcDateTime;
            // BR-PKG-02/03: hết gói (kể cả ân hạn) thì mất tick; quá ân hạn thì không còn gian hàng
            if (!g.PlanActive(now) && !g.InGrace(now)) throw DomainException.NotFound("gian hàng");
            return Public(g, now);
        });

        var me = app.MapGroup("/api/garden").WithTags("Gardens").RequireAuthorization(Policies.Member);
        me.MapGet("/", async (ClaimsPrincipal p, GardenService svc, TimeProvider clock, CancellationToken ct) =>
            await svc.FindByOwnerAsync(p.UserId(), ct) is { } g ? Results.Ok(Owner(g, clock.GetUtcNow().UtcDateTime)) : Results.NotFound());
        me.MapPost("/application", async (GardenApplication req, ClaimsPrincipal p, GardenService svc, KycCheckService kyc, TimeProvider clock, CancellationToken ct) =>
        {
            var g = await svc.ApplyAsync(p.UserId(), req, ct);
            await kyc.RunAsync(g, req, ct);
            return Owner(await svc.GetAsync(g.Id, ct), clock.GetUtcNow().UtcDateTime);
        });
        me.MapPut("/", async (GardenUpdate req, ClaimsPrincipal p, GardenService svc, TimeProvider clock, CancellationToken ct) =>
            Owner(await svc.UpdateAsync(p.UserId(), req, ct), clock.GetUtcNow().UtcDateTime));
        me.MapPost("/plan", async (BuyPlanRequest req, ClaimsPrincipal p, GardenService svc, TimeProvider clock, CancellationToken ct) =>
            Owner(await svc.BuyPlanAsync(p.UserId(), req, ct), clock.GetUtcNow().UtcDateTime));
        me.MapPost("/plan/auto-renew", async (bool enabled, ClaimsPrincipal p, GardenService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.Gardens.UpdateOneAsync(g => g.OwnerId == userId && g.Plan != null, Builders<GardenProfile>.Update.Set("plan.autoRenew", enabled), cancellationToken: ct);
            return Results.NoContent();
        });

        var admin = app.MapGroup("/api/admin/gardens").WithTags("Admin Gardens").RequireAuthorization(Policies.ForPerm(Perm.GardenVerify));
        admin.MapGet("/", async (VerificationStatus? status, GardenService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var list = await svc.Gardens.Find(g => g.Status == (status ?? VerificationStatus.Submitted)).SortBy(g => g.UpdatedAt).Limit(200).ToListAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            return list.Select(g => Owner(g, now));
        });
        // Xem dữ liệu CCCD đã giải mã: cần quyền kyc.view và luôn ghi audit (BR-AUTH-04).
        admin.MapGet("/{id}/kyc", async (string id, ClaimsPrincipal p, GardenService svc, DataProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (!p.HasClaim(Claims.Perm, Perm.KycView)) return Results.Forbid();
            var g = await svc.GetAsync(id, ct);
            await audit.LogAsync(p, "garden.view_kyc", "garden", id, ct: ct);
            return Results.Ok(new
            {
                idNumber = protector.Decrypt(g.Kyc.IdNumberEnc), fullName = protector.Decrypt(g.Kyc.FullNameEnc), dob = protector.Decrypt(g.Kyc.DobEnc),
                documents = g.Kyc.DocumentMediaIds.Select(m => $"/api/secure-media/{m}/full"), g.Method, g.BusinessLicenseNo,
                bankAccountNo = g.Bank is null ? null : protector.Decrypt(g.Bank.AccountNoEnc), bankAccountName = g.Bank?.AccountName,
            });
        });
        admin.MapPost("/{id}/review", async (string id, ReviewRequest req, ClaimsPrincipal p, GardenService svc, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var g = await svc.ReviewAsync(id, req, p.UserId(), ct);
            await audit.LogAsync(p, $"garden.{req.Decision.ToString().ToLowerInvariant()}", "garden", id, reason: req.Note, ct: ct);
            return Owner(g, clock.GetUtcNow().UtcDateTime);
        });
        admin.MapPost("/{id}/grant-plan", async (string id, GrantPlanRequest req, ClaimsPrincipal p, GardenService svc, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var g = await svc.GrantPlanAsync(id, req, ct);
            await audit.LogAsync(p, "garden.grant_plan", "garden", id, after: new { req.Months, req.Founding }, ct: ct);
            return Owner(g, clock.GetUtcNow().UtcDateTime);
        });
    }
}

public class GardenPlanWorker(IServiceScopeFactory scopes, ILogger<GardenPlanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<GardenService>().ProcessPlanExpiryAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi job gói Nhà vườn");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
