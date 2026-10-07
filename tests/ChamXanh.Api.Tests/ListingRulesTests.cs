using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Moderation;

namespace ChamXanh.Api.Tests;

public class ListingRulesTests
{
    static readonly Category SenDa = CatalogSeed.Create().First(c => c.Id == "sen-da");
    static readonly Category Bonsai = CatalogSeed.Create().First(c => c.Id == "bonsai-mini");
    static readonly Category PhanBon = CatalogSeed.Create().First(c => c.Id == "phan-bon");

    [Theory]
    [InlineData("Sen Đá  Kim Tuyến!!", "sen da kim tuyen")]
    [InlineData("ĐÀO TẾT Nhật Tân", "dao tet nhat tan")]
    [InlineData("  ", "")]
    public void Normalizes_vietnamese(string input, string expected) => Assert.Equal(expected, VietnameseText.Normalize(input));

    [Theory]
    [InlineData("Liên hệ 0912 345 678 nhé")]
    [InlineData("gọi +84912345678")]
    [InlineData("xem thêm tại zalo.me/abc")]
    [InlineData("web www.caycanh.vn")]
    public void Detects_contact_info(string text) => Assert.True(ListingRules.ContainsContactInfo(text));

    [Fact]
    public void Plain_numbers_are_not_phone() => Assert.False(ListingRules.ContainsContactInfo("Cây cao 120 cm, chậu 35 cm, giá 1500000"));

    [Fact]
    public void Title_rules()
    {
        Assert.Contains(ListingRules.ValidateText("Ngắn", "Mô tả đủ dài hơn hai mươi ký tự nhé"), e => e.Field == "title");
        Assert.Contains(ListingRules.ValidateText("SEN ĐÁ ĐẸP GIÁ RẺ", "Mô tả đủ dài hơn hai mươi ký tự nhé"), e => e.Message.Contains("viết hoa"));
        Assert.Empty(ListingRules.ValidateText("Sen đá kim tuyến chậu 10cm", "Cây khỏe, lên màu đẹp, giao tại TP.HCM"));
    }

    [Fact]
    public void Negotiable_price_only_for_allowed_categories_and_needs_range()
    {
        var input = new ListingRules.PriceInput(null, PriceMode.Negotiable, false, 10_000_000, 30_000_000, null, null, null, 1);
        Assert.Contains(ListingRules.ValidatePrice(ListingType.Sell, SenDa, input), e => e.Field == "priceMode");
        Assert.Empty(ListingRules.ValidatePrice(ListingType.Sell, Bonsai, input));
        Assert.Contains(ListingRules.ValidatePrice(ListingType.Sell, Bonsai, input with { RefMin = null }), e => e.Field == "priceRef");
    }

    [Fact]
    public void Give_must_be_free_and_sell_must_have_price()
    {
        Assert.NotEmpty(ListingRules.ValidatePrice(ListingType.Give, SenDa, new(50_000, PriceMode.Fixed, false, null, null, null, null, null, 1)));
        Assert.NotEmpty(ListingRules.ValidatePrice(ListingType.Sell, SenDa, new(0, PriceMode.Fixed, false, null, null, null, null, null, 1)));
    }

    [Fact]
    public void Real_photo_badge_expires_after_30_days()
    {
        var now = DateTime.UtcNow;
        var fresh = new List<ListingMediaRef> { new() { CapturedInApp = true, CapturedAt = now.AddDays(-5) } };
        var old = new List<ListingMediaRef> { new() { CapturedInApp = true, CapturedAt = now.AddDays(-31) } };
        var gallery = new List<ListingMediaRef> { new() { CapturedInApp = false } };
        Assert.True(ListingRules.HasRealPhotoBadge(fresh, now, 30));
        Assert.False(ListingRules.HasRealPhotoBadge(old, now, 30));
        Assert.False(ListingRules.HasRealPhotoBadge(gallery, now, 30));
    }

    [Fact]
    public void Expiry_counts_from_launch_date_for_seed_listings()
    {
        var launch = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var o = new ListingOptions { LaunchDate = launch };
        Assert.Equal(launch.AddDays(60), ListingRules.ComputeExpiry(launch.AddDays(-20), o));
        Assert.Equal(launch.AddDays(70), ListingRules.ComputeExpiry(launch.AddDays(10), o));
    }

    [Fact]
    public void Pro_seller_only_without_plan() =>
        Assert.Equal((true, false), (ListingRules.IsProSeller(21, false, 20), ListingRules.IsProSeller(21, true, 20)));

