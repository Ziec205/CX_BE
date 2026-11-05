using System.Security.Claims;
using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Ai;
using ChamXanh.Api.Modules.PlantCare;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Plans;

public static class PlanEndpoints
{
    static object PlanDto(PlanDefinition p) => new
    {
        code = p.Code.ToString(), p.Name, p.Tagline, p.MonthlyVnd, p.YearlyVnd,
        p.GardenPlants, p.AiPerDay, p.MarketCompare, p.SellerAi, p.ListingsPerMonth, p.ExtraListingXu, p.ListingDays,
    };

    static object PaymentDto(PlanPayment p) => new
    {
        p.Id, plan = p.Plan.ToString(), p.Months, p.AmountVnd, p.OrderCode, p.Gateway, p.CheckoutUrl, p.QrCode, status = p.Status.ToString(),
        p.CreatedAt, p.ExpiresAt, p.PaidAt, appliedPlan = p.AppliedPlan?.ToString(), p.AppliedEndAt,
    };

    public static void MapPlans(this IEndpointRouteBuilder app)
    {
        // Đối soát: xem mọi đơn mua gói, chỉ đọc.
        app.MapGet("/api/admin/plans/payments", async (PlanPaymentStatus? status, PlanCode? plan, string? q, int? page, PlanService plans, ChamXanh.Api.Modules.Identity.UserService users, CancellationToken ct) =>
        {
            var b = Builders<PlanPayment>.Filter;
            var f = b.Empty;
            if (status is { } st) f &= b.Eq(x => x.Status, st);
            if (plan is { } pl) f &= b.Eq(x => x.Plan, pl);
            var term = q?.Trim();
            if (!string.IsNullOrEmpty(term))
            {
                if (long.TryParse(term, out var order)) f &= b.Eq(x => x.OrderCode, order);
                else
                {
                    var byId = MongoDB.Bson.ObjectId.TryParse(term, out _);
                    var lower = term.ToLowerInvariant();
                    var u = await users.Users.Find(x => x.Username == lower || x.Username == term || (byId && x.Id == term)).FirstOrDefaultAsync(ct);
                    f &= b.Eq(x => x.UserId, u?.Id ?? "");
                }
            }
            const int size = 20;
            var pageNo = Math.Max(1, page ?? 1);
            var list = await plans.Payments.Find(f).SortByDescending(x => x.CreatedAt).Skip((pageNo - 1) * size).Limit(size).ToListAsync(ct);
            var total = await plans.Payments.CountDocumentsAsync(f, cancellationToken: ct);
            var ids = list.Select(x => x.UserId).Distinct().ToList();
            var names = await users.Users.Find(u => ids.Contains(u.Id)).Project(u => new { u.Id, u.DisplayName }).ToListAsync(ct);
            return new
            {
                items = list.Select(x => new
                {
                    x.Id, x.UserId, userName = names.FirstOrDefault(n => n.Id == x.UserId)?.DisplayName, plan = x.Plan.ToString(), x.Months, x.AmountVnd, x.OrderCode, x.Gateway,
                    status = x.Status.ToString(), x.CreatedAt, x.PaidAt, x.AppliedEndAt,
                }),
                total, page = pageNo, pageSize = size,
            };
        }).WithTags("Admin Plans").RequireAuthorization(ChamXanh.Api.Common.Auth.Policies.ForPerm(ChamXanh.Api.Common.Auth.Perm.ReportsView));

        app.MapGet("/api/plans", (IPlanPaymentGateway gateway) => new
        {
            plans = PlanCatalog.All.Select(PlanDto), terms = PlanCatalog.Terms, simulator = gateway.IsSimulator,
        }).WithTags("Plans");

        // Link quay về từ PayOS: xem / hủy đơn bằng mã đơn + mã số đơn, không cần phiên đăng nhập.
        app.MapGet("/api/plans/payments/{id}/status", async (string id, long ma, PlanService plans, CancellationToken ct) =>
            PaymentDto(await plans.SyncAsync(await plans.GetByLinkAsync(id, ma, ct), ct))).WithTags("Plans");
        app.MapPost("/api/plans/payments/{id}/status/cancel", async (string id, long ma, PlanService plans, CancellationToken ct) =>
            PaymentDto(await plans.CancelAsync(await plans.GetByLinkAsync(id, ma, ct), ct))).WithTags("Plans");

        var me = app.MapGroup("/api").WithTags("Plans").RequireAuthorization(Policies.Member);

        me.MapGet("/me/plan", async (ClaimsPrincipal p, PlanService plans, AiQuotaService quota, PlantCareService care, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var (plan, endAt) = await plans.GetEffectiveAsync(userId, ct);
            var ai = await quota.GetAsync(userId, ct);
            var plants = await care.Plants.CountDocumentsAsync(x => x.UserId == userId, cancellationToken: ct);
            return new
            {
                plan = PlanDto(plan), endAt,
                usage = new { gardenPlants = plants, aiToday = ai.Used, aiRemaining = ai.Remaining },
            };
        });
        me.MapGet("/me/plan/payments", async (ClaimsPrincipal p, PlanService plans, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var list = await plans.Payments.Find(x => x.UserId == userId).SortByDescending(x => x.CreatedAt).Limit(50).ToListAsync(ct);
            return list.Select(PaymentDto);
        });
        me.MapPost("/plans/checkout", async (CheckoutRequest req, ClaimsPrincipal p, PlanService plans, CancellationToken ct) =>
            PaymentDto(await plans.CheckoutAsync(p.UserId(), req, ct)));
        // Trang kết quả gọi lại để biết đã thanh toán chưa; BE tự hỏi PayOS nếu webhook chưa tới.
        me.MapGet("/plans/payments/{id}", async (string id, ClaimsPrincipal p, PlanService plans, CancellationToken ct) =>
            PaymentDto(await plans.SyncAsync(await plans.GetOwnPaymentAsync(p.UserId(), id, ct), ct)));
        me.MapGet("/plans/payments/order/{orderCode:long}", async (long orderCode, ClaimsPrincipal p, PlanService plans, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await plans.Payments.Find(x => x.OrderCode == orderCode && x.UserId == userId).FirstOrDefaultAsync(ct) is { } pay
                ? Results.Ok(PaymentDto(pay)) : throw DomainException.NotFound("đơn thanh toán");
        });
        me.MapPost("/plans/payments/{id}/cancel", async (string id, ClaimsPrincipal p, PlanService plans, CancellationToken ct) =>
            PaymentDto(await plans.CancelAsync(await plans.GetOwnPaymentAsync(p.UserId(), id, ct), ct)));
        // Chỉ có khi chưa cấu hình PayOS và bật AllowSimulator (phát triển/demo).
        me.MapPost("/plans/payments/{id}/simulate", async (string id, ClaimsPrincipal p, PlanService plans, IPlanPaymentGateway gateway,
            PayOsOptions options, CancellationToken ct) =>
        {
            if (!gateway.IsSimulator || !options.AllowSimulator) throw DomainException.NotFound("chế độ thanh toán thử");
            var pay = await plans.GetOwnPaymentAsync(p.UserId(), id, ct);
            if (pay.Gateway != gateway.Name) throw DomainException.NotFound("đơn thanh toán thử");
            if (pay.Status == PlanPaymentStatus.Cancelled) throw new DomainException("PAYMENT_CANCELLED", "Đơn đã hủy, hãy tạo đơn mới");
            return PaymentDto(await plans.ActivateAsync(pay.Id, $"SIM-{pay.OrderCode}", ct));
        });

        // Webhook PayOS: xác thực bằng chữ ký HMAC trên "data" với Checksum Key. Đặt URL này trong kênh thanh toán PayOS.
        app.MapPost("/api/payments/payos/webhook", async (HttpRequest request, PlanService plans, CancellationToken ct) =>
        {
            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct); }
            catch (JsonException) { throw new DomainException("BAD_REQUEST", "Payload không phải JSON", StatusCodes.Status400BadRequest); }
            using (doc) await plans.HandleWebhookAsync(doc.RootElement, ct);
            return Results.Ok(new { success = true });
        }).WithTags("Payments");
    }
}
