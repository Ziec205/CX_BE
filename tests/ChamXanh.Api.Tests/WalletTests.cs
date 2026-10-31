using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Wallet;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Microsoft.AspNetCore.Hosting;
using SkiaSharp;

namespace ChamXanh.Api.Tests;

public class WalletAllocationTests
{
    static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Bonus_first_then_earliest_expiry_then_paid()
    {
        var lots = new List<XuLot>
        {
            new() { Id = "paid", Kind = LotKind.Paid, Remaining = 100, CreatedAt = Now.AddDays(-5) },
            new() { Id = "bonusLate", Kind = LotKind.Bonus, Remaining = 10, ExpiresAt = Now.AddDays(30), CreatedAt = Now },
            new() { Id = "bonusSoon", Kind = LotKind.Bonus, Remaining = 5, ExpiresAt = Now.AddDays(2), CreatedAt = Now },
            new() { Id = "bonusExpired", Kind = LotKind.Bonus, Remaining = 50, ExpiresAt = Now.AddDays(-1), CreatedAt = Now },
        };
        var alloc = WalletService.Allocate(lots, 20, Now)!;
        Assert.Equal(["bonusSoon", "bonusLate", "paid"], alloc.Select(a => a.LotId));
        Assert.Equal([5L, 10L, 5L], alloc.Select(a => a.Amount));
        Assert.Null(WalletService.Allocate(lots, 116, Now)); // lô hết hạn không được tính
    }
}

