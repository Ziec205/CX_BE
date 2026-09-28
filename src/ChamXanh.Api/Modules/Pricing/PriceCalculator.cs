namespace ChamXanh.Api.Modules.Pricing;

public record QuoteContext(string CategoryId, IReadOnlyCollection<string> CollectionIds, DateTime At);

public record PriceQuote(string ServiceCode, int? Days, string PriceGroup, long BasePrice, decimal Factor, long FinalPrice, int PriceBookVersion);

public static class PriceCalculator
{
    public static string? ResolvePriceGroup(PriceBook book, string categoryId) =>
        book.PriceGroups.FirstOrDefault(g => g.CategoryIds.Contains(categoryId))?.Code;

    /// <summary>BR-PRC-08: nhiều hệ số chồng nhau thì lấy hệ số lớn nhất, không nhân dồn.</summary>
    public static decimal ResolveFactor(PriceBook book, string serviceCode, string priceGroup, QuoteContext ctx)
    {
        var matching = book.Multipliers.Where(m =>
            m.From <= ctx.At && ctx.At < m.To
            && (m.Services.Count == 0 || m.Services.Contains(serviceCode))
            && (m.PriceGroups.Contains(priceGroup)
                || m.CategoryIds.Contains(ctx.CategoryId)
                || m.CollectionIds.Intersect(ctx.CollectionIds).Any()));
        return matching.Select(m => m.Factor).DefaultIfEmpty(1m).Max();
    }

    public static PriceQuote? Quote(PriceBook book, string serviceCode, int? days, QuoteContext ctx)
    {
        var group = ResolvePriceGroup(book, ctx.CategoryId);
        if (group is null) return null;
        var service = book.ListingServices.FirstOrDefault(s => s.Code == serviceCode && s.Enabled && s.Days == days);
        if (service is null || !service.Prices.TryGetValue(group, out var basePrice)) return null;

        var factor = ResolveFactor(book, serviceCode, group, ctx);
        var final = (long)Math.Ceiling(basePrice * factor); // làm tròn lên số Xu nguyên
        return new(serviceCode, days, group, basePrice, factor, final, book.Version);
    }

    /// <summary>Phí Giao dịch đảm bảo, tính trên tiền cây, không tính phí ship (BR-ESC-04, L28).</summary>
    public static long EscrowFee(EscrowFee fee, long itemAmountVnd)
    {
        var raw = (long)Math.Ceiling(itemAmountVnd * fee.Pct / 100m);
        return Math.Clamp(raw, fee.MinVnd, fee.MaxVnd);
    }
}
