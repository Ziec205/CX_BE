using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Wallet;
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

    [Fact]
    public async Task Bump_charges_once_per_idempotency_key_and_moves_listing_to_top()
    {
        var (seller, _) = await Member();
        await TopUp(seller);
        var older = await ActiveListing(seller, title: "Cây kim tiền cũ đăng trước");
        await ActiveListing(seller, title: "Cây kim tiền mới đăng sau");

        var body = new { service = "BUMP", days = (int?)null, expectedPriceBookVersion = 1, expectedPrice = 10, idempotencyKey = "k-1" };
        (await seller.PostAsJsonAsync($"/api/listings/{older}/promotions", body)).EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync($"/api/listings/{older}/promotions", body)).EnsureSuccessStatusCode(); // bấm 2 lần
        Assert.Equal(205, await Balance(seller));

        var list = await (await Anonymous().GetAsync("/api/listings?q=kim%20tien")).EnsureOk<SearchResult>();
        Assert.Equal(older, list.Items[0].Id);
    }

    [Fact]
    public async Task Price_change_and_insufficient_balance_and_give_listing_are_rejected()
    {
        var (seller, _) = await Member();
        var id = await ActiveListing(seller);
        var noMoney = await seller.PostAsJsonAsync($"/api/listings/{id}/promotions",
            new { service = "PRIORITY", days = 7, expectedPriceBookVersion = 1, expectedPrice = 140, idempotencyKey = "a" });
        Assert.Equal(HttpStatusCode.PaymentRequired, noMoney.StatusCode);

        await TopUp(seller);
        var wrongPrice = await seller.PostAsJsonAsync($"/api/listings/{id}/promotions",
            new { service = "PRIORITY", days = 7, expectedPriceBookVersion = 1, expectedPrice = 100, idempotencyKey = "b" });
        Assert.Equal("PRICE_CHANGED", await wrongPrice.ErrorCode());

        var give = await ActiveListing(seller, "Give", "Tặng cây kim tiền con tách chậu");
        var giveRes = await seller.PostAsJsonAsync($"/api/listings/{give}/promotions",
            new { service = "BUMP", days = (int?)null, expectedPriceBookVersion = 1, expectedPrice = 10, idempotencyKey = "c" });
        Assert.Equal("NOT_ALLOWED", await giveRes.ErrorCode());
        Assert.Equal(215, await Balance(seller));
    }

    [Fact]
    public async Task Priority_listing_appears_in_priority_block_and_extends_expiry()
    {
        var (seller, _) = await Member();
        await TopUp(seller, "VUON"); // 550 Xu
        var id = await ActiveListing(seller, title: "Kim tiền ưu tiên hiển thị đầu trang");
        var promo = await (await seller.PostAsJsonAsync($"/api/listings/{id}/promotions",
            new { service = "PRIORITY", days = 14, expectedPriceBookVersion = 1, expectedPrice = 240, idempotencyKey = "p" })).EnsureOk();
        Assert.Equal(240, promo.GetProperty("priceSnapshot").GetProperty("finalPrice").GetInt64());

        var res = await (await Anonymous().GetAsync("/api/listings?categoryId=noi-that")).EnsureOk<SearchResult>();
        Assert.Contains(res.Priority, p => p.Id == id && p.IsPriority);
        // tin khác danh mục không được chèn vào khối Ưu tiên (BR-SRC-02)
        var other = await (await Anonymous().GetAsync("/api/listings?categoryId=sen-da")).EnsureOk<SearchResult>();
        Assert.DoesNotContain(other.Priority, p => p.Id == id);

        var detail = await (await Anonymous().GetAsync($"/api/listings/{id}")).EnsureOk();
        Assert.True(detail.GetProperty("expiresAt").GetDateTime() >= detail.GetProperty("priorityUntil").GetDateTime()); // BR-XU-07 (1)
        Assert.Equal(310, await Balance(seller));
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
