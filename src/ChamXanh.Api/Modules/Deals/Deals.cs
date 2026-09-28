using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Escrow;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Notifications;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Deals;

// Báo giá cho tin Cần mua và Yêu cầu thuê có lịch cho tin Cho thuê (tài liệu 03 §2, 04 §1).

public enum QuoteStatus { Sent, Selected, Declined, Withdrawn }

public class Quote
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ListingId { get; set; } = default!; // tin Cần mua
    public string BuyerId { get; set; } = default!;   // người đăng tin Cần mua
    public string SellerId { get; set; } = default!;  // người gửi báo giá
    public long UnitPrice { get; set; }
    public int Quantity { get; set; }
    public string? Note { get; set; }
    public List<string> MediaIds { get; set; } = [];
    public DateTime? AvailableFrom { get; set; }
    [BsonRepresentation(BsonType.String)] public QuoteStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum RentalStatus { Requested, Accepted, Declined, Cancelled, Active, Returned }

public class RentalBooking
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ListingId { get; set; } = default!;
    public string OwnerId { get; set; } = default!;
    public string RenterId { get; set; } = default!;
    public int Quantity { get; set; }
    /// <summary>Ngày theo lịch VN, [StartDate, EndDate] tính cả hai đầu.</summary>
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public long PriceEstimate { get; set; }
    public long Deposit { get; set; }
    public string? Note { get; set; }
    public string? DeliveryAddress { get; set; }
    [BsonRepresentation(BsonType.String)] public RentalStatus Status { get; set; }
    public string? ResponseNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public record QuoteRequest(long UnitPrice, int Quantity, string? Note, List<string>? MediaIds, DateTime? AvailableFrom);
public record QuoteOrderRequest(DeliveryMethod Delivery, long? ShippingFee, string? DeliveryAddress, DateTime? PickupDate, int? ShipWithinDays);
public record RentalRequest(DateTime StartDate, DateTime EndDate, int? Quantity, string? Note, string? DeliveryAddress);
public record RentalResponse(bool Accept, string? Note);

