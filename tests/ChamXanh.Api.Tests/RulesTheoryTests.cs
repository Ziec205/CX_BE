using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Community;
using ChamXanh.Api.Modules.Deals;
using ChamXanh.Api.Modules.Escrow;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Messaging;
using ChamXanh.Api.Modules.Notifications;
using ChamXanh.Api.Modules.PlantCare;
using ChamXanh.Api.Modules.Platform;
using ChamXanh.Api.Modules.Pricing;

namespace ChamXanh.Api.Tests;

/// <summary>Quy tắc nghiệp vụ thuần (không cần DB) — mỗi dòng InlineData là một test case.</summary>
public class CommunityRulesTests
{
    [Theory]
    [InlineData("Cây sen đá 150k một chậu", true)]
    [InlineData("Ai cần inbox giá nhé", true)]
    [InlineData("ib giá cho mình", true)]
    [InlineData("Cần bán gấp chậu mai", true)]
    [InlineData("Thanh lý bonsai", true)]
    [InlineData("giá 300 nghìn", true)]
    [InlineData("liên hệ 0912345678", true)]
    [InlineData("Trầu bà nhà mình bị vàng lá", false)]
    [InlineData("Khoe cây mai nở đúng Tết", false)]
    [InlineData("Nên tưới sen đá mấy lần một tuần?", false)]
    public void Detects_selling_in_community_posts(string text, bool sale) => Assert.Equal(sale, CommunityRules.LooksLikeSale(text));

    [Theory]
    [InlineData("xem https://abc.com", true)]
    [InlineData("vào www.shopcay.vn", true)]
    [InlineData("shopcay.vn giá tốt", true)]
    [InlineData("không có link nào", false)]
    [InlineData("cây cao 1.5 m", false)]
    public void Detects_links(string text, bool link) => Assert.Equal(link, CommunityRules.HasLink(text));

    [Theory]
    [InlineData(0, "Mầm")]
    [InlineData(49, "Mầm")]
    [InlineData(50, "Chồi")]
    [InlineData(299, "Chồi")]
    [InlineData(300, "Cây")]
    [InlineData(999, "Cây")]
    [InlineData(1000, "Cổ thụ")]
    public void Community_badges_by_points(int points, string badge) => Assert.Equal(badge, CommunityRules.Badge(points));
}

public class ReminderRecurrenceTests
{
    static Recurrence R(DateTime anchor, ReminderRepeat repeat, int interval = 1) => new(anchor, repeat, interval);
    static readonly DateTime Anchor = new(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc); // 07:30 giờ VN

    [Theory]
    [InlineData(ReminderRepeat.Daily, 1, 1, "2026-01-02")]
    [InlineData(ReminderRepeat.Daily, 1, 31, "2026-02-01")]
    [InlineData(ReminderRepeat.Daily, 2, 1, "2026-01-03")]
    [InlineData(ReminderRepeat.Weekly, 1, 1, "2026-01-08")]
    [InlineData(ReminderRepeat.Weekly, 2, 2, "2026-01-29")]
    [InlineData(ReminderRepeat.Monthly, 1, 1, "2026-02-01")]
    [InlineData(ReminderRepeat.Monthly, 3, 1, "2026-04-01")]
    [InlineData(ReminderRepeat.Monthly, 1, 12, "2027-01-01")]
    [InlineData(ReminderRepeat.Yearly, 1, 1, "2027-01-01")]
    [InlineData(ReminderRepeat.None, 1, 5, "2026-01-01")]
    public void Occurrence_steps_by_calendar(ReminderRepeat repeat, int interval, int n, string expectedDate)
    {
        var at = ReminderSchedule.Occurrence(R(Anchor, repeat, interval), n);
        Assert.Equal(DateTime.Parse(expectedDate).AddMinutes(30), at);
    }

    [Theory]
    [InlineData("2026-01-10T12:00:00Z", "2026-01-11T00:30:00Z")]
    [InlineData("2026-01-01T00:30:00Z", "2026-01-02T00:30:00Z")] // đúng giờ nhắc → lần sau
    [InlineData("2025-12-31T00:00:00Z", "2026-01-01T00:30:00Z")] // trước mốc → chính mốc
    [InlineData("2027-06-15T01:00:00Z", "2027-06-16T00:30:00Z")] // bỏ lỡ lâu ngày → nhảy tới lần kế tiếp
    public void Daily_next_after(string after, string expected) =>
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), ReminderSchedule.NextAfter(R(Anchor, ReminderRepeat.Daily), DateTime.Parse(after).ToUniversalTime()));

    [Fact]
    public void One_off_in_future_returns_anchor() => Assert.Equal(Anchor, ReminderSchedule.NextAfter(R(Anchor, ReminderRepeat.None), Anchor.AddDays(-1)));

    [Fact]
    public void Monthly_from_31st_clamps_to_month_end_without_drift()
    {
        var r = R(new DateTime(2026, 1, 30, 17, 0, 0, DateTimeKind.Utc), ReminderRepeat.Monthly); // 31/01 00:00 giờ VN
        Assert.Equal(new DateTime(2026, 4, 29, 17, 0, 0), ReminderSchedule.Occurrence(r, 3)); // 30/04 giờ VN
        Assert.Equal(new DateTime(2026, 5, 30, 17, 0, 0), ReminderSchedule.Occurrence(r, 4)); // 31/05 giờ VN, không trôi về 30
    }

    [Theory]
    [InlineData(ReminderKind.Watering, "Tưới cây")]
    [InlineData(ReminderKind.Fertilizing, "Bón phân")]
    [InlineData(ReminderKind.Pruning, "Cắt tỉa")]
    [InlineData(ReminderKind.Repotting, "Thay chậu")]
    [InlineData(ReminderKind.Other, "Chăm cây")]
    public void Reminder_labels(ReminderKind kind, string label) => Assert.Equal(label, ReminderSchedule.Label(kind));
}

