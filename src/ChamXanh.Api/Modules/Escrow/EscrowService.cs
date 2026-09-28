using System.Security.Cryptography;
using System.Text;
using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Gardens;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Messaging;
using ChamXanh.Api.Modules.Moderation;
using ChamXanh.Api.Modules.Notifications;
using ChamXanh.Api.Modules.Platform;
using ChamXanh.Api.Modules.Pricing;
using ChamXanh.Api.Modules.Reviews;
using ChamXanh.Api.Modules.Wallet;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Escrow;

public class EscrowService(
    IMongoDatabase db, TimeProvider clock, EscrowOptions options, IEscrowGateway gateway, FeatureFlagService flags,
    ListingService listings, UserService users, GardenService gardens, PricingService pricing, MediaService media,
    ChatService chat, NotificationService notifications, ModerationService moderation, ReviewService reviews,
    DataProtector protector, PaymentOptions paymentOptions)
{
    public IMongoCollection<EscrowOrder> Orders { get; } = db.GetCollection<EscrowOrder>("escrowOrders");
    DateTime Now => clock.GetUtcNow().UtcDateTime;

    static readonly OrderStatus[] Open =
        [OrderStatus.AwaitingPayment, OrderStatus.AwaitingSellerConfirm, OrderStatus.Paid, OrderStatus.Shipping, OrderStatus.Delivered, OrderStatus.Disputed, OrderStatus.AwaitingReturn];

    public async Task EnsureIndexesAsync()
    {
        await Orders.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<EscrowOrder>(Builders<EscrowOrder>.IndexKeys.Ascending(o => o.Code), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<EscrowOrder>(Builders<EscrowOrder>.IndexKeys.Ascending(o => o.BuyerId).Descending(o => o.CreatedAt)),
            new CreateIndexModel<EscrowOrder>(Builders<EscrowOrder>.IndexKeys.Ascending(o => o.SellerId).Descending(o => o.CreatedAt)),
            new CreateIndexModel<EscrowOrder>(Builders<EscrowOrder>.IndexKeys.Ascending(o => o.Status).Ascending(o => o.UpdatedAt)),
            new CreateIndexModel<EscrowOrder>(Builders<EscrowOrder>.IndexKeys.Ascending(o => o.GatewayRef)),
        ]);
    }

    public async Task<EscrowOrder> GetAsync(string id, CancellationToken ct) =>
        (MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Orders.Find(o => o.Id == id).FirstOrDefaultAsync(ct) : null)
        ?? throw DomainException.NotFound("đơn đảm bảo");

    public async Task<EscrowOrder> GetForPartyAsync(string id, string userId, CancellationToken ct)
    {
        var o = await GetAsync(id, ct);
        if (o.BuyerId != userId && o.SellerId != userId) throw DomainException.NotFound("đơn đảm bảo");
        return o;
    }

    // ---------- Tạo đơn ----------

    /// <summary>BR-ESC-01, BR-ESC-19, BR-DSP-07: người bán đủ điều kiện nhận Giao dịch đảm bảo.</summary>
    public async Task RequireSellerEligibleAsync(string sellerId, CancellationToken ct)
    {
        var seller = await users.GetAsync(sellerId, ct);
        if (seller.Status is not (UserStatus.Active or UserStatus.Restricted) || !seller.Flags.HasVerifiedGarden)
            throw new DomainException("SELLER_NOT_ELIGIBLE", "Người bán chưa đủ điều kiện nhận Giao dịch đảm bảo");
        var garden = await gardens.FindByOwnerAsync(sellerId, ct);
        if (garden?.Bank is null) throw new DomainException("SELLER_NO_BANK", "Người bán chưa liên kết tài khoản ngân hàng nhận tiền");
        var cancels = await Orders.CountDocumentsAsync(o => o.SellerId == sellerId && o.CancelledBySeller && o.UpdatedAt > Now.AddDays(-30), cancellationToken: ct);
        var lost = await Orders.CountDocumentsAsync(o => o.SellerId == sellerId && o.Dispute != null && o.Dispute.SellerLost && o.Dispute.ResolvedAt > Now.AddDays(-90), cancellationToken: ct);
        if (cancels >= 2 || lost >= 3)
            throw new DomainException("ESCROW_SUSPENDED", "Quyền Giao dịch đảm bảo của người bán đang tạm khóa 30 ngày");
    }

    public async Task<EscrowOrder> CreateFromListingAsync(string buyerId, CreateOrderRequest req, CancellationToken ct)
    {
        await flags.RequireAsync(Flags.Escrow, ct);
        var l = await listings.GetAsync(req.ListingId, ct);
        if (!l.EscrowEnabled || l.Type != ListingType.Sell || l.Status != ListingStatus.Active)
            throw new DomainException("ESCROW_NOT_AVAILABLE", "Tin này không nhận Giao dịch đảm bảo");
        if (l.PriceMode != PriceMode.Fixed || l.Price is null)
            throw new DomainException("OFFER_REQUIRED", "Tin giá thỏa thuận: hãy gửi Đề nghị giá trong chat, người bán sẽ tạo đơn");
        return await CreateAsync(l, buyerId, l.SellerId, buyerId, OrderSource.Listing, null, l.Price.Value,
            req.Quantity ?? 1, req.Delivery, req.ShippingFee, req.DeliveryAddress, req.PickupDate, req.ShipWithinDays, ct);
    }

    /// <summary>BR-ESC-02(b): người bán tạo đơn từ thẻ Đề nghị giá đã chấp nhận trong chat.</summary>
    public async Task<EscrowOrder> CreateFromOfferAsync(string sellerId, CreateFromOfferRequest req, CancellationToken ct)
    {
        await flags.RequireAsync(Flags.Escrow, ct);
        var msg = await chat.Messages.Find(m => m.Id == req.OfferMessageId && m.Type == MessageType.Offer).FirstOrDefaultAsync(ct)
                  ?? throw DomainException.NotFound("đề nghị giá");
        if (msg.Offer?.Status != OfferStatus.Accepted) throw new DomainException("OFFER_NOT_ACCEPTED", "Đề nghị giá chưa được chấp nhận");
        var conv = await chat.Conversations.Find(c => c.Id == msg.ConversationId).FirstAsync(ct);
        if (conv.SellerId != sellerId) throw DomainException.Forbidden("Chỉ người bán của tin mới tạo được đơn");
        if (await Orders.Find(o => o.OfferMessageId == msg.Id && o.Status != OrderStatus.Cancelled).AnyAsync(ct))
            throw DomainException.Conflict("ORDER_EXISTS", "Đề nghị giá này đã có đơn");
        var l = await listings.GetAsync(conv.ListingId, ct);
        if (!l.EscrowEnabled || l.Type != ListingType.Sell || l.Status != ListingStatus.Active)
            throw new DomainException("ESCROW_NOT_AVAILABLE", "Tin này không nhận Giao dịch đảm bảo");
        return await CreateAsync(l, conv.BuyerId, sellerId, sellerId, OrderSource.Offer, msg.Id, msg.Offer.Amount,
            req.Quantity ?? 1, req.Delivery, req.ShippingFee, req.DeliveryAddress, req.PickupDate, req.ShipWithinDays, ct);
    }

    /// <summary>BR-ESC-02(c): người đăng tin Cần mua tạo đơn từ báo giá đã chọn của Nhà vườn/Shop đã xác minh.</summary>
    public async Task<EscrowOrder> CreateFromQuoteAsync(Listing buyListing, string quoterId, string quoteId, long unitPrice, int qty,
        DeliveryMethod delivery, long? shippingFee, string? address, DateTime? pickupDate, int? shipDays, CancellationToken ct)
    {
        await flags.RequireAsync(Flags.Escrow, ct);
        if (await Orders.Find(o => o.OfferMessageId == quoteId && o.Status != OrderStatus.Cancelled).AnyAsync(ct))
            throw DomainException.Conflict("ORDER_EXISTS", "Báo giá này đã có đơn");
        return await CreateAsync(buyListing, buyListing.SellerId, quoterId, buyListing.SellerId, OrderSource.Quote, quoteId, unitPrice,
            qty, delivery, shippingFee, address, pickupDate, shipDays, ct);
    }

    async Task<EscrowOrder> CreateAsync(Listing l, string buyerId, string sellerId, string creatorId, OrderSource source, string? offerId, long unitPrice,
        int qty, DeliveryMethod delivery, long? shippingFee, string? address, DateTime? pickupDate, int? shipDays, CancellationToken ct)
    {
        if (buyerId == sellerId) throw new DomainException("CANNOT_BUY_OWN", "Không thể mua tin của chính mình");
        await users.RequireActiveAsync(buyerId, ct);
        await RequireSellerEligibleAsync(sellerId, ct);
        if (qty < 1 || qty > 1000) throw new DomainException("INVALID_QUANTITY", "Số lượng không hợp lệ");
        if (delivery == DeliveryMethod.PlatformCarrier) throw new DomainException("DELIVERY_UNAVAILABLE", "Giao qua Chạm Xanh chưa mở");
        var ship = shippingFee ?? 0;
        if (ship < 0 || ship > 50_000_000) throw new DomainException("INVALID_SHIPPING_FEE", "Phí giao hàng không hợp lệ");
        if (delivery == DeliveryMethod.BuyerPickup)
        {
            if (pickupDate is null || pickupDate < Now.Date) throw new DomainException("PICKUP_DATE_REQUIRED", "Chọn ngày hẹn đến lấy cây");
            ship = 0;
        }
        else if (string.IsNullOrWhiteSpace(address)) throw new DomainException("ADDRESS_REQUIRED", "Nhập địa chỉ nhận hàng");
        var days = Math.Clamp(shipDays ?? 3, 1, 7);

        var book = await pricing.GetActiveAsync(ct) ?? throw new DomainException("NO_PRICE_BOOK", "Chưa có bảng giá");
        var items = unitPrice * qty;
        if (items < book.EscrowFee.OrderMinVnd || items > book.EscrowFee.OrderMaxVnd)
            throw new DomainException("ORDER_AMOUNT_OUT_OF_RANGE", $"Giá trị đơn phải từ {book.EscrowFee.OrderMinVnd:N0}đ đến {book.EscrowFee.OrderMaxVnd:N0}đ");
        var fee = PriceCalculator.EscrowFee(book.EscrowFee, items);
        var buyerPaysFee = book.EscrowFee.Payer == "BUYER";

        // BR-ESC-17: giữ chỗ số lượng nguyên tử — chặn hai người cùng trả tiền cho một cây.
        if (source != OrderSource.Quote && !await ReserveAsync(l.Id, qty, ct)) throw DomainException.Conflict("OUT_OF_STOCK", "Không đủ số lượng còn lại");

        var sellerCreated = creatorId == sellerId;
        var o = new EscrowOrder
        {
            Code = NewCode(), ListingId = l.Id, ListingTitle = l.Title, ThumbMediaId = l.Media.FirstOrDefault()?.MediaId,
            BuyerId = buyerId, SellerId = sellerId, Source = source, OfferMessageId = offerId, CreatedBy = creatorId,
            Quantity = qty, UnitPrice = unitPrice, ItemAmount = items, ShippingFee = ship, Fee = fee, FeePayer = book.EscrowFee.Payer,
            Total = items + ship + (buyerPaysFee ? fee : 0), PriceBookVersion = book.Version,
            Delivery = delivery, ShipWithinDays = days, PickupDate = pickupDate, DeliveryAddress = address?.Trim(),
            PickupCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
            Status = OrderStatus.AwaitingPayment,
            PaymentDueAt = sellerCreated ? Now.AddHours(options.SellerPaymentHours) : Now.AddMinutes(options.BuyerPaymentMinutes),
            History = [new OrderEvent { Status = nameof(OrderStatus.AwaitingPayment), ActorId = creatorId, At = Now }],
            CreatedAt = Now, UpdatedAt = Now,
        };
        await Orders.InsertOneAsync(o, cancellationToken: ct);
        if (sellerCreated)
            await notifications.SendAsync(buyerId, "escrow.created", "Người bán đã tạo đơn đảm bảo", $"{l.Title} — thanh toán trong 24 giờ", Link(o), ct);
        return o;
    }

    static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
    static string Link(EscrowOrder o) => $"/don-hang/{o.Id}";

    // ---------- Giữ chỗ số lượng ----------

    async Task<bool> ReserveAsync(string listingId, int qty, CancellationToken ct)
    {
        var res = await listings.Listings.UpdateOneAsync(
            l => l.Id == listingId && l.Status == ListingStatus.Active && l.Quantity - l.Reserved - l.Sold >= qty,
            Builders<Listing>.Update.Inc(l => l.Reserved, qty), cancellationToken: ct);
        await RefreshStockStatusAsync(listingId, ct);
        return res.ModifiedCount == 1;
    }

    /// <summary>Đơn từ báo giá (tin Cần mua) không giữ chỗ số lượng trên tin.</summary>
    async Task MoveStockAsync(EscrowOrder o, int reservedDelta, int soldDelta, CancellationToken ct)
    {
        if (o.Source == OrderSource.Quote) return;
        await listings.Listings.UpdateOneAsync(l => l.Id == o.ListingId,
            Builders<Listing>.Update.Inc(l => l.Reserved, reservedDelta).Inc(l => l.Sold, soldDelta), cancellationToken: ct);
        await RefreshStockStatusAsync(o.ListingId, ct);
    }

    async Task RefreshStockStatusAsync(string listingId, CancellationToken ct)
    {
        await listings.Listings.UpdateOneAsync(l => l.Id == listingId && l.Status == ListingStatus.Active && l.Quantity - l.Reserved - l.Sold <= 0,
            Builders<Listing>.Update.Set(l => l.Status, ListingStatus.SoldOut), cancellationToken: ct);
        await listings.Listings.UpdateOneAsync(l => l.Id == listingId && l.Status == ListingStatus.SoldOut && l.Quantity - l.Reserved - l.Sold > 0,
            Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Active), cancellationToken: ct);
    }

    // ---------- Chuyển trạng thái ----------

    async Task<EscrowOrder> MoveAsync(EscrowOrder o, OrderStatus[] from, OrderStatus to, string actor, string? note,
        Func<UpdateDefinitionBuilder<EscrowOrder>, UpdateDefinition<EscrowOrder>>? extra, CancellationToken ct)
    {
        if (!from.Contains(o.Status))
            throw DomainException.Conflict("INVALID_STATE", $"Không thể thực hiện khi đơn đang ở trạng thái {o.Status}");
        var b = Builders<EscrowOrder>.Update;
        var u = b.Combine(
            b.Set(x => x.Status, to).Set(x => x.UpdatedAt, Now).Inc(x => x.Version, 1)
             .Push(x => x.History, new OrderEvent { Status = to.ToString(), ActorId = actor, Note = note, At = Now }),
            extra?.Invoke(b) ?? b.Set(x => x.UpdatedAt, Now));
        var res = await Orders.UpdateOneAsync(x => x.Id == o.Id && x.Version == o.Version && x.Status == o.Status, u, cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("CONCURRENT_UPDATE", "Đơn vừa được cập nhật, vui lòng tải lại");
        return await GetAsync(o.Id, ct);
    }

    /// <summary>SMS/ZNS kèm theo ở bước quan trọng: đã thanh toán, đã giao, khiếu nại (05 §5).</summary>
    async Task Notify(string userId, string title, string? body, EscrowOrder o, CancellationToken ct, bool sms = false)
    {
        var phone = sms ? (await users.GetAsync(userId, ct)).Phone : null;
        await notifications.SendAsync(userId, "escrow", $"Đơn {o.Code}: {title}", body, Link(o), ct, phone);
    }

    // ---------- Thanh toán ----------

    public async Task<PaymentSession> StartPaymentAsync(string buyerId, string id, CancellationToken ct)
    {
        var o = await GetForPartyAsync(id, buyerId, ct);
        if (o.BuyerId != buyerId) throw DomainException.Forbidden("Chỉ người mua thanh toán đơn");
        if (o.Status != OrderStatus.AwaitingPayment || o.PaymentDueAt < Now) throw DomainException.Conflict("PAYMENT_CLOSED", "Đơn đã hết hạn hoặc đã thanh toán");
        var session = await gateway.CreatePaymentAsync(o, o.Total, ct);
        await Orders.UpdateOneAsync(x => x.Id == o.Id, Builders<EscrowOrder>.Update.Set(x => x.GatewayRef, session.GatewayRef), cancellationToken: ct);
        return session;
    }

    /// <summary>Webhook đối tác: kiểm chữ ký HMAC, idempotent. Tiền về sau khi đơn đã hủy thì tự hoàn (BR-ESC-18).</summary>
    public async Task<EscrowOrder> HandleWebhookAsync(string rawBody, string? signature, EscrowWebhook payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(paymentOptions.WebhookSecret) || signature is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(TopUpService.Sign(paymentOptions.WebhookSecret, rawBody)), Encoding.UTF8.GetBytes(signature.ToLowerInvariant())))
            throw new DomainException("INVALID_SIGNATURE", "Chữ ký webhook không hợp lệ", StatusCodes.Status401Unauthorized);
        var o = await GetAsync(payload.OrderId, ct);
        if (payload.Status != "PAID" || o.PaidAt is not null) return o;
        if (payload.AmountVnd != o.Total)
            throw new DomainException("AMOUNT_MISMATCH", $"Số tiền {payload.AmountVnd} không khớp đơn {o.Total} — chuyển kế toán đối soát");

        if (o.Status != OrderStatus.AwaitingPayment)
        {
            var refRef = await gateway.RefundAsync(o, payload.AmountVnd, "Tiền về sau khi đơn đã hủy", ct);
            await Orders.UpdateOneAsync(x => x.Id == o.Id, Builders<EscrowOrder>.Update
                .Set(x => x.PaidAt, Now).Set(x => x.GatewayRef, payload.GatewayRef).Set(x => x.RefundAmount, payload.AmountVnd).Set(x => x.RefundRef, refRef), cancellationToken: ct);
            await Notify(o.BuyerId, "tiền về sau khi đơn đã hủy", "Khoản thanh toán sẽ được hoàn trong 1 ngày làm việc", o, ct);
            return await GetAsync(o.Id, ct);
        }

        var sellerCreated = o.CreatedBy == o.SellerId;
        var to = sellerCreated ? OrderStatus.Paid : OrderStatus.AwaitingSellerConfirm;
        o = await MoveAsync(o, [OrderStatus.AwaitingPayment], to, "gateway", null, b => b
            .Set(x => x.PaidAt, Now).Set(x => x.GatewayRef, payload.GatewayRef)
            .Set(x => x.SellerConfirmDueAt, sellerCreated ? null : Now.AddHours(options.SellerConfirmHours))
            .Set(x => x.ShipDueAt, sellerCreated && o.Delivery != DeliveryMethod.BuyerPickup ? Now.AddDays(o.ShipWithinDays) : null), ct);
        await MoveStockAsync(o, -o.Quantity, o.Quantity, ct);
        await Notify(o.SellerId, sellerCreated ? "người mua đã thanh toán, hãy chuẩn bị cây" : "có đơn mới, xác nhận còn hàng trong 24 giờ", o.ListingTitle, o, ct, sms: true);
        return o;
    }

    // ---------- Người bán ----------

    EscrowOrder RequireSeller(EscrowOrder o, string userId) => o.SellerId == userId ? o : throw DomainException.Forbidden("Chỉ người bán thực hiện được");
    EscrowOrder RequireBuyer(EscrowOrder o, string userId) => o.BuyerId == userId ? o : throw DomainException.Forbidden("Chỉ người mua thực hiện được");

    public async Task<EscrowOrder> SellerConfirmAsync(string userId, string id, CancellationToken ct)
    {
        var o = RequireSeller(await GetAsync(id, ct), userId);
        o = await MoveAsync(o, [OrderStatus.AwaitingSellerConfirm], OrderStatus.Paid, userId, null,
            b => b.Set(x => x.ShipDueAt, o.Delivery == DeliveryMethod.BuyerPickup ? null : Now.AddDays(o.ShipWithinDays)), ct);
        await Notify(o.BuyerId, "người bán đã xác nhận còn hàng", null, o, ct);
        return o;
    }

    public async Task<EscrowOrder> ShipAsync(string userId, string id, ShipRequest req, CancellationToken ct)
    {
        var o = RequireSeller(await GetAsync(id, ct), userId);
        if (o.Delivery == DeliveryMethod.BuyerPickup) throw new DomainException("PICKUP_ORDER", "Đơn nhận tại vườn: quét mã QR của người mua khi giao cây");
        var mediaIds = req.MediaIds ?? [];
        if (o.Delivery == DeliveryMethod.SelfArrangedCarrier &&
            (string.IsNullOrWhiteSpace(req.Carrier) || string.IsNullOrWhiteSpace(req.TrackingCode) || mediaIds.Count == 0 || req.ExpectedArrival is null))
            throw new DomainException("SHIPMENT_INFO_REQUIRED", "Gửi xe cần tên nhà xe, mã vận đơn, ảnh phiếu gửi và ngày dự kiến đến");
        await media.GetOwnedAsync(mediaIds, userId, ct);
        await media.AttachAsync(mediaIds, $"order:{o.Id}", ct);
        var shipment = new ShipmentInfo { Carrier = req.Carrier?.Trim(), TrackingCode = req.TrackingCode?.Trim(), MediaIds = mediaIds, ExpectedArrival = req.ExpectedArrival };
        o = await MoveAsync(o, [OrderStatus.Paid], OrderStatus.Shipping, userId, null, b => b.Set(x => x.Shipment, shipment).Set(x => x.ShippedAt, Now), ct);
        await Notify(o.BuyerId, "cây đang được giao", o.Delivery == DeliveryMethod.SelfArrangedCarrier ? $"{shipment.Carrier} · mã {shipment.TrackingCode}" : null, o, ct);
        return o;
    }

    /// <summary>SELLER_DELIVERY: người bán báo đã giao kèm ảnh bằng chứng, mở 48 giờ kiểm tra.</summary>
    public async Task<EscrowOrder> MarkDeliveredBySellerAsync(string userId, string id, DeliverRequest req, CancellationToken ct)
    {
        var o = RequireSeller(await GetAsync(id, ct), userId);
        if (o.Delivery != DeliveryMethod.SellerDelivery) throw new DomainException("BUYER_CONFIRMS", "Với gửi xe, người mua báo đã nhận hàng");
        var mediaIds = req.MediaIds ?? [];
        if (mediaIds.Count == 0) throw new DomainException("PROOF_REQUIRED", "Cần ảnh giao hàng");
        await media.GetOwnedAsync(mediaIds, userId, ct);
        await media.AttachAsync(mediaIds, $"order:{o.Id}", ct);
        return await ToDeliveredAsync(o, userId, mediaIds, ct);
    }

    async Task<EscrowOrder> ToDeliveredAsync(EscrowOrder o, string actor, List<string> proof, CancellationToken ct)
    {
        o = await MoveAsync(o, [OrderStatus.Shipping], OrderStatus.Delivered, actor, null, b => b
            .Set(x => x.DeliveredAt, Now).Set(x => x.InspectionDueAt, Now.AddHours(options.InspectionHours)).Set(x => x.DeliveryProofMediaIds, proof), ct);
        await Notify(o.BuyerId, "cây đã được giao", "Hãy quay video mở hàng. Bạn có 48 giờ để kiểm tra và khiếu nại", o, ct, sms: true);
        return o;
    }

    /// <summary>BUYER_PICKUP: người bán quét mã QR trên máy người mua, đơn hoàn thành ngay (không có 48h).</summary>
    public async Task<EscrowOrder> ConfirmPickupAsync(string userId, string id, PickupRequest req, CancellationToken ct)
    {
        var o = RequireSeller(await GetAsync(id, ct), userId);
        if (o.Delivery != DeliveryMethod.BuyerPickup) throw new DomainException("NOT_PICKUP", "Đơn không phải nhận tại vườn");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.Code ?? ""), Encoding.UTF8.GetBytes(o.PickupCode)))
            throw new DomainException("INVALID_PICKUP_CODE", "Mã nhận hàng không đúng");
        return await CompleteAsync(o, [OrderStatus.Paid], userId, "Nhận tại vườn", ct);
    }

    public async Task<EscrowOrder> CancelAsync(string userId, string id, CancelRequest req, CancellationToken ct)
    {
        var o = await GetForPartyAsync(id, userId, ct);
        var reason = req.Reason?.Trim();
        if (o.Status == OrderStatus.AwaitingPayment)
        {
            o = await MoveAsync(o, [OrderStatus.AwaitingPayment], OrderStatus.Cancelled, userId, reason, b => b.Set(x => x.CancelReason, reason ?? "Hủy trước khi thanh toán"), ct);
            await MoveStockAsync(o, -o.Quantity, 0, ct);
            await Notify(userId == o.BuyerId ? o.SellerId : o.BuyerId, "đơn đã bị hủy", reason, o, ct);
            return o;
        }
        RequireSeller(o, userId);
        // BR-ESC-12: người mua không đến lấy sau 2 ngày kể từ ngày hẹn — người bán hủy không bị phạt.
        var noShow = o.Delivery == DeliveryMethod.BuyerPickup && o.Status == OrderStatus.Paid && o.PickupDate?.AddDays(2) < Now;
        o = await RefundAndCloseAsync(o, [OrderStatus.AwaitingSellerConfirm, OrderStatus.Paid], userId,
            noShow ? "Người mua không đến lấy" : reason ?? "Người bán hủy", sellerFault: !noShow && o.Status == OrderStatus.Paid, buyerNoShow: noShow, ct);
        if (!noShow && o.CancelledBySeller)
        {
            var count = await Orders.CountDocumentsAsync(x => x.SellerId == o.SellerId && x.CancelledBySeller && x.UpdatedAt > Now.AddDays(-30), cancellationToken: ct);
            if (count >= 2) await moderation.AddViolationAsync(o.SellerId, "ESCROW_SELLER_CANCEL", 3, o.ListingId, "system", ct); // BR-ESC-19
        }
        return o;
    }

    async Task<EscrowOrder> RefundAndCloseAsync(EscrowOrder o, OrderStatus[] from, string actor, string reason, bool sellerFault, bool buyerNoShow, CancellationToken ct)
    {
        var refundRef = await gateway.RefundAsync(o, o.Total, reason, ct);
        o = await MoveAsync(o, from, OrderStatus.CancelledRefunded, actor, reason, b => b
            .Set(x => x.RefundAmount, o.Total).Set(x => x.RefundRef, refundRef).Set(x => x.CancelReason, reason)
            .Set(x => x.CancelledBySeller, sellerFault).Set(x => x.BuyerNoShow, buyerNoShow), ct);
        await MoveStockAsync(o, 0, -o.Quantity, ct);
        await Notify(o.BuyerId, "đã hoàn 100% tiền", reason, o, ct);
        await Notify(o.SellerId, "đơn đã hủy và hoàn tiền cho người mua", reason, o, ct);
        return o;
    }

    // ---------- Người mua ----------

    /// <summary>SELF_ARRANGED_CARRIER: người mua báo đã nhận hàng ở nhà xe → mở 48 giờ kiểm tra.</summary>
    public async Task<EscrowOrder> BuyerReceivedAsync(string userId, string id, CancellationToken ct)
    {
        var o = RequireBuyer(await GetAsync(id, ct), userId);
        return await ToDeliveredAsync(o, userId, [], ct);
    }

    public async Task<EscrowOrder> BuyerAcceptAsync(string userId, string id, CancellationToken ct)
    {
        var o = RequireBuyer(await GetAsync(id, ct), userId);
        return await CompleteAsync(o, [OrderStatus.Delivered], userId, "Người mua xác nhận hài lòng", ct);
    }

    async Task<EscrowOrder> CompleteAsync(EscrowOrder o, OrderStatus[] from, string actor, string? note, CancellationToken ct)
    {
        o = await MoveAsync(o, from, OrderStatus.Completed, actor, note, b => b
            .Set(x => x.CompletedAt, Now).Set(x => x.PayoutEligibleAt, Now.AddDays(options.PayoutDelayDays)).Set(x => x.PayoutAmount, SellerPayout(o, o.RefundAmount)), ct);
        await Notify(o.SellerId, "đơn hoàn thành", "Tiền sẽ được chuyển sau T+1 ngày làm việc", o, ct);
        await Notify(o.BuyerId, "đơn hoàn thành — mời bạn đánh giá \"Đã mua hàng\"", null, o, ct);
        return o;
    }

    /// <summary>Số tiền chi cho người bán: tiền cây + ship − phần hoàn − phí (phí tính trên phần người bán thực nhận, BR-DSP-06).</summary>
    public static long SellerPayout(EscrowOrder o, long refund) => SellerPayout(o.ItemAmount, o.ShippingFee, o.Fee, o.FeePayer, refund);

    public static long SellerPayout(long itemAmount, long shippingFee, long fee, string feePayer, long refund)
    {
        var goods = itemAmount + shippingFee;
        var kept = Math.Max(0, goods - refund);
        if (kept == 0) return 0;
        var charged = feePayer == "SELLER" ? (long)Math.Ceiling((decimal)fee * Math.Min(kept, itemAmount) / itemAmount) : 0;
        return Math.Max(0, kept - charged);
    }

    // ---------- Khiếu nại ----------

    public async Task<EscrowOrder> OpenDisputeAsync(string userId, string id, OpenDisputeRequest req, CancellationToken ct)
    {
        var o = RequireBuyer(await GetAsync(id, ct), userId);
        var canOpen = o.Status == OrderStatus.Delivered && o.InspectionDueAt > Now
            // BR-ESC-16: "Không nhận được hàng" khi quá ngày dự kiến + 2 ngày
            || o.Status == OrderStatus.Shipping && req.Reason == DisputeReason.NotReceived
               && (o.Shipment?.ExpectedArrival ?? o.ShipDueAt ?? o.ShippedAt?.AddDays(o.ShipWithinDays))?.AddDays(2) < Now;
        if (!canOpen) throw DomainException.Conflict("DISPUTE_WINDOW_CLOSED", "Đã hết thời hạn khiếu nại hoặc chưa đến lúc khiếu nại");
        var desc = req.Description?.Trim() ?? "";
        if (desc.Length is < 10 or > 2000) throw new DomainException("INVALID_DESCRIPTION", "Mô tả dài 10–2000 ký tự");
        var mediaIds = req.MediaIds ?? [];
        if (req.Reason != DisputeReason.NotReceived && mediaIds.Count == 0)
            throw new DomainException("EVIDENCE_REQUIRED", "Cần ảnh hoặc video mở hàng làm bằng chứng");
        await media.GetOwnedAsync(mediaIds, userId, ct);
        await media.AttachAsync(mediaIds, $"order:{o.Id}", ct);
        var d = new Dispute
        {
            Reason = req.Reason, Description = desc, MediaIds = mediaIds, HasUnboxingVideo = req.HasUnboxingVideo,
            OpenedAt = Now, SellerDueAt = Now.AddHours(options.SellerDisputeResponseHours),
        };
        o = await MoveAsync(o, [OrderStatus.Delivered, OrderStatus.Shipping], OrderStatus.Disputed, userId, req.Reason.ToString(), b => b.Set(x => x.Dispute, d), ct);
        await Notify(o.SellerId, "người mua khiếu nại", "Phản hồi trong 48 giờ, quá hạn sẽ xử có lợi cho người mua", o, ct, sms: true);
        return o;
    }

    public async Task<EscrowOrder> RespondDisputeAsync(string userId, string id, DisputeResponseRequest req, CancellationToken ct)
    {
        var o = RequireSeller(await GetAsync(id, ct), userId);
        if (o.Status != OrderStatus.Disputed || o.Dispute!.SellerRespondedAt is not null) throw DomainException.Conflict("INVALID_STATE", "Không thể phản hồi lúc này");
        if (req.Response == SellerDisputeResponse.AcceptFullRefund)
            return await ResolveAsync(o, DisputeOutcome.FullRefund, null, "Người bán đồng ý hoàn toàn bộ", userId, sellerLost: true, ct);
        if (req.Response == SellerDisputeResponse.OfferPartialRefund && (req.Amount is null || req.Amount <= 0 || req.Amount >= o.ItemAmount + o.ShippingFee))
            throw new DomainException("INVALID_AMOUNT", "Số tiền hoàn một phần không hợp lệ");
        var mediaIds = req.MediaIds ?? [];
        await media.GetOwnedAsync(mediaIds, userId, ct);
        await media.AttachAsync(mediaIds, $"order:{o.Id}", ct);
        await Orders.UpdateOneAsync(x => x.Id == o.Id, Builders<EscrowOrder>.Update
            .Set(x => x.Dispute!.SellerResponse, req.Response).Set(x => x.Dispute!.SellerOfferAmount, req.Amount)
            .Set(x => x.Dispute!.SellerNote, req.Note?.Trim()).Set(x => x.Dispute!.SellerMediaIds, mediaIds)
            .Set(x => x.Dispute!.SellerRespondedAt, Now).Set(x => x.UpdatedAt, Now).Inc(x => x.Version, 1), cancellationToken: ct);
        await Notify(o.BuyerId, "người bán đã phản hồi khiếu nại", req.Note, o, ct);
        return await GetAsync(o.Id, ct);
    }

    /// <summary>Người mua chấp nhận đề xuất hoàn một phần của người bán (hòa giải, BR-DSP-03).</summary>
    public async Task<EscrowOrder> BuyerAcceptProposalAsync(string userId, string id, CancellationToken ct)
    {
        var o = RequireBuyer(await GetAsync(id, ct), userId);
        if (o.Status != OrderStatus.Disputed || o.Dispute?.SellerResponse != SellerDisputeResponse.OfferPartialRefund)
            throw DomainException.Conflict("NO_PROPOSAL", "Không có đề xuất để chấp nhận");
        await Orders.UpdateOneAsync(x => x.Id == o.Id, Builders<EscrowOrder>.Update.Set(x => x.Dispute!.BuyerAccepted, true), cancellationToken: ct);
        return await ResolveAsync(await GetAsync(id, ct), DisputeOutcome.PartialRefund, o.Dispute.SellerOfferAmount, "Hai bên hòa giải", userId, sellerLost: false, ct);
    }

    /// <summary>Người mua rút khiếu nại → hoàn thành.</summary>
    public async Task<EscrowOrder> WithdrawDisputeAsync(string userId, string id, CancellationToken ct)
    {
        var o = RequireBuyer(await GetAsync(id, ct), userId);
        return await ResolveAsync(o, DisputeOutcome.RejectClaim, null, "Người mua rút khiếu nại", userId, sellerLost: false, ct);
    }

    /// <summary>CSKH phân xử (UC-ESC-07) hoặc kết quả tự động.</summary>
    public async Task<EscrowOrder> ResolveAsync(EscrowOrder o, DisputeOutcome outcome, long? amount, string note, string actor, bool sellerLost, CancellationToken ct)
    {
        if (o.Status != OrderStatus.Disputed) throw DomainException.Conflict("INVALID_STATE", "Đơn không ở trạng thái khiếu nại");
        var goods = o.ItemAmount + o.ShippingFee;
        long refund = outcome switch
        {
            DisputeOutcome.RejectClaim => 0,
            DisputeOutcome.PartialRefund => amount is > 0 && amount < goods ? amount.Value : throw new DomainException("INVALID_AMOUNT", "Số tiền hoàn một phần không hợp lệ"),
            _ => o.Total,
        };
        if (outcome == DisputeOutcome.FullRefundWithReturn && o.ItemAmount < options.ReturnRequiredMinVnd)
            throw new DomainException("RETURN_NOT_ALLOWED", $"Chỉ yêu cầu trả cây với đơn từ {options.ReturnRequiredMinVnd:N0}đ (BR-DSP-04)");
        var lost = sellerLost || outcome is DisputeOutcome.FullRefund or DisputeOutcome.FullRefundWithReturn or DisputeOutcome.PartialRefund && actor != o.BuyerId;
        UpdateDefinition<EscrowOrder> Resolution(UpdateDefinitionBuilder<EscrowOrder> b) => b
            .Set(x => x.Dispute!.Outcome, outcome).Set(x => x.Dispute!.ResolvedRefund, refund).Set(x => x.Dispute!.ResolutionNote, note)
            .Set(x => x.Dispute!.ResolvedBy, actor).Set(x => x.Dispute!.ResolvedAt, Now).Set(x => x.Dispute!.SellerLost, lost);

        switch (outcome)
        {
            case DisputeOutcome.RejectClaim:
                o = await CompleteAsync(o, [OrderStatus.Disputed], actor, note, ct);
                await Orders.UpdateOneAsync(x => x.Id == o.Id, Resolution(Builders<EscrowOrder>.Update), cancellationToken: ct);
                break;
            case DisputeOutcome.PartialRefund:
            {
                var rf = await gateway.RefundAsync(o, refund, note, ct);
                o = await MoveAsync(o, [OrderStatus.Disputed], OrderStatus.PartiallyRefunded, actor, note, b => b.Combine(Resolution(b),
                    b.Set(x => x.RefundAmount, refund).Set(x => x.RefundRef, rf).Set(x => x.CompletedAt, Now)
                     .Set(x => x.PayoutEligibleAt, Now.AddDays(options.PayoutDelayDays)).Set(x => x.PayoutAmount, SellerPayout(o, refund))), ct);
                await Notify(o.BuyerId, $"được hoàn {refund:N0}đ", note, o, ct);
                break;
            }
            case DisputeOutcome.FullRefund:
            {
                var rf = await gateway.RefundAsync(o, refund, note, ct);
                o = await MoveAsync(o, [OrderStatus.Disputed], OrderStatus.Refunded, actor, note, b => b.Combine(Resolution(b),
                    b.Set(x => x.RefundAmount, refund).Set(x => x.RefundRef, rf).Set(x => x.PayoutAmount, 0L)), ct);
                await Notify(o.BuyerId, "được hoàn toàn bộ tiền", note, o, ct);
                break;
            }
            case DisputeOutcome.FullRefundWithReturn:
                o = await MoveAsync(o, [OrderStatus.Disputed], OrderStatus.AwaitingReturn, actor, note, Resolution, ct);
                await Notify(o.BuyerId, "cần gửi trả cây để nhận hoàn tiền", "Phí gửi trả được hoàn cùng tiền cây", o, ct);
                break;
        }
        await Notify(o.SellerId, "khiếu nại đã có kết quả", note, o, ct);
        if (lost && actor != "system")
        {
            var lostCount = await Orders.CountDocumentsAsync(x => x.SellerId == o.SellerId && x.Dispute != null && x.Dispute.SellerLost && x.Dispute.ResolvedAt > Now.AddDays(-90), cancellationToken: ct);
            if (lostCount >= 3) await moderation.AddViolationAsync(o.SellerId, "ESCROW_DISPUTES_LOST", 3, o.ListingId, actor, ct); // BR-DSP-07
        }
        return await GetAsync(o.Id, ct);
    }

    public async Task<EscrowOrder> ShipReturnAsync(string userId, string id, ReturnShipRequest req, CancellationToken ct)
    {
        var o = RequireBuyer(await GetAsync(id, ct), userId);
        if (o.Status != OrderStatus.AwaitingReturn) throw DomainException.Conflict("INVALID_STATE", "Đơn không chờ trả hàng");
        if (string.IsNullOrWhiteSpace(req.Carrier) || string.IsNullOrWhiteSpace(req.TrackingCode)) throw new DomainException("SHIPMENT_INFO_REQUIRED", "Cần nhà xe và mã vận đơn");
        var mediaIds = req.MediaIds ?? [];
        await media.GetOwnedAsync(mediaIds, userId, ct);
        await Orders.UpdateOneAsync(x => x.Id == o.Id && x.Status == OrderStatus.AwaitingReturn, Builders<EscrowOrder>.Update
            .Set(x => x.Dispute!.Return, new ShipmentInfo { Carrier = req.Carrier.Trim(), TrackingCode = req.TrackingCode.Trim(), MediaIds = mediaIds })
            .Set(x => x.Dispute!.ReturnShippedAt, Now).Set(x => x.UpdatedAt, Now).Inc(x => x.Version, 1), cancellationToken: ct);
        await Notify(o.SellerId, "người mua đã gửi trả cây", $"{req.Carrier} · mã {req.TrackingCode}", o, ct);
        return await GetAsync(id, ct);
    }

    public async Task<EscrowOrder> ConfirmReturnReceivedAsync(string actor, EscrowOrder o, CancellationToken ct)
    {
        var rf = await gateway.RefundAsync(o, o.Total, "Hoàn tiền sau khi trả cây", ct);
        o = await MoveAsync(o, [OrderStatus.AwaitingReturn], OrderStatus.Refunded, actor, "Đã nhận lại cây", b => b
            .Set(x => x.RefundAmount, o.Total).Set(x => x.RefundRef, rf).Set(x => x.PayoutAmount, 0L), ct);
        await Notify(o.BuyerId, "đã hoàn toàn bộ tiền", null, o, ct);
        return o;
    }

    // ---------- Quyết toán ----------

    public async Task<EscrowOrder> ApprovePayoutAsync(string actor, string id, CancellationToken ct)
    {
        var o = await GetAsync(id, ct);
        if (o.Status is not (OrderStatus.Completed or OrderStatus.PartiallyRefunded)) throw DomainException.Conflict("INVALID_STATE", "Đơn chưa đến bước quyết toán");
        if (o.SettlementHold) throw DomainException.Conflict("SETTLEMENT_HOLD", "Đơn đang tạm dừng quyết toán");
        if (o.PayoutEligibleAt > Now) throw DomainException.Conflict("TOO_EARLY", "Chưa đến hạn quyết toán T+1");
        var garden = await gardens.FindByOwnerAsync(o.SellerId, ct);
        if (garden?.Bank is null) throw new DomainException("SELLER_NO_BANK", "Người bán chưa có tài khoản ngân hàng");
        var amount = o.PayoutAmount ?? SellerPayout(o, o.RefundAmount);
        var payoutRef = amount > 0
            ? await gateway.PayoutAsync(o, amount, garden.Bank.BankCode, protector.Decrypt(garden.Bank.AccountNoEnc), garden.Bank.AccountName, ct)
            : "NONE";
        o = await MoveAsync(o, [OrderStatus.Completed, OrderStatus.PartiallyRefunded], OrderStatus.Settled, actor, null, b => b
            .Set(x => x.PayoutAmount, amount).Set(x => x.PayoutApprovedBy, actor).Set(x => x.PayoutRef, payoutRef).Set(x => x.SettledAt, Now), ct);
        await Notify(o.SellerId, $"đã chuyển {amount:N0}đ về tài khoản", null, o, ct);
        return o;
    }

    public async Task<EscrowOrder> SetHoldAsync(string id, bool hold, CancellationToken ct)
    {
        await Orders.UpdateOneAsync(x => x.Id == id, Builders<EscrowOrder>.Update.Set(x => x.SettlementHold, hold).Set(x => x.UpdatedAt, Now), cancellationToken: ct);
        return await GetAsync(id, ct);
    }

    // ---------- Đánh giá "Đã mua hàng" (D-14) ----------

    public async Task<Review> ReviewAsync(string userId, string id, OrderReviewRequest req, CancellationToken ct)
    {
        var o = RequireBuyer(await GetAsync(id, ct), userId);
        if (o.Status is not (OrderStatus.Completed or OrderStatus.PartiallyRefunded or OrderStatus.Settled))
            throw DomainException.Conflict("NOT_COMPLETED", "Chỉ đánh giá sau khi đơn hoàn thành");
        if (o.Reviewed) throw DomainException.Conflict("ALREADY_REVIEWED", "Bạn đã đánh giá đơn này");
        if (req.Stars is < 1 or > 5) throw new DomainException("INVALID_STARS", "Chấm từ 1 đến 5 sao");
        var text = req.Text?.Trim();
        if (text?.Length > 1000) throw new DomainException("TEXT_TOO_LONG", "Nhận xét tối đa 1.000 ký tự");
        var review = new Review
        {
            ReviewerId = userId, SellerId = o.SellerId, Tier = ReviewTier.Purchased, ListingId = o.ListingId, OrderId = o.Id,
            Stars = req.Stars, Tags = (req.Tags ?? []).Where(ReviewService.AllowedTags.Contains).Distinct().ToList(), Text = text,
            CreatedAt = Now, UpdatedAt = Now,
        };
        var res = await Orders.UpdateOneAsync(x => x.Id == o.Id && !x.Reviewed, Builders<EscrowOrder>.Update.Set(x => x.Reviewed, true), cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("ALREADY_REVIEWED", "Bạn đã đánh giá đơn này");
        await reviews.Reviews.InsertOneAsync(review, cancellationToken: ct);
        return review;
    }

    // ---------- Job định kỳ (thời hạn BR-ESC-10..20) ----------

    public async Task RunDeadlinesAsync(CancellationToken ct)
    {
        foreach (var o in await Orders.Find(x => x.Status == OrderStatus.AwaitingPayment && x.PaymentDueAt < Now).Limit(200).ToListAsync(ct))
            await Try(async () =>
            {
                await MoveAsync(o, [OrderStatus.AwaitingPayment], OrderStatus.Cancelled, "system", "Quá hạn thanh toán", b => b.Set(x => x.CancelReason, "Quá hạn thanh toán"), ct);
                await MoveStockAsync(o, -o.Quantity, 0, ct);
            });
        foreach (var o in await Orders.Find(x => x.Status == OrderStatus.AwaitingSellerConfirm && x.SellerConfirmDueAt < Now).Limit(200).ToListAsync(ct))
            await Try(() => RefundAndCloseAsync(o, [OrderStatus.AwaitingSellerConfirm], "system", "Người bán không xác nhận trong 24 giờ", false, false, ct));
        foreach (var o in await Orders.Find(x => x.Status == OrderStatus.Paid && x.ShipDueAt < Now).Limit(200).ToListAsync(ct))
            await Try(() => RefundAndCloseAsync(o, [OrderStatus.Paid], "system", "Quá hạn gửi hàng", true, false, ct));
        // SELF_ARRANGED: quá ngày dự kiến đến + 2 ngày thì tự chuyển Đã giao (đã nhắc người mua).
        foreach (var o in await Orders.Find(x => x.Status == OrderStatus.Shipping && x.Delivery == DeliveryMethod.SelfArrangedCarrier && x.Shipment!.ExpectedArrival < Now.AddDays(-2)).Limit(200).ToListAsync(ct))
            await Try(() => ToDeliveredAsync(o, "system", [], ct));
        // BR-ESC-14: nhắc ở mốc 24h và 4h trước hạn, rồi mới tự hoàn thành.
        foreach (var o in await Orders.Find(x => x.Status == OrderStatus.Delivered).Limit(500).ToListAsync(ct))
            await Try(async () =>
            {
                var left = o.InspectionDueAt!.Value - Now;
                if (left <= TimeSpan.Zero && o.InspectionRemindersSent >= 2) { await CompleteAsync(o, [OrderStatus.Delivered], "system", "Hết 48 giờ kiểm tra", ct); return; }
                var due = left <= TimeSpan.FromHours(4) ? 2 : left <= TimeSpan.FromHours(24) ? 1 : 0;
                if (due > o.InspectionRemindersSent)
                {
                    await Orders.UpdateOneAsync(x => x.Id == o.Id, Builders<EscrowOrder>.Update.Set(x => x.InspectionRemindersSent, due)
                        .Set(x => x.InspectionDueAt, left < TimeSpan.FromHours(4) ? Now.AddHours(4) : o.InspectionDueAt), cancellationToken: ct);
                    await Notify(o.BuyerId, "sắp hết hạn kiểm tra cây", "Nếu có vấn đề, hãy khiếu nại trước khi hết hạn", o, ct);
                }
            });
        // BR-DSP-02: người bán không phản hồi → xử có lợi cho người mua.
        foreach (var o in await Orders.Find(x => x.Status == OrderStatus.Disputed && x.Dispute!.SellerRespondedAt == null && x.Dispute.SellerDueAt < Now).Limit(200).ToListAsync(ct))
            await Try(() => ResolveAsync(o, DisputeOutcome.FullRefund, null, "Người bán không phản hồi khiếu nại trong 48 giờ", "system", sellerLost: true, ct));
        // BR-DSP-04: 7 ngày sau khi người mua gửi trả có vận đơn.
        foreach (var o in await Orders.Find(x => x.Status == OrderStatus.AwaitingReturn && x.Dispute!.ReturnShippedAt < Now.AddDays(-7)).Limit(200).ToListAsync(ct))
            await Try(() => ConfirmReturnReceivedAsync("system", o, ct));
        await ApplySellerRiskAsync(ct);
    }

    /// <summary>BR-ESC-20: tin bị gỡ / người bán bị khóa khi còn đơn mở.</summary>
    async Task ApplySellerRiskAsync(CancellationToken ct)
    {
        var open = await Orders.Find(x => Open.Contains(x.Status) || (x.Status == OrderStatus.Completed || x.Status == OrderStatus.PartiallyRefunded) && !x.SettlementHold)
            .Limit(1000).ToListAsync(ct);
        if (open.Count == 0) return;
        var sellerIds = open.Select(o => o.SellerId).Distinct().ToList();
        var bad = (await users.Users.Find(u => sellerIds.Contains(u.Id) && (u.Status == UserStatus.Locked || u.Status == UserStatus.Banned)).ToListAsync(ct))
            .Select(u => u.Id).ToHashSet();
        var listingIds = open.Select(o => o.ListingId).Distinct().ToList();
        var removed = (await listings.Listings.Find(l => listingIds.Contains(l.Id) && l.Status == ListingStatus.Removed).ToListAsync(ct)).Select(l => l.Id).ToHashSet();
        foreach (var o in open.Where(o => bad.Contains(o.SellerId) || removed.Contains(o.ListingId)))
            await Try(async () =>
            {
                if (o.Status == OrderStatus.AwaitingPayment)
                {
                    await MoveAsync(o, [OrderStatus.AwaitingPayment], OrderStatus.Cancelled, "system", "Tin/người bán bị xử lý", b => b.Set(x => x.CancelReason, "Tin/người bán bị xử lý"), ct);
                    await MoveStockAsync(o, -o.Quantity, 0, ct);
                }
                else if (o.Status is OrderStatus.AwaitingSellerConfirm or OrderStatus.Paid)
                    await RefundAndCloseAsync(o, [o.Status], "system", "Tin bị gỡ hoặc người bán bị khóa", false, false, ct);
                else if (!o.SettlementHold)
                    await SetHoldAsync(o.Id, true, ct);
            });
    }

    static async Task Try(Func<Task> action)
    {
        try { await action(); }
        catch (DomainException) { /* đơn đã đổi trạng thái bởi thao tác khác — bỏ qua */ }
    }
}

public class EscrowWorker(IServiceScopeFactory scopes, ILogger<EscrowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EscrowService>().RunDeadlinesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi job Giao dịch đảm bảo");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