public class DealsService(IMongoDatabase db, TimeProvider clock, ListingService listings, UserService users, MediaService media,
    NotificationService notifications, EscrowService escrow)
{
    public IMongoCollection<Quote> Quotes { get; } = db.GetCollection<Quote>("quotes");
    public IMongoCollection<RentalBooking> Rentals { get; } = db.GetCollection<RentalBooking>("rentalBookings");
    DateTime Now => clock.GetUtcNow().UtcDateTime;
    static readonly RentalStatus[] Holding = [RentalStatus.Accepted, RentalStatus.Active];

    public async Task EnsureIndexesAsync()
    {
        await Quotes.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Quote>(Builders<Quote>.IndexKeys.Ascending(q => q.ListingId).Ascending(q => q.SellerId), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<Quote>(Builders<Quote>.IndexKeys.Ascending(q => q.SellerId).Descending(q => q.CreatedAt)),
        ]);
        await Rentals.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<RentalBooking>(Builders<RentalBooking>.IndexKeys.Ascending(r => r.ListingId).Ascending(r => r.StartDate)),
            new CreateIndexModel<RentalBooking>(Builders<RentalBooking>.IndexKeys.Ascending(r => r.RenterId).Descending(r => r.CreatedAt)),
            new CreateIndexModel<RentalBooking>(Builders<RentalBooking>.IndexKeys.Ascending(r => r.OwnerId).Descending(r => r.CreatedAt)),
        ]);
    }

    // ---------- Báo giá ----------

    public async Task<Quote> SendQuoteAsync(string sellerId, string listingId, QuoteRequest req, CancellationToken ct)
    {
        var l = await listings.GetAsync(listingId, ct);
        if (l.Type != ListingType.Buy || l.Status != ListingStatus.Active) throw new DomainException("NOT_BUY_LISTING", "Chỉ gửi báo giá cho tin Cần mua đang hiển thị");
        if (l.SellerId == sellerId) throw new DomainException("CANNOT_QUOTE_OWN", "Không thể báo giá cho tin của mình");
        await users.RequireActiveAsync(sellerId, ct);
        if (req.UnitPrice is <= 0 or > 10_000_000_000 || req.Quantity is < 1 or > 100_000) throw new DomainException("INVALID_QUOTE", "Giá hoặc số lượng không hợp lệ");
        if (req.Note?.Length > 1000) throw new DomainException("INVALID_NOTE", "Ghi chú tối đa 1000 ký tự");
        var mediaIds = (req.MediaIds ?? []).Take(6).ToList();
        await media.GetOwnedAsync(mediaIds, sellerId, ct);
        var existing = await Quotes.Find(q => q.ListingId == listingId && q.SellerId == sellerId).FirstOrDefaultAsync(ct);
        if (existing is { Status: QuoteStatus.Selected }) throw DomainException.Conflict("QUOTE_SELECTED", "Báo giá đã được chọn, không sửa được");
        var q = existing ?? new Quote { ListingId = listingId, BuyerId = l.SellerId, SellerId = sellerId, CreatedAt = Now };
        q.UnitPrice = req.UnitPrice; q.Quantity = req.Quantity; q.Note = req.Note?.Trim(); q.MediaIds = mediaIds;
        q.AvailableFrom = req.AvailableFrom; q.Status = QuoteStatus.Sent; q.UpdatedAt = Now;
        await Quotes.ReplaceOneAsync(x => x.Id == q.Id, q, new ReplaceOptions { IsUpsert = true }, ct);
        await media.AttachAsync(mediaIds, $"quote:{q.Id}", ct);
        await notifications.SendAsync(l.SellerId, "listing.quote", "Có báo giá mới cho tin Cần mua", $"{l.Title}: {req.UnitPrice:N0}đ × {req.Quantity}", $"/tin/{l.Id}", ct);
        return q;
    }

    public async Task<Quote> SelectQuoteAsync(string buyerId, string quoteId, CancellationToken ct)
    {
        var q = await Quotes.Find(x => x.Id == quoteId && x.BuyerId == buyerId).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("báo giá");
        var res = await Quotes.UpdateOneAsync(x => x.Id == quoteId && x.Status == QuoteStatus.Sent,
            Builders<Quote>.Update.Set(x => x.Status, QuoteStatus.Selected).Set(x => x.UpdatedAt, Now), cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("INVALID_STATE", "Báo giá không còn hiệu lực");
        await notifications.SendAsync(q.SellerId, "listing.quote_selected", "Báo giá của bạn đã được chọn", "Người mua sẽ liên hệ hoặc tạo đơn đảm bảo", $"/tin/{q.ListingId}", ct);
        return (await Quotes.Find(x => x.Id == quoteId).FirstAsync(ct));
    }

    public async Task<EscrowOrder> OrderFromQuoteAsync(string buyerId, string quoteId, QuoteOrderRequest req, CancellationToken ct)
    {
        var q = await Quotes.Find(x => x.Id == quoteId && x.BuyerId == buyerId).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("báo giá");
        if (q.Status != QuoteStatus.Selected) throw DomainException.Conflict("QUOTE_NOT_SELECTED", "Hãy chọn báo giá trước khi tạo đơn");
        var l = await listings.GetAsync(q.ListingId, ct);
        return await escrow.CreateFromQuoteAsync(l, q.SellerId, q.Id, q.UnitPrice, q.Quantity, req.Delivery, req.ShippingFee, req.DeliveryAddress,
            req.PickupDate, req.ShipWithinDays, ct);
    }

    // ---------- Thuê cây có lịch ----------

    public static int Units(RentUnit unit, int days) => unit switch
    {
        RentUnit.Day => days,
        RentUnit.Week => (int)Math.Ceiling(days / 7.0),
        RentUnit.Month => (int)Math.Ceiling(days / 30.0),
        _ => 1, // TetSeason: trọn mùa
    };

    public async Task<int> BookedAsync(string listingId, DateTime start, DateTime end, string? excludeId, CancellationToken ct)
    {
        var overlapping = await Rentals.Find(r => r.ListingId == listingId && Holding.Contains(r.Status) && r.StartDate <= end && r.EndDate >= start && r.Id != excludeId)
            .ToListAsync(ct);
        // Số lượng đang cho thuê cao nhất trong khoảng: kiểm từng ngày.
        var max = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
            max = Math.Max(max, overlapping.Where(r => r.StartDate <= d && r.EndDate >= d).Sum(r => r.Quantity));
        return max;
    }

    public async Task<RentalBooking> RequestRentalAsync(string renterId, string listingId, RentalRequest req, CancellationToken ct)
    {
        var l = await listings.GetAsync(listingId, ct);
        if (l.Type != ListingType.Rent || l.Status != ListingStatus.Active || l.Rent is null) throw new DomainException("NOT_RENT_LISTING", "Tin này không cho thuê");
        if (l.SellerId == renterId) throw new DomainException("CANNOT_RENT_OWN", "Không thể thuê cây của mình");
        await users.RequireActiveAsync(renterId, ct);
        var start = req.StartDate.Date; var end = req.EndDate.Date;
        var today = Now.AddHours(7).Date;
        if (start < today || end < start || (end - start).TotalDays > 366) throw new DomainException("INVALID_DATES", "Khoảng ngày thuê không hợp lệ");
        var days = (int)(end - start).TotalDays + 1;
        var units = Units(l.Rent.Unit, days);
        if (units < l.Rent.MinUnits) throw new DomainException("BELOW_MIN_UNITS", $"Thuê tối thiểu {l.Rent.MinUnits} {l.Rent.Unit}");
        var qty = Math.Max(1, req.Quantity ?? 1);
        if (await BookedAsync(listingId, start, end, null, ct) + qty > l.Quantity)
            throw DomainException.Conflict("NOT_AVAILABLE", "Không đủ số lượng trong khoảng ngày này");
        var r = new RentalBooking
        {
            ListingId = listingId, OwnerId = l.SellerId, RenterId = renterId, Quantity = qty, StartDate = start, EndDate = end,
            PriceEstimate = l.Rent.PricePerUnit * units * qty, Deposit = l.Rent.Deposit * qty, Note = req.Note?.Trim(),
            DeliveryAddress = req.DeliveryAddress?.Trim(), Status = RentalStatus.Requested, CreatedAt = Now, UpdatedAt = Now,
        };
        await Rentals.InsertOneAsync(r, cancellationToken: ct);
        await notifications.SendAsync(l.SellerId, "listing.rental", "Có yêu cầu thuê mới", $"{l.Title}: {start:dd/MM} – {end:dd/MM}", $"/thue/{r.Id}", ct);
        return r;
    }

    public async Task<RentalBooking> RespondRentalAsync(string ownerId, string id, RentalResponse req, CancellationToken ct)
    {
        var r = await Rentals.Find(x => x.Id == id && x.OwnerId == ownerId).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("yêu cầu thuê");
        if (r.Status != RentalStatus.Requested) throw DomainException.Conflict("INVALID_STATE", "Yêu cầu đã được xử lý");
        if (req.Accept)
        {
            var l = await listings.GetAsync(r.ListingId, ct);
            if (await BookedAsync(r.ListingId, r.StartDate, r.EndDate, r.Id, ct) + r.Quantity > l.Quantity)
                throw DomainException.Conflict("NOT_AVAILABLE", "Không còn đủ cây trong khoảng ngày này");
        }
        return await MoveRentalAsync(r, RentalStatus.Requested, req.Accept ? RentalStatus.Accepted : RentalStatus.Declined, req.Note, r.RenterId,
            req.Accept ? "Chủ cây đã nhận lịch thuê" : "Chủ cây từ chối yêu cầu thuê", ct);
    }

    public async Task<RentalBooking> MoveRentalAsync(RentalBooking r, RentalStatus from, RentalStatus to, string? note, string notifyUser, string title, CancellationToken ct)
    {
        var res = await Rentals.UpdateOneAsync(x => x.Id == r.Id && x.Status == from, Builders<RentalBooking>.Update
            .Set(x => x.Status, to).Set(x => x.ResponseNote, note ?? r.ResponseNote).Set(x => x.UpdatedAt, Now), cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("INVALID_STATE", "Trạng thái đã thay đổi, vui lòng tải lại");
        await notifications.SendAsync(notifyUser, "listing.rental", title, note, $"/thue/{r.Id}", ct);
        return await Rentals.Find(x => x.Id == r.Id).FirstAsync(ct);
    }
}

public static class DealsEndpoints
{
    public static void MapDeals(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Deals").RequireAuthorization(Policies.Member);

        g.MapPost("/listings/{id}/quotes", (string id, QuoteRequest req, ClaimsPrincipal p, DealsService svc, CancellationToken ct) => svc.SendQuoteAsync(p.UserId(), id, req, ct));
        // Người đăng tin Cần mua xem toàn bộ báo giá; người báo giá chỉ xem báo giá của mình.
        g.MapGet("/listings/{id}/quotes", async (string id, ClaimsPrincipal p, DealsService svc, UserService users, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var list = await svc.Quotes.Find(q => q.ListingId == id && (q.BuyerId == userId || q.SellerId == userId) && q.Status != QuoteStatus.Withdrawn)
                .SortBy(q => q.UnitPrice).ToListAsync(ct);
            var ids = list.Select(q => q.SellerId).Distinct().ToList();
            var sellers = (await users.Users.Find(u => ids.Contains(u.Id)).ToListAsync(ct)).ToDictionary(u => u.Id, PublicUser.From);
            return list.Select(q => new { quote = q, seller = sellers.GetValueOrDefault(q.SellerId), photos = q.MediaIds.Select(m => $"/media/{m}/thumb.webp") });
        });
        g.MapGet("/me/quotes", async (ClaimsPrincipal p, DealsService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await svc.Quotes.Find(q => q.SellerId == userId).SortByDescending(q => q.UpdatedAt).Limit(100).ToListAsync(ct);
        });
        g.MapPost("/quotes/{id}/select", (string id, ClaimsPrincipal p, DealsService svc, CancellationToken ct) => svc.SelectQuoteAsync(p.UserId(), id, ct));
        g.MapPost("/quotes/{id}/decline", async (string id, ClaimsPrincipal p, DealsService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.Quotes.UpdateOneAsync(q => q.Id == id && q.BuyerId == userId && q.Status == QuoteStatus.Sent,
                Builders<Quote>.Update.Set(q => q.Status, QuoteStatus.Declined).Set(q => q.UpdatedAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
            return Results.NoContent();
        });
        g.MapDelete("/quotes/{id}", async (string id, ClaimsPrincipal p, DealsService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.Quotes.UpdateOneAsync(q => q.Id == id && q.SellerId == userId && q.Status == QuoteStatus.Sent,
                Builders<Quote>.Update.Set(q => q.Status, QuoteStatus.Withdrawn), cancellationToken: ct);
            return Results.NoContent();
        });
        g.MapPost("/quotes/{id}/order", (string id, QuoteOrderRequest req, ClaimsPrincipal p, DealsService svc, CancellationToken ct) => svc.OrderFromQuoteAsync(p.UserId(), id, req, ct));

        // Lịch thuê công khai: các khoảng ngày đã kín (không lộ người thuê).
        app.MapGet("/api/listings/{id}/rental-calendar", async (string id, DateTime? from, DateTime? to, DealsService svc, ListingService listings, TimeProvider clock, CancellationToken ct) =>
        {
            var l = await listings.GetAsync(id, ct);
            var start = (from ?? clock.GetUtcNow().UtcDateTime.AddHours(7)).Date;
            var end = (to ?? start.AddDays(90)).Date;
            if ((end - start).TotalDays > 366) end = start.AddDays(366);
            var bookings = await svc.Rentals.Find(r => r.ListingId == id && (r.Status == RentalStatus.Accepted || r.Status == RentalStatus.Active) && r.StartDate <= end && r.EndDate >= start).ToListAsync(ct);
            var days = new List<object>();
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                var booked = bookings.Where(r => r.StartDate <= d && r.EndDate >= d).Sum(r => r.Quantity);
                if (booked > 0) days.Add(new { date = d, available = Math.Max(0, l.Quantity - booked) });
            }
            return new { quantity = l.Quantity, l.Rent, busyDays = days };
        }).WithTags("Deals");
        g.MapPost("/listings/{id}/rentals", (string id, RentalRequest req, ClaimsPrincipal p, DealsService svc, CancellationToken ct) => svc.RequestRentalAsync(p.UserId(), id, req, ct));
        g.MapGet("/me/rentals", async (string? role, ClaimsPrincipal p, DealsService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var f = role == "owner" ? Builders<RentalBooking>.Filter.Eq(r => r.OwnerId, userId) : Builders<RentalBooking>.Filter.Eq(r => r.RenterId, userId);
            return await svc.Rentals.Find(f).SortByDescending(r => r.CreatedAt).Limit(100).ToListAsync(ct);
        });
        g.MapGet("/rentals/{id}", async (string id, ClaimsPrincipal p, DealsService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await svc.Rentals.Find(r => r.Id == id && (r.OwnerId == userId || r.RenterId == userId)).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("yêu cầu thuê");
        });
        g.MapPost("/rentals/{id}/respond", (string id, RentalResponse req, ClaimsPrincipal p, DealsService svc, CancellationToken ct) => svc.RespondRentalAsync(p.UserId(), id, req, ct));
        g.MapPost("/rentals/{id}/cancel", async (string id, ClaimsPrincipal p, DealsService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var r = await svc.Rentals.Find(x => x.Id == id && (x.OwnerId == userId || x.RenterId == userId)).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("yêu cầu thuê");
            if (r.Status is not (RentalStatus.Requested or RentalStatus.Accepted)) throw DomainException.Conflict("INVALID_STATE", "Không thể hủy lúc này");
            return await svc.MoveRentalAsync(r, r.Status, RentalStatus.Cancelled, null, userId == r.OwnerId ? r.RenterId : r.OwnerId, "Lịch thuê đã bị hủy", ct);
        });
        // Chủ cây đánh dấu đã giao cây / đã nhận lại cây.
        g.MapPost("/rentals/{id}/handover", async (string id, ClaimsPrincipal p, DealsService svc, CancellationToken ct) =>
        {
            var r = await svc.Rentals.Find(x => x.Id == id && x.OwnerId == p.UserId()).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("yêu cầu thuê");
            return await svc.MoveRentalAsync(r, RentalStatus.Accepted, RentalStatus.Active, null, r.RenterId, "Cây thuê đã được giao", ct);
        });
        g.MapPost("/rentals/{id}/returned", async (string id, ClaimsPrincipal p, DealsService svc, CancellationToken ct) =>
        {
            var r = await svc.Rentals.Find(x => x.Id == id && x.OwnerId == p.UserId()).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("yêu cầu thuê");
            return await svc.MoveRentalAsync(r, RentalStatus.Active, RentalStatus.Returned, null, r.RenterId, "Đã trả cây thuê", ct);
        });
    }
}
