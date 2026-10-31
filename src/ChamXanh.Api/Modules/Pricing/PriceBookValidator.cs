namespace ChamXanh.Api.Modules.Pricing;

public record ValidationIssue(string Field, string Message);

public record ValidationResult(List<ValidationIssue> Errors, List<ValidationIssue> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

// Lỗi chặn lưu; Cảnh báo cho lưu nhưng người duyệt phải xác nhận.
public static class PriceBookValidator
{
    public static ValidationResult Validate(PriceBook book, PriceBook? active = null)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        if (!book.TopUpPackages.Any(p => p.Enabled)) errors.Add(new("topUpPackages", "Cần ít nhất 1 gói nạp đang bật"));
        foreach (var dup in book.TopUpPackages.GroupBy(p => p.Code).Where(g => g.Count() > 1))
            errors.Add(new("topUpPackages", $"Trùng mã gói '{dup.Key}'"));
        foreach (var p in book.TopUpPackages)
        {
            if (p.PriceVnd <= 0 || p.Xu <= 0) errors.Add(new($"topUpPackages.{p.Code}", "Giá và Xu nạp phải > 0"));
            if (p.BonusXu * 2 > p.Xu) errors.Add(new($"topUpPackages.{p.Code}", "Xu thưởng không vượt quá 50% Xu nạp"));
        }

        var packages = book.TopUpPackages.OrderBy(p => p.PriceVnd).ToList();
        for (var i = 1; i < packages.Count; i++)
        {
            var prevRate = (decimal)packages[i - 1].BonusXu / packages[i - 1].Xu;
            var rate = (decimal)packages[i].BonusXu / packages[i].Xu;
            if (rate < prevRate)
                warnings.Add(new($"topUpPackages.{packages[i].Code}", "Gói nạp lớn hơn có tỉ lệ thưởng thấp hơn gói nhỏ"));
        }

        if (active is not null)
            foreach (var p in book.TopUpPackages)
                if (active.TopUpPackages.FirstOrDefault(a => a.Code == p.Code) is { PriceVnd: > 0 } old && Math.Abs(p.PriceVnd - old.PriceVnd) * 2 > old.PriceVnd)
                    warnings.Add(new($"topUpPackages.{p.Code}", $"Giá thay đổi > 50% so với bản đang hiệu lực ({old.PriceVnd} → {p.PriceVnd})"));

        return new(errors, warnings);
    }
}
