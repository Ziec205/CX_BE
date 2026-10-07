using System.Globalization;
using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Notifications;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Plans;

public class PlanService(IMongoDatabase db, IMongoClient client, IPlanPaymentGateway gateway, PayOsOptions options,
    NotificationService notifications, TimeProvider clock, ILogger<PlanService> logger)
{
    public IMongoCollection<Subscription> Subscriptions { get; } = db.GetCollection<Subscription>("subscriptions");
    public IMongoCollection<PlanPayment> Payments { get; } = db.GetCollection<PlanPayment>("planPayments");

    public Task EnsureIndexesAsync() => Payments.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<PlanPayment>(Builders<PlanPayment>.IndexKeys.Ascending(p => p.OrderCode), new CreateIndexOptions { Unique = true }),
        new CreateIndexModel<PlanPayment>(Builders<PlanPayment>.IndexKeys.Ascending(p => p.UserId).Descending(p => p.CreatedAt)),
        new CreateIndexModel<PlanPayment>(Builders<PlanPayment>.IndexKeys.Ascending(p => p.Status).Ascending(p => p.ExpiresAt)),
    ]);

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Subscription?> GetSubscriptionAsync(string userId, CancellationToken ct) =>
        await Subscriptions.Find(s => s.UserId == userId).FirstOrDefaultAsync(ct);

    /// <summary>Gói đang có hiệu lực; hết hạn thì về Miễn phí.</summary>
    public async Task<(PlanDefinition Plan, DateTime? EndAt)> GetEffectiveAsync(string userId, CancellationToken ct)
    {
        var s = await GetSubscriptionAsync(userId, ct);
        return s is not null && s.EndAt > Now ? (PlanCatalog.Get(s.Plan), s.EndAt) : (PlanCatalog.Free, null);
    }

    public async Task<PlanPayment> CheckoutAsync(string userId, CheckoutRequest req, CancellationToken ct)
    {
        if (req.Plan == PlanCode.Free) throw new DomainException("INVALID_PLAN", "Gói Miễn phí không cần thanh toán");
        if (!PlanCatalog.Terms.Contains(req.Months)) throw new DomainException("INVALID_TERM", "Chỉ có kỳ hạn 1 tháng hoặc 12 tháng");
        var plan = PlanCatalog.Get(req.Plan);
        var (current, endAt) = await GetEffectiveAsync(userId, ct);
        if (current.MonthlyVnd > plan.MonthlyVnd)
            throw DomainException.Conflict("PLAN_DOWNGRADE",
                $"Gói {current.Name} của bạn còn hạn đến {VnDate(endAt!.Value)}. Gia hạn {current.Name} hoặc đợi hết hạn rồi mua {plan.Name}.");

        var now = Now;
        // Bấm mua lại cùng gói khi link cũ còn hạn: dùng lại link, tránh tạo nhiều đơn treo.
        var open = await Payments.Find(p => p.UserId == userId && p.Status == PlanPaymentStatus.Pending && p.Plan == req.Plan
            && p.Months == req.Months && p.Gateway == gateway.Name && p.ExpiresAt > now.AddMinutes(2)).FirstOrDefaultAsync(ct);
        if (open is not null) return open;

        var payment = new PlanPayment
        {
            UserId = userId, Plan = req.Plan, Months = req.Months, AmountVnd = PlanCatalog.Price(plan, req.Months),
            OrderCode = NewOrderCode(now), Gateway = gateway.Name, CreatedAt = now, ExpiresAt = now.AddMinutes(options.LinkExpiryMinutes),
        };
        var web = options.ReturnBaseUrl.TrimEnd('/');
        // Tham số riêng "thanhToan": PayOS tự thêm id, code, status, orderCode vào URL quay về.
        var link = await gateway.CreateAsync(new PaymentLinkRequest(payment.OrderCode, payment.AmountVnd,
            $"CX{(req.Plan == PlanCode.Pro ? "PRO" : "PLUS")}{req.Months}", $"{plan.Name} {req.Months} tháng",
            $"{web}/goi/ket-qua?thanhToan={payment.Id}", $"{web}/goi/ket-qua?thanhToan={payment.Id}&huy=1", payment.ExpiresAt), ct);
        payment.PaymentLinkId = link.PaymentLinkId;
        payment.CheckoutUrl = link.CheckoutUrl;
        await Payments.InsertOneAsync(payment, cancellationToken: ct);
        return payment;
    }

    /// <summary>Số nguyên ≤ 2^53 (giới hạn của PayOS), gần như không trùng; trùng thì index unique chặn lại.</summary>
    static long NewOrderCode(DateTime now) =>
        new DateTimeOffset(now).ToUnixTimeMilliseconds() * 100 + Random.Shared.Next(100);

    public async Task<PlanPayment> GetOwnPaymentAsync(string userId, string id, CancellationToken ct) =>
        (MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Payments.Find(p => p.Id == id && p.UserId == userId).FirstOrDefaultAsync(ct) : null)
        ?? throw DomainException.NotFound("đơn thanh toán");

    /// <summary>Người dùng quay về từ PayOS: hỏi trạng thái trực tiếp từ PayOS (không chờ webhook) rồi kích hoạt nếu đã trả.</summary>
    public async Task<PlanPayment> SyncAsync(PlanPayment p, CancellationToken ct)
    {
        if (p.Status != PlanPaymentStatus.Pending || gateway.IsSimulator || p.Gateway != gateway.Name) return p;
        GatewayStatus? status;
        try { status = await gateway.GetAsync(p.OrderCode, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DomainException or JsonException)
        {
            logger.LogWarning(ex, "Không hỏi được trạng thái PayOS cho đơn {OrderCode}", p.OrderCode);
            return p;
        }
        switch (status?.State)
        {
            case GatewayState.Paid when status.AmountPaid >= p.AmountVnd:
                return await ActivateAsync(p.Id, status.Reference ?? $"PAYOS-{p.OrderCode}", ct);
            case GatewayState.Cancelled or GatewayState.Expired:
                var to = status.State == GatewayState.Cancelled ? PlanPaymentStatus.Cancelled : PlanPaymentStatus.Expired;
                await Payments.UpdateOneAsync(x => x.Id == p.Id && x.Status == PlanPaymentStatus.Pending, Builders<PlanPayment>.Update.Set(x => x.Status, to), cancellationToken: ct);
                return await Payments.Find(x => x.Id == p.Id).FirstAsync(ct);
            default:
                return p;
        }
    }

    public async Task<PlanPayment> CancelAsync(PlanPayment p, CancellationToken ct)
    {
        if (p.Status != PlanPaymentStatus.Pending) return p;
        try { await gateway.CancelAsync(p.OrderCode, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DomainException or JsonException)
        {
            logger.LogWarning(ex, "Không hủy được link PayOS {OrderCode}", p.OrderCode);
        }
        await Payments.UpdateOneAsync(x => x.Id == p.Id && x.Status == PlanPaymentStatus.Pending,
            Builders<PlanPayment>.Update.Set(x => x.Status, PlanPaymentStatus.Cancelled), cancellationToken: ct);
        return await Payments.Find(x => x.Id == p.Id).FirstAsync(ct);
    }

    /// <summary>Webhook PayOS. Trả true khi đã xử lý (kể cả đơn không có trong hệ thống — PayOS gửi đơn thử khi xác nhận URL webhook).</summary>
    public async Task HandleWebhookAsync(JsonElement body, CancellationToken ct)
    {
        if (!options.IsConfigured) throw DomainException.NotFound("webhook");
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !PayOsSignature.Verify(options.ChecksumKey, data, body.TryGetProperty("signature", out var sig) ? sig.GetString() : null))
            throw new DomainException("INVALID_SIGNATURE", "Chữ ký webhook không hợp lệ", StatusCodes.Status401Unauthorized);

        if (!data.TryGetProperty("orderCode", out var oc) || !oc.TryGetInt64(out var orderCode)) return;
        var payment = await Payments.Find(p => p.OrderCode == orderCode).FirstOrDefaultAsync(ct);
        if (payment is null) return;
        var success = (data.TryGetProperty("code", out var code) ? code.GetString() : body.GetProperty("code").GetString()) == "00";
        var amount = data.TryGetProperty("amount", out var a) && a.TryGetInt64(out var n) ? n : 0;
        if (!success) return;
        if (amount != payment.AmountVnd)
        {
            logger.LogError("PayOS báo số tiền {Amount} không khớp đơn {OrderCode} ({Expected}) — cần đối soát", amount, orderCode, payment.AmountVnd);
            return;
        }
        await ActivateAsync(payment.Id, data.TryGetProperty("reference", out var r) ? r.GetString() : null, ct);
    }

    /// <summary>Đánh dấu đã trả và cộng gói, một lần duy nhất (webhook gửi lại hay vừa webhook vừa đồng bộ đều an toàn).
    /// Tiền về sau khi link hết hạn/bị hủy vẫn được kích hoạt vì người dùng đã trả.</summary>
    public async Task<PlanPayment> ActivateAsync(string paymentId, string? gatewayRef, CancellationToken ct)
    {
        var now = Now;
        PlanPayment? applied = null;
        using (var s = await client.StartSessionAsync(cancellationToken: ct))
        {
            await s.WithTransactionAsync(async (session, token) =>
            {
                var payment = await Payments.FindOneAndUpdateAsync(session,
                    Builders<PlanPayment>.Filter.Where(p => p.Id == paymentId && p.Status != PlanPaymentStatus.Paid),
                    Builders<PlanPayment>.Update.Set(p => p.Status, PlanPaymentStatus.Paid).Set(p => p.PaidAt, now).Set(p => p.GatewayRef, gatewayRef),
                    new FindOneAndUpdateOptions<PlanPayment> { ReturnDocument = ReturnDocument.After }, token);
                if (payment is null) return false;

                var current = await Subscriptions.Find(session, x => x.UserId == payment.UserId).FirstOrDefaultAsync(token);
                var (plan, start, end) = PlanCatalog.Apply(current, payment.Plan, payment.Months, now);
                await Subscriptions.UpdateOneAsync(session, x => x.UserId == payment.UserId, Builders<Subscription>.Update
                    .Set(x => x.Plan, plan).Set(x => x.StartAt, start).Set(x => x.EndAt, end).Set(x => x.UpdatedAt, now),
                    new UpdateOptions { IsUpsert = true }, token);
                await Payments.UpdateOneAsync(session, p => p.Id == payment.Id,
                    Builders<PlanPayment>.Update.Set(p => p.AppliedPlan, plan).Set(p => p.AppliedEndAt, end), cancellationToken: token);
                payment.AppliedPlan = plan;
                payment.AppliedEndAt = end;
                applied = payment;
                return true;
            }, cancellationToken: ct);
        }
        if (applied is not null)
        {
            var name = PlanCatalog.Get(applied.AppliedPlan!.Value).Name;
            await notifications.SendAsync(applied.UserId, "plan.activated", $"Đã kích hoạt gói {name}",
                $"Gói có hiệu lực đến {VnDate(applied.AppliedEndAt!.Value)}. Cảm ơn bạn đã ủng hộ Chạm Xanh!", "/goi", ct);
        }
        return await Payments.Find(p => p.Id == paymentId).FirstAsync(ct);
    }

    /// <summary>Nhắc trước 3 ngày khi sắp hết hạn, báo khi đã hết hạn; đơn treo quá hạn chuyển sang Expired.</summary>
    public async Task RunMaintenanceAsync(CancellationToken ct)
    {
        var now = Now;
        var soon = await Subscriptions.Find(s => s.Plan != PlanCode.Free && s.EndAt > now && s.EndAt <= now.AddDays(3)
            && (s.RemindedEndAt == null || s.RemindedEndAt != s.EndAt)).Limit(500).ToListAsync(ct);
        foreach (var s in soon)
        {
            var claimed = await Subscriptions.UpdateOneAsync(x => x.UserId == s.UserId && x.EndAt == s.EndAt && x.RemindedEndAt == s.RemindedEndAt,
                Builders<Subscription>.Update.Set(x => x.RemindedEndAt, s.EndAt), cancellationToken: ct);
            if (claimed.ModifiedCount == 0) continue;
            var name = PlanCatalog.Get(s.Plan).Name;
            await notifications.SendAsync(s.UserId, "plan.expiring", $"Gói {name} sắp hết hạn",
                $"Gói hết hạn ngày {VnDate(s.EndAt)}. Gia hạn để giữ cây trong Hồ sơ vườn và lượt dùng AI.", "/goi", ct);
        }

        var ended = await Subscriptions.Find(s => s.Plan != PlanCode.Free && s.EndAt <= now && s.EndAt > now.AddDays(-2)
            && (s.ExpiredNoticeEndAt == null || s.ExpiredNoticeEndAt != s.EndAt)).Limit(500).ToListAsync(ct);
        foreach (var s in ended)
        {
            var claimed = await Subscriptions.UpdateOneAsync(x => x.UserId == s.UserId && x.EndAt == s.EndAt && x.ExpiredNoticeEndAt == s.ExpiredNoticeEndAt,
                Builders<Subscription>.Update.Set(x => x.ExpiredNoticeEndAt, s.EndAt), cancellationToken: ct);
            if (claimed.ModifiedCount == 0) continue;
            await notifications.SendAsync(s.UserId, "plan.expired", $"Gói {PlanCatalog.Get(s.Plan).Name} đã hết hạn",
                $"Hồ sơ vườn giữ {PlanCatalog.Free.GardenPlants} cây mới nhất; các cây khác tạm khóa và tắt nhắc lịch đến khi bạn gia hạn.", "/goi", ct);
        }

        await Payments.UpdateManyAsync(p => p.Status == PlanPaymentStatus.Pending && p.ExpiresAt < now.AddHours(-1),
            Builders<PlanPayment>.Update.Set(p => p.Status, PlanPaymentStatus.Expired), cancellationToken: ct);
    }

    public static string VnDate(DateTime utc) => utc.AddHours(7).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}

public class PlanWorker(IServiceScopeFactory scopes, ILogger<PlanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PlanService>().RunMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi job gói sử dụng");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
