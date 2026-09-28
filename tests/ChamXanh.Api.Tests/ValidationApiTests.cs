using System.Net;
using System.Net.Http.Json;
using ChamXanh.Api.Common.Auth;
using Microsoft.AspNetCore.Hosting;

namespace ChamXanh.Api.Tests;

/// <summary>Kiểm tra đầu vào và phân quyền của các API mới — mỗi dòng dữ liệu là một test case.</summary>
[Collection(ApiCollection.Name)]
public class ValidationApiTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    protected override void ConfigureSettings(IWebHostBuilder b) => b.UseSetting("Features:escrow", "true");

    async Task<(HttpClient Client, string PlantId)> MemberWithPlant()
    {
        var (c, _) = await Member();
        var p = await (await c.PostAsJsonAsync("/api/me/garden/plants", new { name = "Sen đá" })).EnsureOk();
        return (c, p.GetProperty("id").GetString()!);
    }

    // ---------- Hồ sơ vườn ----------

    [Theory]
    [InlineData("", "INVALID_NAME")]
    [InlineData("   ", "INVALID_NAME")]
    [InlineData("Tên cây dài quá mức cho phép vì vượt tám mươi ký tự theo quy định của hồ sơ vườn Chạm Xanh", "INVALID_NAME")]
    public async Task Plant_name_is_validated(string name, string code)
    {
        var (c, _) = await Member();
        Assert.Equal(code, await (await c.PostAsJsonAsync("/api/me/garden/plants", new { name })).ErrorCode());
    }

    [Fact]
    public async Task Plant_with_unknown_species_is_rejected()
    {
        var (c, _) = await Member();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/me/garden/plants", new { name = "Cây lạ", speciesId = "khong-co" })).StatusCode);
    }

    [Fact]
    public async Task Plant_with_someone_elses_photo_is_rejected()
    {
        var (other, _) = await Member();
        var form = new MultipartFormDataContent { { new ByteArrayContent(TestImages.Jpeg()), "file", "a.jpg" } };
        var photo = (await (await other.PostAsync("/api/media", form)).EnsureOk()).GetProperty("id").GetString();
        var (c, _) = await Member();
        Assert.Equal("INVALID_MEDIA", await (await c.PostAsJsonAsync("/api/me/garden/plants", new { name = "Sen đá", mediaIds = new[] { photo } })).ErrorCode());
    }

    [Theory]
    [InlineData(0, "INVALID_INTERVAL")]
    [InlineData(366, "INVALID_INTERVAL")]
    [InlineData(-1, "INVALID_INTERVAL")]
    public async Task Reminder_interval_bounds(int interval, string code)
    {
        var (c, plantId) = await MemberWithPlant();
        Assert.Equal(code, await (await c.PostAsJsonAsync($"/api/me/garden/plants/{plantId}/reminders",
            new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(1), repeat = "Daily", interval })).ErrorCode());
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Yearly")]
    public async Task Repeating_reminder_with_past_start_is_accepted_and_moves_forward(string repeat)
    {
        var (c, plantId) = await MemberWithPlant();
        var r = await (await c.PostAsJsonAsync($"/api/me/garden/plants/{plantId}/reminders",
            new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(-400), repeat })).EnsureOk();
        Assert.True(r.GetProperty("nextAt").GetDateTime() > DateTime.UtcNow);
    }

    [Fact]
    public async Task At_most_ten_reminders_per_plant()
    {
        var (c, plantId) = await MemberWithPlant();
        for (var i = 0; i < 10; i++)
            (await c.PostAsJsonAsync($"/api/me/garden/plants/{plantId}/reminders", new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(1 + i), repeat = "None" })).EnsureSuccessStatusCode();
        Assert.Equal("LIMIT_REACHED", await (await c.PostAsJsonAsync($"/api/me/garden/plants/{plantId}/reminders", new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(20), repeat = "None" })).ErrorCode());
    }

    [Fact]
    public async Task Cannot_add_reminder_to_someone_elses_plant()
    {
        var (_, plantId) = await MemberWithPlant();
        var (c, _) = await Member();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/api/me/garden/plants/{plantId}/reminders", new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(1), repeat = "Daily" })).StatusCode);
    }

    [Theory]
    [InlineData("/api/me/garden/plants")]
    [InlineData("/api/me/garden/upcoming")]
    [InlineData("/api/me/notifications")]
    [InlineData("/api/me/notifications/settings")]
    [InlineData("/api/escrow/orders")]
    [InlineData("/api/me/rentals")]
    [InlineData("/api/support/tickets")]
    [InlineData("/api/me/account/deletion-check")]
    public async Task Member_endpoints_require_login(string path) => Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync(path)).StatusCode);

    // ---------- Thông báo ----------

    [Fact]
    public async Task Notification_settings_cannot_disable_transaction_group()
    {
        var (c, _) = await Member();
        var g = await (await c.PutAsJsonAsync("/api/me/notifications/settings", new { groups = new Dictionary<string, bool> { ["care"] = false, ["transaction"] = false } })).EnsureOk();
        Assert.False(g.GetProperty("care").GetBoolean());
        Assert.True(g.GetProperty("transaction").GetBoolean());
    }

    [Theory]
    [InlineData("", "ios")]
    [InlineData("token", "windows")]
    public async Task Push_device_is_validated(string token, string platform)
    {
        var (c, _) = await Member();
        Assert.Equal("INVALID_DEVICE", await (await c.PostAsJsonAsync("/api/me/notifications/devices", new { token, platform })).ErrorCode());
    }

    // ---------- Khám phá (quản trị) ----------

    [Theory]
    [InlineData("Ngắn", "INVALID_TITLE")]
    [InlineData("Tiêu đề hợp lệ nhưng nội dung ngắn", "INVALID_BODY")]
    public async Task Article_is_validated(string title, string code)
    {
        var editor = await Admin($"ed.{Guid.NewGuid():N}"[..12], AdminRoles.Editor);
        var body = title.StartsWith("Tiêu") ? "Quá ngắn" : new string('a', 60);
        Assert.Equal(code, await (await editor.PostAsJsonAsync("/api/admin/explore", new { title, body, status = "Draft" })).ErrorCode());
    }

    [Theory]
    [InlineData(AdminRoles.Moderator)]
    [InlineData(AdminRoles.Accountant)]
    [InlineData(AdminRoles.Verification)]
    public async Task Only_content_roles_manage_articles(string role)
    {
        var admin = await Admin($"r.{Guid.NewGuid():N}"[..12], role);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/explore")).StatusCode);
    }

    [Fact]
    public async Task Members_cannot_use_admin_article_api()
    {
        var (c, _) = await Member();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/explore")).StatusCode);
    }

    [Fact]
    public async Task Explore_unknown_slug_is_404() => Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync("/api/explore/khong-ton-tai")).StatusCode);

    // ---------- Cộng đồng ----------

    [Theory]
    [InlineData("Ngắn", "Nội dung đủ dài để đăng", "INVALID_TITLE")]
    [InlineData("Tiêu đề đủ dài", "ngắn", "INVALID_BODY")]
    public async Task Community_post_is_validated(string title, string body, string code)
    {
        var (c, _) = await Member();
        Assert.Equal(code, await (await c.PostAsJsonAsync("/api/community/posts", new { type = "Question", title, body })).ErrorCode());
    }

    [Fact]
    public async Task Showcase_requires_photo()
    {
        var (c, _) = await Member();
        Assert.Equal("PHOTO_REQUIRED", await (await c.PostAsJsonAsync("/api/community/posts", new { type = "Showcase", title = "Khoe chậu mai", body = "Mai nhà mình nở rồi mọi người ơi" })).ErrorCode());
    }

    [Fact]
    public async Task New_account_cannot_post_links()
    {
        var (c, _) = await Member();
        Assert.Equal("LINK_NOT_ALLOWED", await (await c.PostAsJsonAsync("/api/community/posts", new { type = "Guide", title = "Hướng dẫn trồng sen đá", body = "Xem chi tiết tại https://abc.com nhé các bạn" })).ErrorCode());
    }

    [Fact]
    public async Task Like_twice_counts_once()
    {
        var (author, _) = await Member();
        var post = await (await author.PostAsJsonAsync("/api/community/posts", new { type = "Question", title = "Hỏi cách tưới lan", body = "Lan hồ điệp tưới mấy lần một tuần ạ?" })).EnsureOk();
        var id = post.GetProperty("id").GetString();
        var (fan, _) = await Member();
        await fan.PutAsync($"/api/community/posts/{id}/like", null);
        await fan.PutAsync($"/api/community/posts/{id}/like", null);
        Assert.Equal(1, (await (await Anonymous().GetAsync($"/api/community/posts/{id}")).EnsureOk()).GetProperty("post").GetProperty("likes").GetInt32());
    }

    [Fact]
    public async Task Same_user_reporting_three_times_does_not_hide_post()
    {
        var (author, _) = await Member();
        var id = (await (await author.PostAsJsonAsync("/api/community/posts", new { type = "Question", title = "Hỏi về bonsai", body = "Cây sanh nên cắt tỉa tháng mấy?" })).EnsureOk()).GetProperty("id").GetString();
        var (r, _) = await Member();
        for (var i = 0; i < 3; i++) await r.PostAsJsonAsync($"/api/community/posts/{id}/report", new { reason = "spam" });
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync($"/api/community/posts/{id}")).StatusCode);
    }

    // ---------- Giao dịch đảm bảo ----------

    [Fact]
    public async Task Escrow_order_for_unknown_listing_is_404()
    {
        var (c, _) = await Member();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/escrow/orders", new { listingId = "000000000000000000000000", delivery = "SellerDelivery", deliveryAddress = "Q1" })).StatusCode);
    }

    [Fact]
    public async Task Escrow_order_of_someone_else_is_hidden()
    {
        var (c, _) = await Member();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/escrow/orders/000000000000000000000000")).StatusCode);
    }

    [Theory]
    [InlineData("{\"orderId\":\"000000000000000000000000\",\"status\":\"PAID\",\"amountVnd\":1,\"gatewayRef\":\"x\"}", "bad-signature")]
    [InlineData("{\"orderId\":\"000000000000000000000000\",\"status\":\"PAID\",\"amountVnd\":1,\"gatewayRef\":\"x\"}", null)]
    public async Task Escrow_webhook_requires_valid_signature(string raw, string? signature)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/escrow/webhook") { Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json") };
        if (signature is not null) req.Headers.Add("X-Signature", signature);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().SendAsync(req)).StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/escrow/payouts", AdminRoles.Moderator)]
    [InlineData("/api/admin/escrow/payouts", AdminRoles.Support)]
    [InlineData("/api/admin/features", AdminRoles.Accountant)]
    [InlineData("/api/admin/members", AdminRoles.Verification)]
    [InlineData("/api/admin/support/tickets", AdminRoles.Editor)]
    [InlineData("/api/admin/reports/dashboard", AdminRoles.Verification)]
    [InlineData("/api/admin/community/reported", AdminRoles.Moderator)]
    [InlineData("/api/admin/campaigns", AdminRoles.Accountant)]
    public async Task Admin_endpoints_enforce_permissions(string path, string role)
    {
        var admin = await Admin($"p.{Guid.NewGuid():N}"[..12], role);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/escrow/payouts", AdminRoles.Accountant)]
    [InlineData("/api/admin/members", AdminRoles.Moderator)]
    [InlineData("/api/admin/support/tickets", AdminRoles.Support)]
    [InlineData("/api/admin/reports/dashboard", AdminRoles.Marketing)]
    [InlineData("/api/admin/campaigns", AdminRoles.Editor)]
    [InlineData("/api/admin/features", AdminRoles.SuperAdmin)]
    public async Task Admin_endpoints_allow_right_role(string path, string role)
    {
        var admin = await Admin($"a.{Guid.NewGuid():N}"[..12], role);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
    }

    // ---------- Thuê, báo giá, hỗ trợ ----------

    [Fact]
    public async Task Quote_on_unknown_listing_is_404()
    {
        var (c, _) = await Member();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/listings/000000000000000000000000/quotes", new { unitPrice = 100000, quantity = 1 })).StatusCode);
    }

    [Theory]
    [InlineData("Ngắn", "Nội dung đủ mười ký tự")]
    [InlineData("Tiêu đề hợp lệ", "ngắn")]
    public async Task Support_ticket_is_validated(string subject, string message)
    {
        var (c, _) = await Member();
        Assert.Equal("INVALID_TICKET", await (await c.PostAsJsonAsync("/api/support/tickets", new { topic = "Other", subject, message })).ErrorCode());
    }

    [Fact]
    public async Task Ticket_of_other_user_is_hidden()
    {
        var (a, _) = await Member();
        var t = await (await a.PostAsJsonAsync("/api/support/tickets", new { topic = "Other", subject = "Cần hỗ trợ đăng tin", message = "Mình không tải được ảnh lên" })).EnsureOk();
        var (b, _) = await Member();
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/support/tickets/{t.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task Rental_calendar_for_unknown_listing_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync("/api/listings/000000000000000000000000/rental-calendar")).StatusCode);

    // ---------- Tài khoản ----------

    [Fact]
    public async Task Deletion_with_wrong_otp_is_rejected()
    {
        var (c, _) = await Member();
        await c.PostAsync("/api/me/account/deletion-otp", null);
        Assert.NotEqual(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/me/account/delete", new { otpCode = "000000", acknowledgeLosses = true })).StatusCode);
    }

    [Theory]
    [InlineData("fly")]
    [InlineData("")]
    public async Task Sanction_action_is_validated(string action)
    {
        var (_, memberId) = await Member();
        var mod = await Admin($"m.{Guid.NewGuid():N}"[..12], AdminRoles.Moderator);
        var res = await mod.PostAsJsonAsync($"/api/admin/members/{memberId}/sanction", new { action, reason = "test" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Sanction_requires_reason()
    {
        var (_, memberId) = await Member();
        var mod = await Admin($"n.{Guid.NewGuid():N}"[..12], AdminRoles.Moderator);
        Assert.Equal("REASON_REQUIRED", await (await mod.PostAsJsonAsync($"/api/admin/members/{memberId}/sanction", new { action = "lock", reason = "" })).ErrorCode());
    }

    [Fact]
    public async Task Dev_bootstrap_admin_logs_in_with_short_password() => await SuperAdmin();

    [Theory]
    [InlineData("123")]
    [InlineData("12345678901")]
    public async Task New_admins_still_need_twelve_char_passwords(string password)
    {
        var super = await SuperAdmin();
        Assert.Equal("WEAK_PASSWORD", await (await super.PostAsJsonAsync("/api/admin/users",
            new { username = $"w{Guid.NewGuid():N}"[..10], displayName = "Test", password, roles = new[] { AdminRoles.Editor } })).ErrorCode());
    }

    [Fact]
    public async Task Public_feature_flags_are_readable() =>
        Assert.True((await (await Anonymous().GetAsync("/api/features")).EnsureOk()).GetProperty("escrow").GetBoolean());
}
