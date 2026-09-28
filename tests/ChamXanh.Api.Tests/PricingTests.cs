using ChamXanh.Api.Modules.Pricing;

namespace ChamXanh.Api.Tests;

public class PricingTests
{
    static readonly DateTime Now = new(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc);
    static PriceBook Seed() => PriceBookSeed.Create(Now);
    static QuoteContext Ctx(string category = "bonsai-mini", params string[] collections) => new(category, collections, Now);

    [Fact]
    public void Seed_is_valid_without_warnings()
    {
        var result = PriceBookValidator.Validate(Seed());
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Quote_uses_price_group_of_category()
    {
        var q = PriceCalculator.Quote(Seed(), ListingServiceCodes.Priority, 7, Ctx("bonsai-mini"))!;
        Assert.Equal("PREMIUM", q.PriceGroup);
        Assert.Equal(350, q.FinalPrice);
    }

    [Fact]
    public void Quote_returns_null_for_unknown_category_or_disabled_service()
    {
        var book = Seed();
        Assert.Null(PriceCalculator.Quote(book, ListingServiceCodes.Bump, null, Ctx("khong-ton-tai")));
        book.ListingServices.First(s => s.Code == ListingServiceCodes.Bump).Enabled = false;
        Assert.Null(PriceCalculator.Quote(book, ListingServiceCodes.Bump, null, Ctx()));
    }

    [Fact]
    public void Overlapping_multipliers_take_max_not_product_and_round_up()
    {
        var book = Seed();
        book.Multipliers.Add(new() { Name = "Tết", CollectionIds = ["cay-tet"], Factor = 1.5m, From = Now.AddDays(-1), To = Now.AddDays(10) });
        book.Multipliers.Add(new() { Name = "Premium", PriceGroups = ["PREMIUM"], Factor = 1.2m, From = Now.AddDays(-1), To = Now.AddDays(10) });

        var q = PriceCalculator.Quote(book, ListingServiceCodes.Bump, null, Ctx("bonsai-mini", "cay-tet"))!;
        Assert.Equal(1.5m, q.Factor);
        Assert.Equal(38, q.FinalPrice); // 25 × 1,5 = 37,5 → làm tròn lên 38
    }

    [Fact]
    public void Expired_multiplier_is_ignored()
    {
        var book = Seed();
        book.Multipliers.Add(new() { Name = "Cũ", PriceGroups = ["PREMIUM"], Factor = 2m, From = Now.AddDays(-10), To = Now.AddDays(-1) });
        Assert.Equal(1m, PriceCalculator.Quote(book, ListingServiceCodes.Bump, null, Ctx())!.Factor);
    }

    [Theory]
    [InlineData(50_000, 5_000)]         // 4% = 2.000 → nâng lên tối thiểu
    [InlineData(1_000_000, 40_000)]
    [InlineData(200_000_000, 2_000_000)] // 4% = 8 triệu → chặn ở tối đa
    public void Escrow_fee_is_clamped(long item, long expected) =>
        Assert.Equal(expected, PriceCalculator.EscrowFee(new EscrowFee(), item));

    [Fact]
    public void Warns_when_priority_is_cheaper_than_auto_bump_same_days()
    {
        var book = Seed();
        book.ListingServices.First(s => s.Code == ListingServiceCodes.Priority && s.Days == 7).Prices["STANDARD"] = 60;
        var result = PriceBookValidator.Validate(book);
        Assert.Contains(result.Warnings, w => w.Message.Contains("Ngược giá trị"));
    }

    [Fact]
    public void Errors_on_category_in_two_groups_and_bad_escrow_fee()
    {
        var book = Seed();
        book.PriceGroups[1].CategoryIds.Add("bonsai-mini");
        book.EscrowFee.Pct = 20;
        book.EscrowFee.MinVnd = 3_000_000;
        var errors = PriceBookValidator.Validate(book).Errors.Select(e => e.Message).ToList();
        Assert.Contains(errors, m => m.Contains("nhiều hơn 1 nhóm giá"));
        Assert.Contains(errors, m => m.Contains("0–15%"));
        Assert.Contains(errors, m => m.Contains("tối thiểu phải ≤"));
    }

    [Fact]
    public void Errors_on_missing_group_price_and_bonus_over_half()
    {
        var book = Seed();
        book.ListingServices[0].Prices.Remove("SEEDLING");
        book.TopUpPackages[0].BonusXu = 30;
        var errors = PriceBookValidator.Validate(book).Errors;
        Assert.Contains(errors, e => e.Message.Contains("Thiếu giá cho nhóm SEEDLING"));
        Assert.Contains(errors, e => e.Message.Contains("50%"));
    }

    [Fact]
    public void Warns_on_large_change_and_detects_garden_price_increase()
    {
        var active = Seed();
        var draft = Seed();
        draft.ListingServices[0].Prices["STANDARD"] = 30;
        draft.GardenPlans[0].PriceVnd = 249_000;
        Assert.Contains(PriceBookValidator.Validate(draft, active).Warnings, w => w.Message.Contains("> 50%"));
        Assert.True(PriceBookValidator.IncreasesGardenPlanPrice(draft, active));
    }
}
