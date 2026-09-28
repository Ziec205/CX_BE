using System.Text.RegularExpressions;
using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Media;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace ChamXanh.Api.Modules.Listings;

public enum ListingSort { Newest, PriceAsc, PriceDesc, Nearest }

public record ListingQuery(
    string? Q = null, ListingType? Type = null, string? CategoryId = null, string? RootCategoryId = null, string? SpeciesId = null,
    string? ProvinceId = null, long? PriceMin = null, long? PriceMax = null, bool? GardenOnly = null, bool? EscrowOnly = null,
    bool? RealPhotoOnly = null, string? Pickup = null, int? PostedWithinDays = null, double? Lat = null, double? Lng = null,
    double? RadiusKm = null, ListingSort Sort = ListingSort.Newest, int Page = 1, int PageSize = 20,
    Dictionary<string, string>? Attr = null, string? SellerId = null, string? Use = null);

public record SellerSummary(string Id, string DisplayName, string? AvatarMediaId, bool IsGarden, bool IsVerifiedGarden, bool IsProSeller);

public record ListingCard(
    string Id, string Type, string Title, long? Price, string PriceMode, long? PriceRefMin, long? PriceRefMax,
    long? BudgetMin, long? BudgetMax, RentTerms? Rent, string CategoryId, string? SpeciesId, string ProvinceId, string? WardId,
    string? ThumbUrl, int PhotoCount, bool RealPhoto, bool Escrow, bool IsPriority, string? Highlight, DateTime? BumpedAt,
    string Status, int Available, string Unit, double? DistanceKm, SellerSummary Seller);

public record SearchResult(List<ListingCard> Priority, List<ListingCard> Items, long Total, int Page, int PageSize);

public class ListingSearch(ListingService listings, CatalogService catalog, UserService users, ListingOptions options, TimeProvider clock)
{
    const int PrioritySlotsPerPage = 3;    // BR-SRC-01
    const int MaxPriorityPerSeller = 2;    // BR-SRC-03

    public async Task<SearchResult> SearchAsync(ListingQuery q, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 50);
        var filter = await BuildFilterAsync(q, now, ct);
        var coll = listings.Listings;

        List<Listing> items;
        long total = await coll.CountDocumentsAsync(filter, cancellationToken: ct);
        if (q.Sort == ListingSort.Nearest && q.Lat is { } lat && q.Lng is { } lng)
        {
            // $near đã sắp theo khoảng cách
            var near = filter & Builders<Listing>.Filter.NearSphere(l => l.Location, GeoJson.Point(GeoJson.Geographic(lng, lat)),
                maxDistance: (q.RadiusKm ?? 50) * 1000);
            items = await coll.Find(near).Skip((page - 1) * size).Limit(size).ToListAsync(ct);
        }
        else
        {
            var sort = q.Sort switch
            {
                ListingSort.PriceAsc => Builders<Listing>.Sort.Ascending(l => l.SortPrice).Descending(l => l.BumpedAt),
                ListingSort.PriceDesc => Builders<Listing>.Sort.Descending(l => l.SortPrice).Descending(l => l.BumpedAt),
                _ => Builders<Listing>.Sort.Descending(l => l.BumpedAt).Descending(l => l.Id),
            };
            items = await coll.Find(filter).Sort(sort).Skip((page - 1) * size).Limit(size).ToListAsync(ct);
        }

        // Khối Ưu tiên: chỉ tin VIP KHỚP bộ lọc (BR-SRC-02), xoay vòng ngẫu nhiên, tối đa 2 tin/người bán (BR-SRC-03).
        var priorityFilter = filter & Builders<Listing>.Filter.Gt(l => l.PriorityUntil, now);
        var pool = await coll.Aggregate().Match(priorityFilter).Sample(30).ToListAsync(ct);
        var priority = pool.GroupBy(l => l.SellerId).SelectMany(g => g.Take(MaxPriorityPerSeller))
            .OrderBy(_ => Random.Shared.Next()).Take(PrioritySlotsPerPage).ToList();

