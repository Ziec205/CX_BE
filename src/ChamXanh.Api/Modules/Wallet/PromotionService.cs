using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Pricing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Wallet;

public enum PromotionStatus { Active, Completed, Cancelled }

public class ListingPromotion
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ListingId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    public string Service { get; set; } = default!;
    public int? Days { get; set; }
    public PriceQuote PriceSnapshot { get; set; } = default!; // BR-PRC-05
    public string LedgerEntryId { get; set; } = default!;
    public DateTime StartAt { get; set; }
    public DateTime? EndAt { get; set; }
    public int RemainingBumps { get; set; }
    public DateTime? NextBumpAt { get; set; }
    public TimeSpan? BumpInterval { get; set; }
    [BsonRepresentation(BsonType.String)] public PromotionStatus Status { get; set; } = PromotionStatus.Active;
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record BuyPromotionRequest(string Service, int? Days, int ExpectedPriceBookVersion, long ExpectedPrice, string IdempotencyKey, string? LabelName = null);

public class PromotionService(IMongoDatabase db, IMongoClient client, WalletService wallets, PricingService pricing, ListingService listings, TimeProvider clock)
{
    public IMongoCollection<ListingPromotion> Promotions { get; } = db.GetCollection<ListingPromotion>("listingPromotions");

    public Task EnsureIndexesAsync() => Promotions.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<ListingPromotion>(Builders<ListingPromotion>.IndexKeys.Ascending(p => p.Status).Ascending(p => p.NextBumpAt)),
        new CreateIndexModel<ListingPromotion>(Builders<ListingPromotion>.IndexKeys.Ascending(p => p.ListingId)),
    ]);

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ListingPromotion> BuyAsync(string listingId, string userId, BuyPromotionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 100)
            throw new DomainException("IDEMPOTENCY_KEY_REQUIRED", "Thiếu khóa idempotency");
        var key = $"promo:{userId}:{req.IdempotencyKey}";
        var listing = await listings.GetOwnedAsync(listingId, userId, ct);

        // BR-XU-07 (4): chỉ bán dịch vụ cho tin đang hiển thị; tin Tặng không được đẩy (BR-GIV-02).
        if (listing.Status != ListingStatus.Active) throw DomainException.Conflict("INVALID_STATE", "Chỉ mua dịch vụ cho tin đang hiển thị");
        if (listing.Type == ListingType.Give) throw new DomainException("NOT_ALLOWED", "Tin tặng/trao đổi không dùng được dịch vụ đẩy tin");

        var quote = await pricing.ConfirmQuoteAsync(req.Service, req.Days, new QuoteContext(listing.CategoryId, listing.CollectionIds, Now),
            req.ExpectedPriceBookVersion, req.ExpectedPrice, ct);
        var service = (await pricing.GetActiveAsync(ct))!.ListingServices.First(s => s.Code == req.Service && s.Days == req.Days
            && (req.Service != ListingServiceCodes.Label || req.LabelName is null || s.LabelName == req.LabelName));

        if (req.Service == ListingServiceCodes.Priority) await EnsurePrioritySlotAsync(listing, quote.PriceGroup, ct);

        ListingPromotion? promo = null;
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        await session.WithTransactionAsync(async (s, token) =>
        {
            var (entry, duplicate) = await wallets.DebitAsync(s, userId, quote.FinalPrice, key, "listing", listingId,
                $"{req.Service}{(req.Days is { } d ? $" {d} ngày" : "")}", token);
            if (duplicate)
            {
                promo = await Promotions.Find(s, p => p.LedgerEntryId == entry.Id).FirstOrDefaultAsync(token);
                return true;
            }
            promo = Build(listing, req, service, quote, entry.Id);
            await Promotions.InsertOneAsync(s, promo, cancellationToken: token);
            await ApplyAsync(s, listing, promo, service, token);
            return true;
        }, cancellationToken: ct);
        return promo!;
    }

    ListingPromotion Build(Listing listing, BuyPromotionRequest req, ListingServicePrice service, PriceQuote quote, string ledgerId)
    {
        var now = Now;
        var p = new ListingPromotion
        {
            ListingId = listing.Id, SellerId = listing.SellerId, Service = req.Service, Days = req.Days, PriceSnapshot = quote,
            LedgerEntryId = ledgerId, StartAt = now, CreatedAt = now,
        };
        switch (req.Service)
        {
            case ListingServiceCodes.Bump:
                p.Status = PromotionStatus.Completed;
                p.EndAt = now;
                break;
            case ListingServiceCodes.AutoBump:
                var perDay = service.PerDay ?? 3;
                p.RemainingBumps = perDay * (service.Days ?? 7) - 1; // lượt đầu đẩy ngay
                p.BumpInterval = TimeSpan.FromHours(24.0 / perDay);
                p.NextBumpAt = now + p.BumpInterval;
                p.EndAt = now.AddDays(service.Days ?? 7);
                break;
            default: // PRIORITY, LABEL
                var from = req.Service == ListingServiceCodes.Priority && listing.PriorityUntil > now ? listing.PriorityUntil.Value
                    : req.Service == ListingServiceCodes.Label && listing.HighlightUntil > now ? listing.HighlightUntil.Value : now;
                p.StartAt = from;
                p.EndAt = from.AddDays(service.Days ?? 7);
                break;
        }
        return p;
    }

    async Task ApplyAsync(IClientSessionHandle s, Listing listing, ListingPromotion p, ListingServicePrice service, CancellationToken ct)
    {
        var u = Builders<Listing>.Update.Inc(l => l.Version, 1);
        if (p.Service is ListingServiceCodes.Bump or ListingServiceCodes.AutoBump) u = u.Set(l => l.BumpedAt, Now); // BR-LST-15: đẩy trả phí
        if (p.Service == ListingServiceCodes.Priority) u = u.Set(l => l.PriorityUntil, p.EndAt);
        if (p.Service == ListingServiceCodes.Label) u = u.Set(l => l.HighlightLabel, service.LabelName).Set(l => l.HighlightUntil, p.EndAt);
        // BR-XU-07 (1): kéo dài hạn tin để phủ hết thời gian dịch vụ
        if (p.EndAt is { } end && (listing.ExpiresAt is null || listing.ExpiresAt < end)) u = u.Set(l => l.ExpiresAt, end.AddDays(1));
        await listings.Listings.UpdateOneAsync(s, l => l.Id == listing.Id, u, cancellationToken: ct);
    }

    /// <summary>BR-SRC-05: giới hạn số tin Ưu tiên đang chạy theo nhóm giá × tỉnh.</summary>
    async Task EnsurePrioritySlotAsync(Listing listing, string priceGroup, CancellationToken ct)
    {
        var book = (await pricing.GetActiveAsync(ct))!;
        if (listing.PriorityUntil > Now) return; // gia hạn tin đang chạy không tốn thêm suất
        var slots = book.PrioritySlots.Overrides.FirstOrDefault(o => o.PriceGroup == priceGroup && o.ProvinceId == listing.ProvinceId)?.Slots
            ?? book.PrioritySlots.Default;
        var categories = book.PriceGroups.First(g => g.Code == priceGroup).CategoryIds;
        var used = await listings.Listings.CountDocumentsAsync(l => l.Status == ListingStatus.Active && l.PriorityUntil > Now
            && l.ProvinceId == listing.ProvinceId && categories.Contains(l.CategoryId), cancellationToken: ct);
        if (used >= slots) throw DomainException.Conflict("NO_PRIORITY_SLOT", "Đã hết suất Tin Ưu tiên cho danh mục này tại tỉnh của bạn, vui lòng thử lại sau");
    }

    /// <summary>Job đẩy tự động. Tin không còn hiển thị do người bán thì hủy phần còn lại, không hoàn (BR-XU-07 (2));
    /// tin bị hệ thống tạm ẩn thì hoãn lượt (sẽ được cộng bù nếu không vi phạm).</summary>
    public async Task<int> RunAutoBumpsAsync(CancellationToken ct)
    {
        var now = Now;
        var due = await Promotions.Find(p => p.Status == PromotionStatus.Active && p.Service == ListingServiceCodes.AutoBump && p.NextBumpAt <= now)
            .Limit(500).ToListAsync(ct);
        foreach (var p in due)
        {
            var listing = await listings.Listings.Find(l => l.Id == p.ListingId).FirstOrDefaultAsync(ct);
            var u = Builders<ListingPromotion>.Update;
            if (listing is null || listing.Status is ListingStatus.Hidden or ListingStatus.SoldOut or ListingStatus.Deleted or ListingStatus.Expired or ListingStatus.Removed)
            {
                await Promotions.UpdateOneAsync(x => x.Id == p.Id, u.Set(x => x.Status, PromotionStatus.Cancelled)
                    .Set(x => x.CancelReason, $"Tin ở trạng thái {listing?.Status}"), cancellationToken: ct);
                continue;
            }
            if (listing.Status != ListingStatus.Active)
            {
                await Promotions.UpdateOneAsync(x => x.Id == p.Id, u.Set(x => x.NextBumpAt, now + p.BumpInterval), cancellationToken: ct);
                continue;
            }
            await listings.Listings.UpdateOneAsync(l => l.Id == p.ListingId, Builders<Listing>.Update.Set(l => l.BumpedAt, now), cancellationToken: ct);
            var left = p.RemainingBumps - 1;
            await Promotions.UpdateOneAsync(x => x.Id == p.Id, left <= 0
                ? u.Set(x => x.RemainingBumps, 0).Set(x => x.Status, PromotionStatus.Completed)
                : u.Set(x => x.RemainingBumps, left).Set(x => x.NextBumpAt, now + p.BumpInterval), cancellationToken: ct);
        }
        return due.Count;
    }

    public Task<List<ListingPromotion>> ForListingAsync(string listingId, CancellationToken ct) =>
        Promotions.Find(p => p.ListingId == listingId).SortByDescending(p => p.CreatedAt).ToListAsync(ct);
}
