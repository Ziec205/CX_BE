using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChamXanh.Api.Modules.Plans;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace ChamXanh.Api.Tests;

public class PlanRuleTests
{
    static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_purchase_starts_now()
    {
        var (plan, start, end) = PlanCatalog.Apply(null, PlanCode.Plus, 1, Now);
        Assert.Equal((PlanCode.Plus, Now, Now.AddMonths(1)), (plan, start, end));
        Assert.Equal(Now.AddMonths(12), PlanCatalog.Apply(Sub(PlanCode.Pro, Now.AddDays(-1)), PlanCode.Plus, 12, Now).EndAt); // gói cũ đã hết hạn
    }

    [Fact]
    public void Same_plan_extends_from_current_end()
    {
        var current = Sub(PlanCode.Plus, Now.AddDays(10));
        Assert.Equal(Now.AddDays(10).AddMonths(1), PlanCatalog.Apply(current, PlanCode.Plus, 1, Now).EndAt);
    }

    [Fact]
    public void Upgrade_converts_remaining_days_by_price_ratio()
    {
        // Còn 69 ngày Plus (39k) → 39 ngày Pro (69k), cộng thêm kỳ Pro vừa mua.
        var (plan, start, end) = PlanCatalog.Apply(Sub(PlanCode.Plus, Now.AddDays(69)), PlanCode.Pro, 1, Now);
        Assert.Equal(PlanCode.Pro, plan);
        Assert.Equal(Now, start);
        Assert.Equal(Now.AddMonths(1).AddDays(39), end);
    }

    [Fact]
    public void Late_lower_plan_payment_is_added_to_higher_plan_without_loss()
    {
        var current = Sub(PlanCode.Pro, Now.AddDays(5));
        var (plan, _, end) = PlanCatalog.Apply(current, PlanCode.Plus, 1, Now);
        Assert.Equal(PlanCode.Pro, plan);
        var term = (Now.AddMonths(1) - Now).TotalMinutes;
        Assert.Equal(Now.AddDays(5).AddMinutes(Math.Floor(term * 39_000 / 69_000)), end);
    }

    [Fact]
    public void Yearly_price_equals_ten_months()
    {
        Assert.Equal(390_000, PlanCatalog.Price(PlanCatalog.Plus, 12));
        Assert.Equal(690_000, PlanCatalog.Price(PlanCatalog.Pro, 12));
        Assert.Equal((3, 20, 100), (PlanCatalog.Free.GardenPlants, PlanCatalog.Plus.GardenPlants, PlanCatalog.Pro.GardenPlants));
        Assert.Equal((3, 15, 40), (PlanCatalog.Free.AiPerDay, PlanCatalog.Plus.AiPerDay, PlanCatalog.Pro.AiPerDay));
    }

    [Fact]
    public void PayOs_webhook_signature_sorts_keys_and_blanks_nulls()
    {
        var data = JsonDocument.Parse("""{"orderCode":123,"amount":3000,"description":"VQRIO123","reference":"TF1","counterAccountName":null,"code":"00"}""").RootElement;
        var expected = PayOsSignature.Hmac("key", "amount=3000&code=00&counterAccountName=&description=VQRIO123&orderCode=123&reference=TF1");
        Assert.Equal(expected, PayOsSignature.ForData("key", data));
        Assert.True(PayOsSignature.Verify("key", data, expected.ToUpperInvariant()));
        Assert.False(PayOsSignature.Verify("other-key", data, expected));
    }

    static Subscription Sub(PlanCode plan, DateTime end) => new() { UserId = "u", Plan = plan, StartAt = end.AddMonths(-1), EndAt = end };
}