        var sellers = await SellerSummariesAsync(items.Concat(priority).Select(l => l.SellerId), ct);
        return new(
            priority.Select(l => ToCard(l, sellers[l.SellerId], now, q, isPriority: true)).ToList(),
            items.Select(l => ToCard(l, sellers[l.SellerId], now, q, l.PriorityUntil > now)).ToList(),
            total, page, size);
    }

    async Task<FilterDefinition<Listing>> BuildFilterAsync(ListingQuery q, DateTime now, CancellationToken ct)
    {
        var f = Builders<Listing>.Filter;
        var filter = f.Eq(l => l.Status, ListingStatus.Active);
        if (q.SellerId is not null) filter &= f.Eq(l => l.SellerId, q.SellerId);
        if (q.Type is { } t) filter &= f.Eq(l => l.Type, t);
        if (q.CategoryId is not null) filter &= f.Eq(l => l.CategoryId, q.CategoryId);
        if (q.RootCategoryId is not null) filter &= f.Eq(l => l.CategoryRootId, q.RootCategoryId);
        if (q.SpeciesId is not null) filter &= f.Eq(l => l.SpeciesId, q.SpeciesId);
        if (q.ProvinceId is not null) filter &= f.Eq(l => l.ProvinceId, q.ProvinceId);
        if (q.PriceMin is { } min) filter &= f.Gte(l => l.SortPrice, min);
        if (q.PriceMax is { } max) filter &= f.Lte(l => l.SortPrice, max);
        if (q.EscrowOnly == true) filter &= f.Eq(l => l.EscrowEnabled, true);
        if (q.Use is not null) filter &= f.AnyEq(l => l.Uses, q.Use);
        if (q.Pickup is not null) filter &= f.AnyEq(l => l.PickupOptions, q.Pickup);
        if (q.PostedWithinDays is { } d) filter &= f.Gte(l => l.FirstPublishedAt, now.AddDays(-d));
        if (q.RealPhotoOnly == true)
        {
            // BR-LST-09 tính khi đọc: mọi ảnh chụp trong app và không có ảnh nào cũ hơn N ngày
            filter &= f.Not(f.ElemMatch(l => l.Media, m => !m.CapturedInApp))
                & f.Not(f.ElemMatch(l => l.Media, m => m.CapturedAt == null || m.CapturedAt < now.AddDays(-options.RealPhotoMaxAgeDays)));
        }
        if (q.GardenOnly == true)
        {
            var gardenIds = await users.Users.Find(u => u.Flags.HasActivePlan).Project(u => u.Id).ToListAsync(ct);
            filter &= f.In(l => l.SellerId, gardenIds);
        }
        if (q.Lat is { } lat && q.Lng is { } lng && q.Sort != ListingSort.Nearest)
            filter &= f.GeoWithinCenterSphere(l => l.Location, lng, lat, (q.RadiusKm ?? 50) / 6378.1);

        // Bộ lọc động: attr[key]=value (chọn), attr[key]=min..max (số)
        if (q.Attr is { Count: > 0 } && q.CategoryId is not null)
        {
            var category = await catalog.GetCategoryAsync(q.CategoryId, ct);
            foreach (var (key, raw) in q.Attr)
            {
                var def = category.Attributes.FirstOrDefault(a => a.Key == key && a.Filterable);
                if (def is null) continue;
                var field = $"attributes.{key}";
                if (def.Type == AttributeType.Number)
                {
                    var parts = raw.Split("..");
                    if (parts.Length == 2)
                    {
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        if (double.TryParse(parts[0], inv, out var lo)) filter &= f.Gte(field, lo);
                        if (double.TryParse(parts[1], inv, out var hi)) filter &= f.Lte(field, hi);
                    }
                }
                else if (def.Type == AttributeType.Boolean && bool.TryParse(raw, out var b)) filter &= f.Eq(field, b);
                else filter &= f.Eq(field, raw);
            }
        }

        // Tìm không dấu + mở rộng theo tên khác của loài ("kim phát tài" → tin loài Kim tiền).
        var norm = VietnameseText.Normalize(q.Q);
        if (norm.Length > 0)
        {
            var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var textMatch = f.And(words.Select(w => f.Regex(l => l.SearchText, new BsonRegularExpression($@"\b{Regex.Escape(w)}"))));
            var speciesIds = await catalog.MatchSpeciesIdsAsync(norm, ct);
            filter &= speciesIds.Count > 0 ? f.Or(textMatch, f.In(l => l.SpeciesId, speciesIds)) : textMatch;
        }
        return filter;
    }

    public async Task<Dictionary<string, SellerSummary>> SellerSummariesAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var found = await users.Users.Find(u => list.Contains(u.Id)).ToListAsync(ct);
        return list.ToDictionary(id => id, id => found.FirstOrDefault(u => u.Id == id) is { } u
            ? new SellerSummary(u.Id, u.DisplayName, u.AvatarMediaId, u.Flags.HasActivePlan, u.Flags.HasVerifiedGarden, u.Flags.IsProSeller)
            : new SellerSummary(id, "Người dùng", null, false, false, false));
    }

    public ListingCard ToCard(Listing l, SellerSummary seller, DateTime now, ListingQuery? q = null, bool isPriority = false)
    {
        double? distance = null;
        if (q?.Lat is { } lat && q.Lng is { } lng && l.Location is { } loc)
            distance = Math.Round(HaversineKm(lat, lng, loc.Coordinates.Latitude, loc.Coordinates.Longitude), 1);
        return new(l.Id, l.Type.ToString(), l.Title, l.Price, l.PriceMode.ToString(), l.PriceRefMin, l.PriceRefMax,
            l.BudgetMin, l.BudgetMax, l.Rent, l.CategoryId, l.SpeciesId, l.ProvinceId, l.WardId,
            l.Media.Count > 0 ? $"/media/{l.Media[0].MediaId}/card.webp" : null, l.Media.Count,
            ListingRules.HasRealPhotoBadge(l.Media, now, options.RealPhotoMaxAgeDays), l.EscrowEnabled, isPriority,
            l.HighlightUntil > now ? l.HighlightLabel : null, l.BumpedAt, l.Status.ToString(), l.Available, l.Unit, distance, seller);
    }

    static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lon2 - lon1) / 2), 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
