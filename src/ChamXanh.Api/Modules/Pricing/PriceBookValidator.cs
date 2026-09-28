namespace ChamXanh.Api.Modules.Pricing;

public record ValidationIssue(string Field, string Message);

public record ValidationResult(List<ValidationIssue> Errors, List<ValidationIssue> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

// Tài liệu 08 §3: Lỗi chặn lưu, Cảnh báo cho lưu nhưng người duyệt phải xác nhận.
public static class PriceBookValidator
{
    public static ValidationResult Validate(PriceBook book, PriceBook? active = null)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var groupCodes = book.PriceGroups.Select(g => g.Code).ToHashSet();

        // Mỗi danh mục thuộc đúng 1 nhóm giá.
        var duplicatedCategories = book.PriceGroups.SelectMany(g => g.CategoryIds)
            .GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var c in duplicatedCategories)
            errors.Add(new("priceGroups", $"Danh mục '{c}' thuộc nhiều hơn 1 nhóm giá"));

        foreach (var s in book.ListingServices)
        {
            foreach (var (group, price) in s.Prices)
            {
                if (price < 0) errors.Add(new($"listingServices.{s.Code}", $"Giá nhóm {group} phải ≥ 0"));
                if (!groupCodes.Contains(group)) errors.Add(new($"listingServices.{s.Code}", $"Nhóm giá '{group}' không tồn tại"));
            }
            foreach (var g in groupCodes.Where(g => !s.Prices.ContainsKey(g)))
                errors.Add(new($"listingServices.{s.Code}", $"Thiếu giá cho nhóm {g}"));
        }

        foreach (var m in book.Multipliers)
        {
            if (m.Factor < 0.5m || m.Factor > 3m) errors.Add(new($"multipliers.{m.Name}", "Hệ số phải trong khoảng 0,5–3,0"));
            if (m.From >= m.To) errors.Add(new($"multipliers.{m.Name}", "Ngày bắt đầu phải trước ngày kết thúc"));
        }

        foreach (var p in book.TopUpPackages)
        {
            if (p.PriceVnd <= 0 || p.Xu <= 0) errors.Add(new($"topUpPackages.{p.Code}", "Giá và Xu nạp phải > 0"));
            if (p.BonusXu * 2 > p.Xu) errors.Add(new($"topUpPackages.{p.Code}", "Xu thưởng không vượt quá 50% Xu nạp"));
        }

        var fee = book.EscrowFee;
        if (fee.Pct < 0 || fee.Pct > 15) errors.Add(new("escrowFee.pct", "% phí phải trong khoảng 0–15%"));
        if (fee.MinVnd > fee.MaxVnd) errors.Add(new("escrowFee", "Phí tối thiểu phải ≤ phí tối đa"));
        if (fee.OrderMinVnd >= fee.OrderMaxVnd) errors.Add(new("escrowFee", "Đơn tối thiểu phải < đơn tối đa"));
        if (fee.MinVnd * 10 > fee.OrderMinVnd)
            warnings.Add(new("escrowFee", "Phí tối thiểu > 10% giá trị đơn tối thiểu"));

        foreach (var plan in book.GardenPlans.Where(p => p.PriceVnd <= 0 || p.Months <= 0))
            errors.Add(new("gardenPlans", $"Gói {plan.Months} tháng: giá và số tháng phải > 0"));

        AddValueInversionWarnings(book, groupCodes, warnings);
        AddGardenPlanWarnings(book, warnings);
        if (active is not null) AddLargeChangeWarnings(book, active, warnings);

        return new(errors, warnings);
    }

    // Chặn tái diễn lỗi L25: dịch vụ cao cấp rẻ hơn dịch vụ thấp hơn.
    static void AddValueInversionWarnings(PriceBook book, HashSet<string> groups, List<ValidationIssue> warnings)
    {
        var bump = book.ListingServices.FirstOrDefault(s => s.Code == ListingServiceCodes.Bump);
        var auto = book.ListingServices.FirstOrDefault(s => s.Code == ListingServiceCodes.AutoBump);
        var priorities = book.ListingServices.Where(s => s.Code == ListingServiceCodes.Priority && s.Days is not null)
            .OrderBy(s => s.Days).ToList();

        foreach (var g in groups)
        {
            if (bump is not null && auto is not null && auto.Days is int days && auto.PerDay is int perDay
                && bump.Prices.TryGetValue(g, out var bp) && auto.Prices.TryGetValue(g, out var ap)
                && ap >= bp * days * perDay)
                warnings.Add(new($"listingServices.AUTO_BUMP.{g}", "Đẩy tự động không rẻ hơn tổng số lượt đẩy lẻ"));

            if (auto?.Days is int autoDays && auto.Prices.TryGetValue(g, out var autoPrice))
            {
                var samePriority = priorities.FirstOrDefault(p => p.Days == autoDays);
                if (samePriority is not null && samePriority.Prices.TryGetValue(g, out var pp) && pp < autoPrice)
                    warnings.Add(new($"listingServices.PRIORITY.{g}",
                        $"Ngược giá trị: Ưu tiên {autoDays} ngày rẻ hơn Đẩy tự động {autoDays} ngày"));
            }

            for (var i = 1; i < priorities.Count; i++)
                if (priorities[i].Prices.GetValueOrDefault(g) < priorities[i - 1].Prices.GetValueOrDefault(g))
                    warnings.Add(new($"listingServices.PRIORITY.{g}",
                        $"Ưu tiên {priorities[i].Days} ngày rẻ hơn {priorities[i - 1].Days} ngày"));
        }

        var packages = book.TopUpPackages.OrderBy(p => p.PriceVnd).ToList();
        for (var i = 1; i < packages.Count; i++)
        {
            var prevRate = (decimal)packages[i - 1].BonusXu / packages[i - 1].Xu;
            var rate = (decimal)packages[i].BonusXu / packages[i].Xu;
            if (rate < prevRate)
                warnings.Add(new($"topUpPackages.{packages[i].Code}", "Gói nạp lớn hơn có tỉ lệ thưởng thấp hơn gói nhỏ"));
        }
    }

    static void AddGardenPlanWarnings(PriceBook book, List<ValidationIssue> warnings)
    {
        var monthly = book.GardenPlans.FirstOrDefault(p => p.Months == 1);
        if (monthly is null) return;
        foreach (var plan in book.GardenPlans.Where(p => p.Months > 1 && p.PriceVnd / p.Months > monthly.PriceVnd))
            warnings.Add(new("gardenPlans", $"Gói {plan.Months} tháng tính theo tháng đắt hơn gói 1 tháng"));
    }

    static void AddLargeChangeWarnings(PriceBook book, PriceBook active, List<ValidationIssue> warnings)
    {
        foreach (var s in book.ListingServices)
        {
            var old = active.ListingServices.FirstOrDefault(a => a.Code == s.Code && a.Days == s.Days && a.LabelName == s.LabelName);
            if (old is null) continue;
            foreach (var (g, price) in s.Prices)
                if (old.Prices.TryGetValue(g, out var oldPrice) && oldPrice > 0
                    && Math.Abs(price - oldPrice) * 2 > oldPrice)
                    warnings.Add(new($"listingServices.{s.Code}.{g}", $"Giá thay đổi > 50% so với bản đang hiệu lực ({oldPrice} → {price})"));
        }
    }

    /// <summary>Bảng giá tăng giá gói Nhà vườn so với bản hiệu lực (BR-PRC-07).</summary>
    public static bool IncreasesGardenPlanPrice(PriceBook book, PriceBook active) =>
        book.GardenPlans.Any(p => active.GardenPlans.FirstOrDefault(a => a.Months == p.Months) is { } a && p.PriceVnd > a.PriceVnd);
}
