using System.Security.Claims;
using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Media;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Escrow;

public static class EscrowEndpoints
{
    public static void MapEscrow(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/escrow").WithTags("Escrow").RequireAuthorization(Policies.Member);

        g.MapPost("/orders", (CreateOrderRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.CreateFromListingAsync(p.UserId(), req, ct));
        g.MapPost("/orders/from-offer", (CreateFromOfferRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.CreateFromOfferAsync(p.UserId(), req, ct));
        g.MapGet("/orders", async (string? role, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var f = role == "seller" ? Builders<EscrowOrder>.Filter.Eq(o => o.SellerId, userId)
                : role == "buyer" ? Builders<EscrowOrder>.Filter.Eq(o => o.BuyerId, userId)
                : Builders<EscrowOrder>.Filter.Where(o => o.BuyerId == userId || o.SellerId == userId);
            var list = await svc.Orders.Find(f).SortByDescending(o => o.CreatedAt).Limit(100).ToListAsync(ct);
            return list.Select(o => ToDto(o, userId));
        });
        g.MapGet("/orders/{id}", async (string id, ClaimsPrincipal p, EscrowService svc, MediaService media, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var o = await svc.GetForPartyAsync(id, userId, ct);
            return await ToDetailAsync(o, userId, media, ct);
        });
        // Mã QR nhận tại vườn: chỉ người mua xem được, người bán quét trên máy người mua.
        g.MapGet("/orders/{id}/pickup-code", async (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) =>
        {
            var o = await svc.GetForPartyAsync(id, p.UserId(), ct);
            if (o.BuyerId != p.UserId()) throw DomainException.Forbidden("Chỉ người mua xem được mã nhận hàng");
            return new { code = o.PickupCode };
        });
        g.MapPost("/orders/{id}/pay", (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.StartPaymentAsync(p.UserId(), id, ct));
        g.MapPost("/orders/{id}/confirm", (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.SellerConfirmAsync(p.UserId(), id, ct));
        g.MapPost("/orders/{id}/ship", (string id, ShipRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.ShipAsync(p.UserId(), id, req, ct));
        g.MapPost("/orders/{id}/delivered", (string id, DeliverRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.MarkDeliveredBySellerAsync(p.UserId(), id, req, ct));
        g.MapPost("/orders/{id}/pickup", (string id, PickupRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.ConfirmPickupAsync(p.UserId(), id, req, ct));
        g.MapPost("/orders/{id}/received", (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.BuyerReceivedAsync(p.UserId(), id, ct));
        g.MapPost("/orders/{id}/accept", (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.BuyerAcceptAsync(p.UserId(), id, ct));
        g.MapPost("/orders/{id}/cancel", (string id, CancelRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.CancelAsync(p.UserId(), id, req, ct));
        g.MapPost("/orders/{id}/dispute", (string id, OpenDisputeRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.OpenDisputeAsync(p.UserId(), id, req, ct));
        g.MapPost("/orders/{id}/dispute/respond", (string id, DisputeResponseRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.RespondDisputeAsync(p.UserId(), id, req, ct));
        g.MapPost("/orders/{id}/dispute/accept-proposal", (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.BuyerAcceptProposalAsync(p.UserId(), id, ct));
        g.MapPost("/orders/{id}/dispute/withdraw", (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.WithdrawDisputeAsync(p.UserId(), id, ct));
        g.MapPost("/orders/{id}/return", (string id, ReturnShipRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.ShipReturnAsync(p.UserId(), id, req, ct));
        g.MapPost("/orders/{id}/return/received", async (string id, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) =>
        {
            var o = await svc.GetAsync(id, ct);
            if (o.SellerId != p.UserId()) throw DomainException.Forbidden("Chỉ người bán xác nhận đã nhận lại cây");
            return await svc.ConfirmReturnReceivedAsync(p.UserId(), o, ct);
        });
        g.MapPost("/orders/{id}/review", (string id, OrderReviewRequest req, ClaimsPrincipal p, EscrowService svc, CancellationToken ct) => svc.ReviewAsync(p.UserId(), id, req, ct));

        // Webhook đối tác trung gian thanh toán (chữ ký HMAC trên nội dung thô).
        app.MapPost("/api/escrow/webhook", async (HttpRequest request, EscrowService svc, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var raw = await reader.ReadToEndAsync(ct);
            var payload = JsonSerializer.Deserialize<EscrowWebhook>(raw, JsonSerializerOptions.Web)
                          ?? throw new DomainException("INVALID_PAYLOAD", "Payload không hợp lệ");
            var o = await svc.HandleWebhookAsync(raw, request.Headers["X-Signature"].FirstOrDefault(), payload, ct);
            return Results.Ok(new { o.Id, o.Status });
        }).WithTags("Escrow");

        // ---- Quản trị: CSKH phân xử (UC-ESC-07), kế toán quyết toán (UC-ESC-08) ----
        var admin = app.MapGroup("/api/admin/escrow").WithTags("Admin Escrow");
        admin.MapGet("/orders", async (OrderStatus? status, string? q, EscrowService svc, CancellationToken ct) =>
        {
            var f = Builders<EscrowOrder>.Filter.Empty;
            if (status is { } s) f &= Builders<EscrowOrder>.Filter.Eq(o => o.Status, s);
            if (!string.IsNullOrWhiteSpace(q)) f &= Builders<EscrowOrder>.Filter.Eq(o => o.Code, q.Trim().ToUpperInvariant());
            return await svc.Orders.Find(f).SortByDescending(o => o.UpdatedAt).Limit(200).ToListAsync(ct);
        }).RequireAuthorization(Policies.ForPerm(Perm.ReportsView));
        admin.MapGet("/orders/{id}", async (string id, EscrowService svc, MediaService media, CancellationToken ct) =>
            await ToDetailAsync(await svc.GetAsync(id, ct), null, media, ct)).RequireAuthorization(Policies.ForPerm(Perm.ReportsView));
        admin.MapPost("/orders/{id}/resolve", async (string id, ResolveDisputeRequest req, ClaimsPrincipal p, EscrowService svc, AuditService audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Note)) throw new DomainException("NOTE_REQUIRED", "Nhập lý do phân xử");
            var o = await svc.ResolveAsync(await svc.GetAsync(id, ct), req.Outcome, req.RefundAmount, req.Note.Trim(), p.UserId(), sellerLost: false, ct);
            await audit.LogAsync(p, "escrow.resolve", "escrowOrder", id, after: new { req.Outcome, req.RefundAmount }, reason: req.Note, ct: ct);
            return o;
        }).RequireAuthorization(Policies.ForPerm(Perm.DisputeResolve));
        admin.MapGet("/payouts", async (EscrowService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return await svc.Orders.Find(o => (o.Status == OrderStatus.Completed || o.Status == OrderStatus.PartiallyRefunded) && o.PayoutEligibleAt <= now)
                .SortBy(o => o.PayoutEligibleAt).Limit(200).ToListAsync(ct);
        }).RequireAuthorization(Policies.ForPerm(Perm.PayoutApprove));
        admin.MapPost("/orders/{id}/payout", async (string id, ClaimsPrincipal p, EscrowService svc, AuditService audit, CancellationToken ct) =>
        {
            var o = await svc.ApprovePayoutAsync(p.UserId(), id, ct);
            await audit.LogAsync(p, "escrow.payout", "escrowOrder", id, after: new { o.PayoutAmount, o.PayoutRef }, ct: ct);
            return o;
        }).RequireAuthorization(Policies.ForPerm(Perm.PayoutApprove));
        admin.MapPost("/orders/{id}/hold", async (string id, bool hold, ClaimsPrincipal p, EscrowService svc, AuditService audit, CancellationToken ct) =>
        {
            var o = await svc.SetHoldAsync(id, hold, ct);
            await audit.LogAsync(p, hold ? "escrow.hold" : "escrow.unhold", "escrowOrder", id, ct: ct);
            return o;
        }).RequireAuthorization(Policies.ForPerm(Perm.DisputeResolve));
    }

    static object ToDto(EscrowOrder o, string userId) => new
    {
        o.Id, o.Code, o.ListingId, o.ListingTitle, thumbUrl = o.ThumbMediaId is null ? null : $"/media/{o.ThumbMediaId}/thumb.webp",
        role = o.BuyerId == userId ? "buyer" : "seller", o.Status, o.Total, o.Quantity, o.Delivery, o.CreatedAt, o.UpdatedAt,
    };

    static async Task<object> ToDetailAsync(EscrowOrder o, string? userId, MediaService media, CancellationToken ct)
    {
        var isBuyer = userId == o.BuyerId;
        var ids = o.DeliveryProofMediaIds.Concat(o.Shipment?.MediaIds ?? []).ToList();
        var items = await media.Items.Find(m => ids.Contains(m.Id)).ToListAsync(ct);
        return new
        {
            order = o,
            role = userId is null ? "admin" : isBuyer ? "buyer" : "seller",
            payout = EscrowService.SellerPayout(o, o.RefundAmount),
            photos = items.Select(m => MediaService.ToDto(m)),
        };
    }

}