    [Fact]
    public void Attribute_validation()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["tinhTrang"] = JsonSerializer.SerializeToElement("Trồng chậu"),
            ["chieuCao"] = JsonSerializer.SerializeToElement(15),
            ["dotBien"] = JsonSerializer.SerializeToElement(true),
        };
        var (values, errors) = AttributeValidator.Validate(SenDa, input);
        Assert.Empty(errors);
        Assert.Equal(15d, values["chieuCao"]);

        // Ô dạng chọn chỉ là gợi ý: giá trị tự gõ hợp lệ; gõ trùng gợi ý thì lưu đúng chữ của gợi ý.
        input["tinhTrang"] = JsonSerializer.SerializeToElement("Ghép gốc  sẵn");
        Assert.Equal("Ghép gốc sẵn", AttributeValidator.Validate(SenDa, input).Values["tinhTrang"]);
        input["tinhTrang"] = JsonSerializer.SerializeToElement("trồng CHẬU");
        Assert.Equal("Trồng chậu", AttributeValidator.Validate(SenDa, input).Values["tinhTrang"]);

        input["la"] = JsonSerializer.SerializeToElement("x");
        input.Remove("chieuCao");
        var bad = AttributeValidator.Validate(SenDa, input).Errors;
        Assert.Equal(["chieuCao", "la"], bad.Select(e => e.Key).Order()); // thiếu bắt buộc, khóa lạ
    }

    [Fact]
    public void Plant_specific_attributes_are_optional_but_supplies_keep_required()
    {
        var lan = new Category
        {
            Id = "lan", Level = 2, IsLivePlant = true,
            Attributes =
            [
                new() { Key = "tinhTrang", Label = "Tình trạng cây", Type = AttributeType.SingleSelect, Options = ["Trồng chậu"], Required = true },
                new() { Key = "loaiLan", Label = "Loại lan", Type = AttributeType.SingleSelect, Options = ["Hồ điệp"], Required = true },
            ],
        };
        var errors = AttributeValidator.Validate(lan, new Dictionary<string, JsonElement> { ["tinhTrang"] = JsonSerializer.SerializeToElement("Trồng chậu") }).Errors;
        Assert.Empty(errors);

        var phanBon = new Category
        {
            Id = "phan-bon", Level = 2,
            Attributes = [new() { Key = "soGiayPhep", Label = "Số giấy phép", Type = AttributeType.Text, Required = true }],
        };
        Assert.Single(AttributeValidator.Validate(phanBon, null).Errors);
    }

    [Fact]
    public void Manual_review_matches_free_text_without_diacritics()
    {
        var cat = new Category
        {
            Id = "bonsai", Level = 2, IsLivePlant = true,
            Attributes = [new() { Key = "nguonGoc", Label = "Nguồn gốc", Type = AttributeType.SingleSelect, Options = ["Phôi rừng"], ManualReviewValues = ["Phôi rừng"] }],
        };
        Assert.True(AttributeValidator.NeedsManualReview(cat, new Dictionary<string, object> { ["nguonGoc"] = "phoi rung Tây Bắc" }));
        Assert.False(AttributeValidator.NeedsManualReview(cat, new Dictionary<string, object> { ["nguonGoc"] = "Tự tạo" }));
    }

    static RiskContext Ctx(string title = "Sen đá kim tuyến chậu nhỏ", string desc = "Cây khỏe mạnh, lên màu đẹp", Category? cat = null,
        Species? sp = null, long? price = 100_000, bool newAcc = false, bool individual = true, bool trusted = false, Dictionary<string, object>? attrs = null) =>
        new(title, desc, cat ?? SenDa, sp, attrs ?? [], price, null, newAcc, individual, trusted, 0, false, 5_000_000, 50_000_000);

    static RiskResult Eval(RiskContext c) => RiskScorer.Evaluate(c, new ModerationConfig(), 30, 70);

    [Fact]
    public void Clean_listing_auto_approves() => Assert.Equal(RiskRoute.AutoApprove, Eval(Ctx()).Route);

    [Fact]
    public void Banned_species_and_terms_auto_reject()
    {
        var canSa = CatalogSeed.Species().First(s => s.Id == "can-sa");
        Assert.Equal(RiskRoute.AutoReject, Eval(Ctx(sp: canSa)).Route);
        Assert.Equal(RiskRoute.AutoReject, Eval(Ctx(desc: "Bán hạt giống cần sa nhập khẩu")).Route);
    }

    [Fact]
    public void Negated_banned_term_goes_to_manual_review_not_rejection() =>
        Assert.Equal(RiskRoute.Manual, Eval(Ctx(desc: "Cây vườn nhà trồng, không phải lan rừng tự nhiên")).Route);

    [Fact]
    public void Restricted_items_always_manual_even_for_trusted_garden()
    {
        Assert.Equal(RiskRoute.Manual, Eval(Ctx(cat: PhanBon, trusted: true, individual: false)).Route);
        var lanHai = CatalogSeed.Species().First(s => s.Id == "lan-hai");
        Assert.Equal(RiskRoute.Manual, Eval(Ctx(sp: lanHai, trusted: true, individual: false)).Route);
        Assert.Equal(RiskRoute.Manual, Eval(Ctx(cat: Bonsai, attrs: new() { ["nguonGoc"] = "Phôi rừng" }, trusted: true)).Route);
        Assert.Equal(RiskRoute.Manual, Eval(Ctx(price: 60_000_000, trusted: true, individual: false)).Route);
    }

    [Fact]
    public void Risky_signals_accumulate_to_priority_queue()
    {
        var r = Eval(Ctx(desc: "Hạt giống hoa hồng xanh, chuyển khoản trước mới gửi", newAcc: true, price: 6_000_000));
        Assert.Equal(RiskRoute.ManualPriority, r.Route);
        Assert.Contains(r.Signals, s => s.StartsWith("risky_term"));
    }

    [Fact]
    public void Sla_outside_working_hours_starts_next_morning()
    {
        var at23Vn = new DateTime(2026, 9, 24, 16, 0, 0, DateTimeKind.Utc); // 23h giờ VN
        var due = ModerationService.ComputeSla(at23Vn, CaseQueue.Normal);
        Assert.Equal(new DateTime(2026, 9, 25, 2, 0, 0, DateTimeKind.Utc), due); // 9h sáng hôm sau giờ VN
    }
}

public class CatalogSeedTests
{
    [Fact]
    public void Every_posting_category_has_a_price_group()
    {
        var book = ChamXanh.Api.Modules.Pricing.PriceBookSeed.Create(DateTime.UtcNow);
        var missing = CatalogSeed.Create().Where(c => c.Level == 2)
            .Where(c => ChamXanh.Api.Modules.Pricing.PriceCalculator.ResolvePriceGroup(book, c.Id) is null).Select(c => c.Id).ToList();
        Assert.Empty(missing);
    }
}