[Collection(ApiCollection.Name)]
public class PlanApiTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    static async Task AddPlants(HttpClient c, int n, string prefix = "Cây")
    {
        for (var i = 0; i < n; i++) (await c.PostAsJsonAsync("/api/me/garden/plants", new { name = $"{prefix} {i + 1}" })).EnsureSuccessStatusCode();
    }

    static async Task<JsonElement> Buy(HttpClient c, string plan, int months = 1)
    {
        var pay = await (await c.PostAsJsonAsync("/api/plans/checkout", new { plan, months })).EnsureOk();
        Assert.Contains("/goi/gia-lap?ma=", pay.GetProperty("checkoutUrl").GetString());
        return await (await c.PostAsync($"/api/plans/payments/{pay.GetProperty("id").GetString()}/simulate", null)).EnsureOk();
    }

    [Fact]
    public async Task Free_plan_limits_garden_to_three_plants_until_upgrade()
    {
        var (c, _) = await Member();
        await AddPlants(c, 3);
        var fourth = await c.PostAsJsonAsync("/api/me/garden/plants", new { name = "Cây 4" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fourth.StatusCode);
        Assert.Equal("PLAN_LIMIT", await fourth.ErrorCode());

        var paid = await Buy(c, "Plus");
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.Equal(39_000, paid.GetProperty("amountVnd").GetInt64());

        var me = await (await c.GetAsync("/api/me/plan")).EnsureOk();
        Assert.Equal("Plus", me.GetProperty("plan").GetProperty("code").GetString());
        Assert.Equal(20, me.GetProperty("plan").GetProperty("gardenPlants").GetInt32());
        await AddPlants(c, 1, "Cây thêm");
        Assert.Equal(4, (await (await c.GetAsync("/api/me/plan")).EnsureOk()).GetProperty("usage").GetProperty("gardenPlants").GetInt32());

        // Kích hoạt lặp lại (webhook gửi lại) không cộng thêm thời hạn.
        var again = await c.PostAsync($"/api/plans/payments/{paid.GetProperty("id").GetString()}/simulate", null);
        Assert.Equal(paid.GetProperty("appliedEndAt").GetDateTime(), (await again.EnsureOk()).GetProperty("appliedEndAt").GetDateTime());
    }

    [Fact]
    public async Task Cannot_buy_plus_while_pro_is_active_and_upgrade_converts_days()
    {
        var (c, userId) = await Member();
        var plus = await Buy(c, "Plus");
        var pro = await Buy(c, "Pro");
        Assert.Equal("Pro", pro.GetProperty("appliedPlan").GetString());
        // Plus 1 tháng (~30 ngày) quy đổi thành ~17 ngày Pro, cộng 1 tháng Pro.
        var extra = pro.GetProperty("appliedEndAt").GetDateTime() - plus.GetProperty("appliedEndAt").GetDateTime();
        Assert.InRange(extra.TotalDays, 15, 19);

        var down = await c.PostAsJsonAsync("/api/plans/checkout", new { plan = "Plus", months = 1 });
        Assert.Equal(HttpStatusCode.Conflict, down.StatusCode);
        Assert.Equal("PLAN_DOWNGRADE", await down.ErrorCode());
        var bad = await c.PostAsJsonAsync("/api/plans/checkout", new { plan = "Pro", months = 3 });
        Assert.Equal("INVALID_TERM", await bad.ErrorCode());
    }

    [Fact]
    public async Task Expired_plan_locks_older_plants_and_keeps_three_newest()
    {
        var (c, userId) = await Member();
        await Buy(c, "Plus");
        await AddPlants(c, 5);
        var plans = Factory.Services.GetRequiredService<IMongoDatabase>().GetCollection<Subscription>("subscriptions");
        await plans.UpdateOneAsync(s => s.UserId == userId, Builders<Subscription>.Update.Set(s => s.EndAt, DateTime.UtcNow.AddMinutes(-1)));

        var list = (await (await c.GetAsync("/api/me/garden/plants")).EnsureOk()).EnumerateArray().ToList();
        Assert.Equal(["Cây 5", "Cây 4", "Cây 3"], list.Where(p => !p.GetProperty("locked").GetBoolean()).Select(p => p.GetProperty("name").GetString()));
        var oldest = list.Single(p => p.GetProperty("name").GetString() == "Cây 1").GetProperty("id").GetString();
        var edit = await c.PutAsJsonAsync($"/api/me/garden/plants/{oldest}", new { name = "Đổi tên" });
        Assert.Equal("PLANT_LOCKED", await edit.ErrorCode());
        var remind = await c.PostAsJsonAsync($"/api/me/garden/plants/{oldest}/reminders", new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(1), repeat = "Daily" });
        Assert.Equal("PLANT_LOCKED", await remind.ErrorCode());
        // Xóa bớt cây mới thì cây cũ hơn được mở lại.
        (await c.DeleteAsync($"/api/me/garden/plants/{list[0].GetProperty("id").GetString()}")).EnsureSuccessStatusCode();
        var after = (await (await c.GetAsync("/api/me/garden/plants")).EnsureOk()).EnumerateArray();
        Assert.False(after.Single(p => p.GetProperty("name").GetString() == "Cây 2").GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Ai_quota_is_shared_per_day_and_pro_features_are_gated()
    {
        var (c, _) = await Member();
        for (var i = 0; i < 3; i++)
        {
            var r = await (await c.PostAsJsonAsync("/api/ai/chat", new { message = $"Cây kim tiền vàng lá phải làm sao? ({i})" })).EnsureOk();
            Assert.True(r.GetProperty("counted").GetBoolean());
            Assert.Equal(3 - i - 1, r.GetProperty("quota").GetProperty("remaining").GetInt32());
        }
        var over = await c.PostAsJsonAsync("/api/ai/chat", new { message = "Thêm một câu nữa" });
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal("AI_QUOTA_EXCEEDED", await over.ErrorCode());

        var compare = await c.PostAsJsonAsync("/api/ai/compare", new { listingIds = new[] { "a", "b" } });
        Assert.Equal(HttpStatusCode.Forbidden, compare.StatusCode);
        Assert.Equal("PLAN_REQUIRED", await compare.ErrorCode());

        var convs = (await (await c.GetAsync("/api/ai/conversations")).EnsureOk()).EnumerateArray().ToList();
        Assert.Equal(3, convs.Count);
        var detail = await (await c.GetAsync($"/api/ai/conversations/{convs[0].GetProperty("id").GetString()}")).EnsureOk();
        Assert.Equal(2, detail.GetProperty("messages").GetArrayLength());
        (await c.DeleteAsync($"/api/ai/conversations/{convs[0].GetProperty("id").GetString()}")).EnsureSuccessStatusCode();

        // Lên Plus: hạn mức thành 15/ngày, lượt đã dùng hôm nay vẫn tính.
        await Buy(c, "Plus");
        var quota = await (await c.GetAsync("/api/ai/quota")).EnsureOk();
        Assert.Equal((3, 15), (quota.GetProperty("quota").GetProperty("used").GetInt32(), quota.GetProperty("quota").GetProperty("limit").GetInt32()));
    }

    [Fact]
    public async Task Reminders_need_an_email_and_email_is_validated()
    {
        var (c, _) = await Member();
        var plant = await (await c.PostAsJsonAsync("/api/me/garden/plants", new { name = "Trầu bà" })).EnsureOk();
        var id = plant.GetProperty("id").GetString();
        Assert.Equal("INVALID_EMAIL", await (await c.PutAsJsonAsync("/api/me", new { email = "khong-phai-email" })).ErrorCode());
        var me = await (await c.PutAsJsonAsync("/api/me", new { email = "" })).EnsureOk();
        Assert.Equal(JsonValueKind.Null, me.GetProperty("email").ValueKind);

        var r = await c.PostAsJsonAsync($"/api/me/garden/plants/{id}/reminders", new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(1), repeat = "Daily" });
        Assert.Equal("EMAIL_REQUIRED", await r.ErrorCode());

        me = await (await c.PutAsJsonAsync("/api/me", new { email = " Vuon.Xanh@Gmail.com " })).EnsureOk();
        Assert.Equal("vuon.xanh@gmail.com", me.GetProperty("email").GetString());
        (await c.PostAsJsonAsync($"/api/me/garden/plants/{id}/reminders", new { kind = "Watering", at = DateTimeOffset.UtcNow.AddDays(1), repeat = "Daily" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Payos_returns_to_the_users_own_site_and_result_page_works_without_login()
    {
        var (c, _) = await Member();
        var pay = await (await c.PostAsJsonAsync("/api/plans/checkout", new { plan = "Plus", months = 1, returnOrigin = "https://evil.example" })).EnsureOk();
        Assert.StartsWith("http://localhost:3000/goi/gia-lap", pay.GetProperty("checkoutUrl").GetString()); // tên miền lạ: dùng địa chỉ mặc định
        var id = pay.GetProperty("id").GetString();
        var ma = pay.GetProperty("orderCode").GetInt64();

        var anon = Anonymous();
        var status = await (await anon.GetAsync($"/api/plans/payments/{id}/status?ma={ma}")).EnsureOk();
        Assert.Equal("Pending", status.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/plans/payments/{id}/status?ma={ma + 1}")).StatusCode);
        var cancelled = await (await anon.PostAsync($"/api/plans/payments/{id}/status/cancel?ma={ma}", null)).EnsureOk();
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
    }

    [Fact]
    public void Return_origin_must_be_allowed()
    {
        var o = new PayOsOptions { ReturnBaseUrl = "https://chamxanh.vn", AllowedReturnOrigins = ["https://cham-xanh.vercel.app"] };
        Assert.Equal("https://cham-xanh.vercel.app", o.ResolveWebBase("https://cham-xanh.vercel.app/goi"));
        Assert.Equal("https://chamxanh.vn", o.ResolveWebBase("https://evil.example"));
        Assert.Equal("https://chamxanh.vn", o.ResolveWebBase(null));
    }

    [Fact]
    public async Task Guests_cannot_use_ai()
    {
        var res = await Anonymous().PostAsJsonAsync("/api/ai/chat", new { message = "Xin chào" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

/// <summary>Đã cấu hình khóa PayOS: webhook phải có chữ ký đúng mới kích hoạt gói.</summary>
[Collection(ApiCollection.Name)]
public class PayOsWebhookTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    const string Checksum = "test-checksum-key";

    protected override void ConfigureSettings(IWebHostBuilder b)
    {
        b.UseSetting("PayOS:ClientId", "client");
        b.UseSetting("PayOS:ApiKey", "api");
        b.UseSetting("PayOS:ChecksumKey", Checksum);
    }

    static StringContent Webhook(long orderCode, long amount, string? signature = null)
    {
        var data = new JsonObject
        {
            ["orderCode"] = orderCode, ["amount"] = amount, ["description"] = "CXPRO1", ["accountNumber"] = "123",
            ["reference"] = $"FT{orderCode}", ["transactionDateTime"] = "2026-10-07 10:00:00", ["currency"] = "VND",
            ["paymentLinkId"] = "abc", ["code"] = "00", ["desc"] = "success", ["counterAccountBankId"] = null,
        };
        signature ??= PayOsSignature.ForData(Checksum, JsonDocument.Parse(data.ToJsonString()).RootElement);
        var body = new JsonObject { ["code"] = "00", ["desc"] = "success", ["success"] = true, ["data"] = data, ["signature"] = signature };
        return new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Signed_webhook_activates_plan_once()
    {
        var (c, userId) = await Member();
        var payments = Factory.Services.GetRequiredService<IMongoDatabase>().GetCollection<PlanPayment>("planPayments");
        var payment = new PlanPayment
        {
            UserId = userId, Plan = PlanCode.Pro, Months = 1, AmountVnd = 69_000, OrderCode = 777_001, Gateway = "PAYOS",
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(15),
        };
        await payments.InsertOneAsync(payment);
        var anon = Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/payments/payos/webhook", Webhook(777_001, 69_000, "deadbeef"))).StatusCode);
        // Số tiền không khớp: không kích hoạt.
        (await anon.PostAsync("/api/payments/payos/webhook", Webhook(777_001, 1_000))).EnsureSuccessStatusCode();
        Assert.Equal("Free", (await (await c.GetAsync("/api/me/plan")).EnsureOk()).GetProperty("plan").GetProperty("code").GetString());

        (await anon.PostAsync("/api/payments/payos/webhook", Webhook(777_001, 69_000))).EnsureSuccessStatusCode();
        (await anon.PostAsync("/api/payments/payos/webhook", Webhook(777_001, 69_000))).EnsureSuccessStatusCode(); // gửi lại
        var me = await (await c.GetAsync("/api/me/plan")).EnsureOk();
        Assert.Equal("Pro", me.GetProperty("plan").GetProperty("code").GetString());
        var end = me.GetProperty("endAt").GetDateTime().ToUniversalTime();
        Assert.InRange((end - DateTime.UtcNow.AddMonths(1)).TotalMinutes, -5, 5);

        // PayOS gửi đơn thử (không có trong hệ thống) khi xác nhận URL webhook: phải trả 200.
        (await anon.PostAsync("/api/payments/payos/webhook", Webhook(123, 3_000))).EnsureSuccessStatusCode();
        var stored = await payments.Find(p => p.OrderCode == 777_001).FirstAsync();
        Assert.Equal((PlanPaymentStatus.Paid, "FT777001"), (stored.Status, stored.GatewayRef));
    }
}
