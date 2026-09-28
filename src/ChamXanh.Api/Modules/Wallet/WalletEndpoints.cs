using System.Security.Claims;
using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Wallet;

public record AdjustRequest(long Amount, LotKind Kind, string Reason);

public static class WalletEndpoints
{
    /// <summary>BR-ADM-02: điều chỉnh vượt ngưỡng cần người thứ hai duyệt.</summary>
    const long AdjustmentSingleApprovalLimit = 50;

    public static void MapWallet(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/wallet").WithTags("Wallet").RequireAuthorization(Policies.Member);

        me.MapGet("/", async (ClaimsPrincipal p, WalletService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var w = await svc.GetAsync(p.UserId(), ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var live = w.Lots.Where(l => l.Remaining > 0 && (l.ExpiresAt is null || l.ExpiresAt > now)).ToList();
            return new
            {
                balance = live.Sum(l => l.Remaining),
                paid = live.Where(l => l.Kind == LotKind.Paid).Sum(l => l.Remaining),
                bonus = live.Where(l => l.Kind == LotKind.Bonus).Sum(l => l.Remaining),
                expiringSoon = live.Where(l => l.ExpiresAt < now.AddDays(7)).Select(l => new { l.Remaining, l.ExpiresAt }),
                w.Frozen,
            };
        });
        me.MapGet("/ledger", async (int? limit, ClaimsPrincipal p, WalletService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await svc.Ledger.Find(e => e.UserId == userId).SortByDescending(e => e.CreatedAt).Limit(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        });
        me.MapPost("/topups", (CreateTopUpRequest req, ClaimsPrincipal p, TopUpService svc, CancellationToken ct) => svc.CreateAsync(p.UserId(), req, ct));
        me.MapGet("/topups/{id}", async (string id, ClaimsPrincipal p, TopUpService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await svc.TopUps.Find(t => t.Id == id && t.UserId == userId).FirstOrDefaultAsync(ct) is { } t ? Results.Ok(t) : Results.NotFound();
        });

        app.MapPost("/api/listings/{id}/promotions", async (string id, BuyPromotionRequest req, ClaimsPrincipal p, PromotionService svc, CancellationToken ct) =>
            Results.Ok(await svc.BuyAsync(id, p.UserId(), req, ct))).WithTags("Wallet").RequireAuthorization(Policies.Member);
        app.MapGet("/api/listings/{id}/promotions", async (string id, ClaimsPrincipal p, PromotionService svc, Listings.ListingService listings, CancellationToken ct) =>
        {
            await listings.GetOwnedAsync(id, p.UserId(), ct);
            return await svc.ForListingAsync(id, ct);
        }).WithTags("Wallet").RequireAuthorization(Policies.Member);

        // Webhook cổng thanh toán (không cần đăng nhập, xác thực bằng chữ ký HMAC trên nội dung thô).
        app.MapPost("/api/payments/webhook", async (HttpRequest request, TopUpService svc, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var raw = await reader.ReadToEndAsync(ct);
            var payload = JsonSerializer.Deserialize<PaymentWebhook>(raw, JsonSerializerOptions.Web)
                ?? throw new DomainException("BAD_REQUEST", "Payload rỗng", StatusCodes.Status400BadRequest);
            var t = await svc.HandleWebhookAsync(raw, request.Headers["X-Signature"].FirstOrDefault(), payload, ct);
            return Results.Ok(new { t.Id, t.Status });
        }).WithTags("Payments");

        var admin = app.MapGroup("/api/admin/wallets").WithTags("Admin Wallet").RequireAuthorization(Policies.ForPerm(Perm.WalletAdjust));
        admin.MapGet("/adjustments", async (string? status, IMongoDatabase db, CancellationToken ct) =>
            await db.GetCollection<AdjustmentRequest>("walletAdjustments").Find(x => x.Status == (status ?? "Pending"))
                .SortByDescending(x => x.CreatedAt).Limit(200).ToListAsync(ct));
        admin.MapGet("/{userId}", async (string userId, WalletService svc, CancellationToken ct) => new
        {
            wallet = await svc.GetAsync(userId, ct),
            ledger = await svc.Ledger.Find(e => e.UserId == userId).SortByDescending(e => e.CreatedAt).Limit(100).ToListAsync(ct),
        });
        admin.MapPost("/{userId}/adjustments", async (string userId, AdjustRequest req, ClaimsPrincipal p, IMongoDatabase db, IMongoClient client,
            WalletService svc, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (req.Amount == 0 || string.IsNullOrWhiteSpace(req.Reason)) throw new DomainException("INVALID_ADJUSTMENT", "Cần số Xu khác 0 và lý do");
            var r = new AdjustmentRequest
            {
                UserId = userId, Amount = req.Amount, Kind = req.Kind, Reason = req.Reason.Trim(), RequestedById = p.UserId(), CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            await db.GetCollection<AdjustmentRequest>("walletAdjustments").InsertOneAsync(r, cancellationToken: ct);
            await audit.LogAsync(p, "wallet.request_adjustment", "wallet", userId, after: r, reason: r.Reason, ct: ct);
            if (Math.Abs(req.Amount) <= AdjustmentSingleApprovalLimit) await ApplyAdjustmentAsync(r, p.UserId(), db, client, svc, clock, ct);
            return await db.GetCollection<AdjustmentRequest>("walletAdjustments").Find(x => x.Id == r.Id).FirstAsync(ct);
        });
        admin.MapPost("/adjustments/{id}/approve", async (string id, ClaimsPrincipal p, IMongoDatabase db, IMongoClient client,
            WalletService svc, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var coll = db.GetCollection<AdjustmentRequest>("walletAdjustments");
            var r = await coll.Find(x => x.Id == id && x.Status == "Pending").FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("yêu cầu điều chỉnh");
            if (r.RequestedById == p.UserId()) throw DomainException.Forbidden("Người đề xuất không được tự duyệt (maker-checker)");
            await ApplyAdjustmentAsync(r, p.UserId(), db, client, svc, clock, ct);
            await audit.LogAsync(p, "wallet.approve_adjustment", "wallet", r.UserId, after: new { r.Id, r.Amount }, ct: ct);
            return await coll.Find(x => x.Id == id).FirstAsync(ct);
        });
    }

    static async Task ApplyAdjustmentAsync(AdjustmentRequest r, string approverId, IMongoDatabase db, IMongoClient client, WalletService svc, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var s = await client.StartSessionAsync(cancellationToken: ct);
        await s.WithTransactionAsync(async (session, token) =>
        {
            if (r.Amount > 0)
                await svc.CreditAsync(session, r.UserId, r.Amount, r.Kind, r.Kind == LotKind.Bonus ? now.AddDays(90) : null,
                    LedgerType.Adjust, $"adjust:{r.Id}", "adjustment", r.Id, r.Reason, token);
            else
                await svc.DebitAsync(session, r.UserId, -r.Amount, $"adjust:{r.Id}", "adjustment", r.Id, r.Reason, token);
            await db.GetCollection<AdjustmentRequest>("walletAdjustments").UpdateOneAsync(session, x => x.Id == r.Id,
                Builders<AdjustmentRequest>.Update.Set(x => x.Status, "Applied").Set(x => x.ApprovedById, approverId), cancellationToken: token);
            return true;
        }, cancellationToken: ct);
    }
}

public class WalletWorker(IServiceScopeFactory scopes, ILogger<WalletWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        var tick = 0;
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PromotionService>().RunAutoBumpsAsync(stoppingToken);
                if (tick++ % 60 == 0)
                    await scope.ServiceProvider.GetRequiredService<WalletService>().ExpireLotsAsync(
                        scope.ServiceProvider.GetRequiredService<IMongoClient>(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi job ví / đẩy tự động");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