public class EscrowMathTests
{

    [Theory]
    [InlineData(1_000_000, 0, 40_000, 0, 960_000)]
    [InlineData(1_000_000, 50_000, 40_000, 0, 1_010_000)]
    [InlineData(1_000_000, 50_000, 40_000, 1_050_000, 0)]
    [InlineData(1_000_000, 0, 40_000, 500_000, 480_000)]
    [InlineData(100_000, 0, 5_000, 99_999, 0)] // giữ 1đ, phí làm tròn lên → không âm
    [InlineData(200_000, 30_000, 8_000, 30_000, 192_000)]
    public void Seller_payout(long items, long ship, long fee, long refund, long expected) =>
        Assert.Equal(expected, EscrowService.SellerPayout(items, ship, fee, "SELLER", refund));

    [Fact]
    public void Buyer_paid_fee_is_not_deducted_from_seller() => Assert.Equal(1_000_000, EscrowService.SellerPayout(1_000_000, 0, 40_000, "BUYER", 0));

    [Theory]
    [InlineData(50_000, 5_000)]        // 4% = 2.000 → tối thiểu 5.000
    [InlineData(1_000_000, 40_000)]
    [InlineData(10_000_000, 400_000)]
    [InlineData(100_000_000, 2_000_000)] // chạm trần 2 triệu
    [InlineData(123_456, 5_000)]
    [InlineData(1_234_567, 49_383)]    // làm tròn lên
    public void Escrow_fee_4pct_with_min_max(long items, long fee) => Assert.Equal(fee, PriceCalculator.EscrowFee(new EscrowFee(), items));
}

public class DealsRulesTests
{
    [Theory]
    [InlineData(RentUnit.Day, 1, 1)]
    [InlineData(RentUnit.Day, 10, 10)]
    [InlineData(RentUnit.Week, 7, 1)]
    [InlineData(RentUnit.Week, 8, 2)]
    [InlineData(RentUnit.Month, 30, 1)]
    [InlineData(RentUnit.Month, 31, 2)]
    [InlineData(RentUnit.TetSeason, 45, 1)]
    public void Rent_units(RentUnit unit, int days, int units) => Assert.Equal(units, DealsService.Units(unit, days));
}

public class NotificationGroupTests
{
    [Theory]
    [InlineData("escrow", NotificationGroups.Transaction)]
    [InlineData("escrow.created", NotificationGroups.Transaction)]
    [InlineData("wallet.topup", NotificationGroups.Transaction)]
    [InlineData("sanction", NotificationGroups.Security)]
    [InlineData("account.support", NotificationGroups.Security)]
    [InlineData("chat.message", NotificationGroups.Chat)]
    [InlineData("listing.quote", NotificationGroups.Listing)]
    [InlineData("community.best", NotificationGroups.Community)]
    [InlineData("care.watering", NotificationGroups.Care)]
    [InlineData("search.new", NotificationGroups.Discovery)]
    [InlineData("promo.tet", NotificationGroups.Marketing)]
    public void Maps_type_to_group(string type, string group) => Assert.Equal(group, NotificationGroups.Of(type));

    [Fact]
    public void Transaction_and_security_are_not_optional() =>
        Assert.Empty(NotificationGroups.Mandatory.Intersect(NotificationGroups.Optional));
}

public class TextAndFlagTests
{
    [Theory]
    [InlineData("Cây Ngọc Ngân Thái", "cay-ngoc-ngan-thai")]
    [InlineData("Lưỡi hổ lá xoắn", "luoi-ho-la-xoan")]
    [InlineData("Đa búp đỏ", "da-bup-do")]
    [InlineData("  Mai   vàng  ", "mai-vang")]
    public void Slugify_vietnamese(string input, string slug) => Assert.Equal(slug, VietnameseText.Slugify(input));

    [Theory]
    [InlineData(Flags.Escrow, false)]
    [InlineData(Flags.Community, true)]
    [InlineData(Flags.AiIdentify, true)]
    [InlineData(Flags.ImageSearch, true)]
    public void Feature_flag_defaults(string key, bool enabled) => Assert.Equal(enabled, Flags.Defaults[key]);

    [Theory]
    [InlineData("Chuyển khoản trước cho mình nhé", false, false, false, true)]
    [InlineData("Gửi mình mã OTP", false, false, false, true)]
    [InlineData("Mai mình qua xem cây", false, false, false, false)]
    [InlineData("Kết bạn zalo nhé", false, true, true, true)]
    [InlineData("Kết bạn zalo nhé", false, false, false, false)]
    public void Scam_detector_cases(string text, bool escrow, bool newAccount, bool firstMessage, bool warned) =>
        Assert.Equal(warned, ScamDetector.Check(text, escrow, newAccount, firstMessage) is not null);
}
