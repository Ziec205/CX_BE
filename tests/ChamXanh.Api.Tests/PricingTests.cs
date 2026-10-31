using ChamXanh.Api.Modules.Pricing;

namespace ChamXanh.Api.Tests;

public class PricingTests
{
    static readonly DateTime Now = new(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc);
    static PriceBook Seed() => PriceBookSeed.Create(Now);

    [Fact]
    public void Seed_is_valid_without_warnings()
    {
        var result = PriceBookValidator.Validate(Seed());
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Errors_on_bad_package_bonus_over_half_and_duplicate_code()
    {
        var book = Seed();
        book.TopUpPackages[0].BonusXu = 30;
        book.TopUpPackages[1].PriceVnd = 0;
        book.TopUpPackages[2].Code = book.TopUpPackages[3].Code;
        var errors = PriceBookValidator.Validate(book).Errors.Select(e => e.Message).ToList();
        Assert.Contains(errors, m => m.Contains("50%"));
        Assert.Contains(errors, m => m.Contains("phải > 0"));
        Assert.Contains(errors, m => m.Contains("Trùng mã gói"));
    }

    [Fact]
    public void Error_when_no_package_enabled()
    {
        var book = Seed();
        foreach (var p in book.TopUpPackages) p.Enabled = false;
        Assert.Contains(PriceBookValidator.Validate(book).Errors, e => e.Message.Contains("ít nhất 1 gói"));
    }

    [Fact]
    public void Warns_when_bigger_package_has_lower_bonus_rate_or_price_changes_over_half()
    {
        var active = Seed();
        var draft = Seed();
        draft.TopUpPackages.Single(p => p.Code == "RUNG").BonusXu = 10;
        draft.TopUpPackages.Single(p => p.Code == "MAM").PriceVnd = 100_000;
        var warnings = PriceBookValidator.Validate(draft, active).Warnings.Select(w => w.Message).ToList();
        Assert.Contains(warnings, m => m.Contains("tỉ lệ thưởng thấp hơn"));
        Assert.Contains(warnings, m => m.Contains("> 50%"));
    }
}