[Collection(ApiCollection.Name)]
public class WalletApiTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    const string Secret = "dev-webhook-secret";
    protected override void ConfigureSettings(IWebHostBuilder b) => b.UseSetting("Listings:RandomAuditRate", "0");

    static int _seed = 50_000;
    static byte[] Jpeg()
    {
        var rnd = new Random(Interlocked.Increment(ref _seed));
        using var bmp = new SKBitmap(420, 420);
        using (var c = new SKCanvas(bmp))
            for (var i = 0; i < 10; i++)
                using (var p = new SKPaint { Color = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)) })
                    c.DrawRect(rnd.Next(380), rnd.Next(380), 40 + rnd.Next(150), 40 + rnd.Next(150), p);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    async Task<string> ActiveListing(HttpClient c, string type = "Sell", string title = "Cây kim tiền để bàn chậu sứ")
    {
        var media = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(Jpeg()), "file", "a.jpg" } };
            media.Add((await (await c.PostAsync("/api/media", form)).EnsureOk()).GetProperty("id").GetString()!);
        }
        var l = await (await c.PostAsJsonAsync("/api/listings", new
        {
            listing = new
            {
                type, categoryId = "noi-that", speciesId = "kim-tien", title, description = "Cây khỏe, lá xanh bóng, hợp để văn phòng",
                price = type == "Give" ? 0 : 200_000, attributes = new { tinhTrang = "Trồng chậu", chieuCao = 50 }, mediaIds = media,
            },
            submit = true,
        })).EnsureOk();
        Assert.Equal("Active", l.GetProperty("status").GetString());
        return l.GetProperty("id").GetString()!;
    }

    async Task<HttpResponseMessage> Webhook(object payload, string? secret = Secret)
    {
        var raw = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        if (secret is not null) req.Headers.Add("X-Signature", TopUpService.Sign(secret, raw));
        return await Anonymous().SendAsync(req);
    }

    async Task TopUp(HttpClient c, string pkg = "CAY")
    {
        var t = await (await c.PostAsJsonAsync("/api/wallet/topups", new { packageCode = pkg, expectedPriceBookVersion = 1 })).EnsureOk();
        var id = t.GetProperty("id").GetString();
        var price = t.GetProperty("snapshot").GetProperty("priceVnd").GetInt64();
        (await Webhook(new { topUpId = id, status = "PAID", amountVnd = price, gatewayRef = $"GW-{id}" })).EnsureSuccessStatusCode();
    }

    async Task<long> Balance(HttpClient c) => (await (await c.GetAsync("/api/wallet")).EnsureOk()).GetProperty("balance").GetInt64();

    [Fact]
    public async Task Topup_webhook_credits_paid_and_bonus_once_and_rejects_bad_signature()
    {
        var (c, _) = await Member();
        var t = await (await c.PostAsJsonAsync("/api/wallet/topups", new { packageCode = "CAY", expectedPriceBookVersion = 1 })).EnsureOk();
        var id = t.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(new { topUpId = id, status = "PAID", amountVnd = 200_000, gatewayRef = "G1" }, "sai-khoa")).StatusCode);
        Assert.Equal("AMOUNT_MISMATCH", await (await Webhook(new { topUpId = id, status = "PAID", amountVnd = 100_000, gatewayRef = "G1" })).ErrorCode());

        (await Webhook(new { topUpId = id, status = "PAID", amountVnd = 200_000, gatewayRef = "G1" })).EnsureSuccessStatusCode();
        (await Webhook(new { topUpId = id, status = "PAID", amountVnd = 200_000, gatewayRef = "G1" })).EnsureSuccessStatusCode(); // gửi lại
        var w = await (await c.GetAsync("/api/wallet")).EnsureOk();
        Assert.Equal(215, w.GetProperty("balance").GetInt64());
        Assert.Equal(200, w.GetProperty("paid").GetInt64());
        Assert.Equal(15, w.GetProperty("bonus").GetInt64());
        Assert.Equal(2, (await (await c.GetAsync("/api/wallet/ledger")).EnsureOk()).GetArrayLength());

        var stale = await c.PostAsJsonAsync("/api/wallet/topups", new { packageCode = "CAY", expectedPriceBookVersion = 99 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    /// <summary>Gửi đăng một tin Bán; trả về response thô để test kiểm tra mã lỗi phí.</summary>
    async Task<HttpResponseMessage> Post(HttpClient c, string title, long? acceptFeeXu = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Jpeg()), "file", "a.jpg" } };
        var media = (await (await c.PostAsync("/api/media", form)).EnsureOk()).GetProperty("id").GetString()!;
        return await c.PostAsJsonAsync("/api/listings", new
        {
            listing = new
            {
                type = "Sell", categoryId = "noi-that", title, description = "Cây khỏe, lá xanh bóng, hợp để văn phòng",
                price = 200_000, attributes = new { tinhTrang = "Trồng chậu", chieuCao = 50 }, mediaIds = new[] { media },
            },
            submit = true, acceptFeeXu,
        });
    }

    async Task<JsonElement> Quota(HttpClient c) => await (await c.GetAsync("/api/me/listing-quota")).EnsureOk();

    [Fact]
    public async Task Free_plan_posts_three_listings_per_month_then_ten_xu_each_with_consent()
    {
        var (seller, _) = await Member();
        var q0 = await Quota(seller);
        Assert.Equal((3, 0, 30), (q0.GetProperty("monthlyQuota").GetInt32(), q0.GetProperty("nextCostXu").GetInt32(), q0.GetProperty("listingDays").GetInt32()));

        for (var i = 1; i <= 3; i++) (await Post(seller, $"Cây kim tiền miễn phí số {i}")).EnsureSuccessStatusCode();
        var q3 = await Quota(seller);
        Assert.Equal((3, 0, 10), (q3.GetProperty("usedThisMonth").GetInt32(), q3.GetProperty("remaining").GetInt32(), q3.GetProperty("nextCostXu").GetInt32()));

        // Tin thứ 4: chưa đồng ý phí thì không trừ Xu và không đăng.
        var noConsent = await Post(seller, "Cây kim tiền tin thứ tư chưa đồng ý");
        Assert.Equal(HttpStatusCode.PaymentRequired, noConsent.StatusCode);
        Assert.Equal("LISTING_FEE_REQUIRED", await noConsent.ErrorCode());
        // Đồng ý nhưng ví trống.
        Assert.Equal("INSUFFICIENT_XU", await (await Post(seller, "Cây kim tiền tin thứ tư ví trống", acceptFeeXu: 10)).ErrorCode());

        await TopUp(seller); // 215 Xu
        var paid = await (await Post(seller, "Cây kim tiền tin thứ tư trả phí", acceptFeeXu: 10)).EnsureOk();
        Assert.Equal("Active", paid.GetProperty("status").GetString());
        Assert.Equal(205, await Balance(seller));
        var detail = await (await Anonymous().GetAsync($"/api/listings/{paid.GetProperty("id").GetString()}")).EnsureOk();
        var days = (detail.GetProperty("expiresAt").GetDateTime() - detail.GetProperty("firstPublishedAt").GetDateTime()).TotalDays;
        Assert.InRange(days, 29.9, 30.1); // gói Miễn phí: tin hiển thị 30 ngày
        Assert.Contains((await (await seller.GetAsync("/api/wallet/ledger")).EnsureOk()).EnumerateArray(),
            e => e.GetProperty("refType").GetString() == "listingFee" && e.GetProperty("amount").GetInt64() == -10);
    }

    [Fact]
    public async Task Plus_plan_gets_thirty_listings_sixty_days_then_five_xu()
    {
        var (seller, _) = await Member();
        var pay = await (await seller.PostAsJsonAsync("/api/plans/checkout", new { plan = "Plus", months = 1 })).EnsureOk();
        (await seller.PostAsync($"/api/plans/payments/{pay.GetProperty("id").GetString()}/simulate", null)).EnsureSuccessStatusCode();

        var q = await Quota(seller);
        Assert.Equal(("Plus", 30, 5, 60), (q.GetProperty("plan").GetString(), q.GetProperty("monthlyQuota").GetInt32(),
            q.GetProperty("extraListingXu").GetInt32(), q.GetProperty("listingDays").GetInt32()));
        var l = await (await Post(seller, "Cây kim tiền gói Plus hiển thị 60 ngày")).EnsureOk();
        var detail = await (await Anonymous().GetAsync($"/api/listings/{l.GetProperty("id").GetString()}")).EnsureOk();
        Assert.InRange((detail.GetProperty("expiresAt").GetDateTime() - detail.GetProperty("firstPublishedAt").GetDateTime()).TotalDays, 59.9, 60.1);

        // Giả lập đã dùng hết 30 lượt trong tháng: tin tiếp theo 5 Xu.
        using var scope = Factory.Services.CreateScope();
        var fees = scope.ServiceProvider.GetRequiredService<ListingFeeService>();
        var month = ListingFeeService.MonthOf(DateTime.UtcNow);
        var userId = (await (await seller.GetAsync("/api/me")).EnsureOk()).GetProperty("id").GetString()!;
        await fees.Charges.InsertManyAsync(Enumerable.Range(0, 29).Select(i => new ListingCharge
        {
            Id = $"fake{i}:1", UserId = userId, ListingId = $"fake{i}", Cycle = 1, Month = month, FromQuota = true, At = DateTime.UtcNow,
        }));
        Assert.Equal(5, (await Quota(seller)).GetProperty("nextCostXu").GetInt32());
        await TopUp(seller, "MAM");
        (await Post(seller, "Cây kim tiền gói Plus vượt hạn mức", acceptFeeXu: 5)).EnsureSuccessStatusCode();
        Assert.Equal(45, await Balance(seller));
    }

    [Fact]
    public async Task Renewing_expired_listing_uses_a_new_posting_slot()
    {
        var (seller, _) = await Member();
        var id = (await (await Post(seller, "Cây kim tiền sẽ hết hạn rồi gia hạn")).EnsureOk()).GetProperty("id").GetString()!;
        for (var i = 2; i <= 3; i++) (await Post(seller, $"Cây kim tiền dùng hết lượt số {i}")).EnsureSuccessStatusCode();
        await Factory.Services.GetRequiredService<IMongoDatabase>().GetCollection<Listing>("listings")
            .UpdateOneAsync(l => l.Id == id, Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Expired));

        Assert.Equal("LISTING_FEE_REQUIRED", await (await seller.PostAsJsonAsync($"/api/listings/{id}/renew", new { })).ErrorCode());
        await TopUp(seller, "MAM");
        var renewed = await (await seller.PostAsJsonAsync($"/api/listings/{id}/renew", new { acceptFeeXu = 10 })).EnsureOk();
        Assert.Equal("Active", renewed.GetProperty("status").GetString());
        Assert.Equal(40, await Balance(seller));
    }

    [Fact]
    public async Task Large_admin_adjustment_needs_second_approver()
    {
        var (c, userId) = await Member();
        var acct1 = await Admin("acct.a", AdminRoles.Accountant);
        var small = await (await acct1.PostAsJsonAsync($"/api/admin/wallets/{userId}/adjustments", new { amount = 20, kind = "Bonus", reason = "Bồi hoàn lỗi" })).EnsureOk();
        Assert.Equal("Applied", small.GetProperty("status").GetString());

        var big = await (await acct1.PostAsJsonAsync($"/api/admin/wallets/{userId}/adjustments", new { amount = 500, kind = "Paid", reason = "Nạp tay" })).EnsureOk();
        Assert.Equal("Pending", big.GetProperty("status").GetString());
        var bigId = big.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await acct1.PostAsync($"/api/admin/wallets/adjustments/{bigId}/approve", null)).StatusCode);
        var acct2 = await Admin("acct.b", AdminRoles.Accountant);
        (await acct2.PostAsync($"/api/admin/wallets/adjustments/{bigId}/approve", null)).EnsureSuccessStatusCode();
        Assert.Equal(520, await Balance(c));
    }
}
