using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Escrow;
using ChamXanh.Api.Modules.PlantCare;
using ChamXanh.Api.Modules.Wallet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SkiaSharp;

namespace ChamXanh.Api.Tests;

public class ReminderScheduleTests
{
    static Recurrence R(DateTime anchorUtc, ReminderRepeat repeat, int interval = 1) => new(anchorUtc, repeat, interval);

    [Fact]
    public void Monthly_keeps_day_of_month_in_vietnam_time()
    {
        // 31/01/2026 07:30 giờ VN = 00:30 UTC
        var r = R(new DateTime(2026, 1, 31, 0, 30, 0, DateTimeKind.Utc), ReminderRepeat.Monthly);
        Assert.Equal(new DateTime(2026, 2, 28, 0, 30, 0), ReminderSchedule.Occurrence(r, 1));
        Assert.Equal(new DateTime(2026, 3, 31, 0, 30, 0), ReminderSchedule.Occurrence(r, 2));
    }

    [Fact]
    public void Next_after_skips_missed_occurrences_and_one_off_expires()
    {
        var anchor = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var daily = R(anchor, ReminderRepeat.Daily);
        Assert.Equal(new DateTime(2026, 3, 11), ReminderSchedule.NextAfter(daily, new DateTime(2026, 3, 10, 12, 0, 0)));
        var every3 = R(anchor, ReminderRepeat.Daily, 3);
        Assert.Equal(new DateTime(2026, 1, 4), ReminderSchedule.NextAfter(every3, anchor));
        Assert.Null(ReminderSchedule.NextAfter(R(anchor, ReminderRepeat.None), anchor.AddMinutes(1)));
        Assert.Equal(new DateTime(2027, 1, 1), ReminderSchedule.NextAfter(R(anchor, ReminderRepeat.Yearly), anchor.AddDays(3)));
    }

    [Fact]
    public void Payout_charges_fee_only_on_kept_goods()
    {
        Assert.Equal(1_010_000, EscrowService.SellerPayout(1_000_000, 50_000, 40_000, "SELLER", 0));
        Assert.Equal(0, EscrowService.SellerPayout(1_000_000, 50_000, 40_000, "SELLER", 1_050_000));
        Assert.Equal(528_000, EscrowService.SellerPayout(1_000_000, 50_000, 40_000, "SELLER", 500_000)); // giữ 550k, phí 40k × 550/1000 = 22k
    }
}

[Collection(ApiCollection.Name)]
public class NewFeatureTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    protected override void ConfigureSettings(IWebHostBuilder b)
    {
        b.UseSetting("Listings:RandomAuditRate", "0");
        b.UseSetting("Features:escrow", "true");
    }

    IMongoDatabase Db => Factory.Services.GetRequiredService<IMongoDatabase>();
    const string Secret = "dev-webhook-secret";

    static int _seed = 70_000;
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

    static async Task<string> Upload(HttpClient c, string kind = "ListingPhoto", string url = "/api/media")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Jpeg()), "file", "a.jpg" }, { new StringContent(kind), "kind" } };
        return (await (await c.PostAsync(url, form)).EnsureOk()).GetProperty("id").GetString()!;
    }

    static async Task<string> Listing(HttpClient c, bool escrow = false, string type = "Sell", object? rent = null, int quantity = 1)
    {
        var media = new List<string> { await Upload(c), await Upload(c), await Upload(c) };
        var l = await (await c.PostAsJsonAsync("/api/listings", new
        {
            listing = new
            {
                type, categoryId = "noi-that", speciesId = "trau-ba", title = "Trầu bà leo cột cao 1m2", description = "Cây khỏe, lá xanh, leo cột dừa, hợp phòng khách",
                price = type is "Sell" ? (long?)350_000 : null, budgetMax = type == "Buy" ? 400_000 : (long?)null, rent, quantity,
                attributes = new { tinhTrang = "Trồng chậu", chieuCao = 120 }, mediaIds = type == "Buy" ? new List<string>() : media,
                escrowEnabled = escrow, lat = 10.03, lng = 105.78,
            },
            submit = true,
        })).EnsureOk();
        return l.GetProperty("id").GetString()!;
    }

    async Task<(HttpClient Client, string Id)> VerifiedGarden()
    {
        var (owner, ownerId) = await Member();
        var photos = new List<string> { await Upload(owner), await Upload(owner) };
        var docs = new List<string> { await Upload(owner, "KycDocument"), await Upload(owner, "KycDocument") };
        var idNumber = $"0790800{Random.Shared.Next(10000, 99999)}";
        var applied = await (await owner.PostAsJsonAsync("/api/garden/application", new
        {
            name = "Vườn test " + idNumber, type = "Garden", description = "Test", address = "Ấp 1", provinceId = "86", lat = 10.26, lng = 106.13,
            openingHours = "7h-17h", allowVisit = true, photoMediaIds = photos, method = "VideoCall", idNumber, fullName = "Nguyễn Văn Tư",
            dateOfBirth = "1980-05-12", documentMediaIds = docs, bankCode = "VCB", bankAccountNo = "0123456789", bankAccountName = "NGUYEN VAN TU",
        })).EnsureOk();
        var super = await SuperAdmin();
        (await super.PostAsJsonAsync($"/api/admin/gardens/{applied.GetProperty("profile").GetProperty("id").GetString()}/review", new { decision = "Approve" })).EnsureSuccessStatusCode();
        return (owner, ownerId);
    }

    async Task<JsonElement> PayWebhook(string orderId, long amount)
    {
        var raw = JsonSerializer.Serialize(new { orderId, status = "PAID", amountVnd = amount, gatewayRef = "GW-" + orderId }, JsonSerializerOptions.Web);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/escrow/webhook") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Signature", TopUpService.Sign(Secret, raw));
        return await (await Anonymous().SendAsync(req)).EnsureOk();
    }

    // ---------------- Hồ sơ vườn + nhắc tưới ----------------

    [Fact]
    public async Task Garden_profile_plants_and_watering_reminders_fire_notifications()
    {
        var (member, userId) = await Member();
        var photo = await Upload(member, "PlantPhoto");
        var plant = await (await member.PostAsJsonAsync("/api/me/garden/plants", new { name = "Trầu bà ban công", speciesId = "trau-ba", mediaIds = new[] { photo }, location = "Ban công" })).EnsureOk();
        var plantId = plant.GetProperty("id").GetString()!;
        Assert.Equal("Trầu bà", plant.GetProperty("speciesName").GetString());

        // Một lần, thời điểm đã qua → từ chối
        Assert.Equal("TIME_IN_PAST", await (await member.PostAsJsonAsync($"/api/me/garden/plants/{plantId}/reminders",
            new { kind = "Watering", at = DateTimeOffset.UtcNow.AddHours(-1), repeat = "None" })).ErrorCode());
        // Hằng ngày lúc 07:30 giờ VN
        var at = new DateTimeOffset(DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1).AddHours(7).AddMinutes(30), DateTimeKind.Unspecified), TimeSpan.FromHours(7));
        var reminder = await (await member.PostAsJsonAsync($"/api/me/garden/plants/{plantId}/reminders",
            new { kind = "Watering", at, repeat = "Daily", note = "Tưới 200ml" })).EnsureOk();
        var rid = reminder.GetProperty("id").GetString()!;

        var list = await (await member.GetAsync("/api/me/garden/plants")).EnsureOk();
        Assert.Equal(1, list[0].GetProperty("reminders").GetArrayLength());
        Assert.Single(list[0].GetProperty("photos").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await (await Member()).Client.GetAsync($"/api/me/garden/plants/{plantId}")).StatusCode);

        // Giả lập đến hạn: đặt NextAt về quá khứ rồi chạy job
        var reminders = Db.GetCollection<CareReminder>("careReminders");
        var due = DateTime.UtcNow.AddMinutes(-1);
        await reminders.UpdateOneAsync(r => r.Id == rid, Builders<CareReminder>.Update.Set(r => r.NextAt, due).Set(r => r.AnchorAt, due.AddDays(-2)));
        using (var scope = Factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PlantCareService>().FireDueAsync(default));

        var notes = await (await member.GetAsync("/api/me/notifications")).EnsureOk();
        Assert.Equal(1, notes.GetProperty("unread").GetInt32());
        Assert.Equal("Tưới cây: Trầu bà ban công", notes.GetProperty("items")[0].GetProperty("title").GetString());
        var after = await reminders.Find(r => r.Id == rid).FirstAsync();
        Assert.True(after.NextAt > DateTime.UtcNow && after.NextAt < DateTime.UtcNow.AddDays(1).AddMinutes(1));

        var upcoming = await (await member.GetAsync("/api/me/garden/upcoming?days=2")).EnsureOk();
        Assert.Equal("Tưới cây", upcoming[0].GetProperty("label").GetString());

        (await member.PostAsync("/api/me/notifications/read-all", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await (await member.GetAsync("/api/me/notifications/unread-count")).EnsureOk()).GetProperty("count").GetInt32());

        (await member.DeleteAsync($"/api/me/garden/plants/{plantId}")).EnsureSuccessStatusCode();
        Assert.Equal(0, await reminders.CountDocumentsAsync(r => r.UserId == userId));
    }

    // ---------------- Khám phá ----------------

    [Fact]
    public async Task Explore_articles_are_scheduled_by_editors_and_shown_publicly()
    {
        var editor = await Admin("editor.x", AdminRoles.Editor);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Admin("mod.y", AdminRoles.Moderator)).GetAsync("/api/admin/explore")).StatusCode);
        var cover = await Upload(editor, url: "/api/admin/media");
        var body = string.Concat(Enumerable.Repeat("Cây lưỡi hổ lá xoắn là giống hiếm, chịu hạn tốt, lọc không khí. ", 3));

        Assert.Equal("COVER_REQUIRED", await (await editor.PostAsJsonAsync("/api/admin/explore", new { title = "Lưỡi hổ lá xoắn", body, status = "Published" })).ErrorCode());
        var future = await (await editor.PostAsJsonAsync("/api/admin/explore", new
        {
            title = "Lưỡi hổ lá xoắn Boncel", summary = "Giống lưỡi hổ ít người biết", body, mediaIds = new[] { cover }, status = "Published",
            publishAt = DateTime.UtcNow.AddDays(1),
        })).EnsureOk();
        Assert.Equal(HttpStatusCode.NoContent, (await Anonymous().GetAsync("/api/explore/today")).StatusCode);

        var now = await (await editor.PostAsJsonAsync("/api/admin/explore", new { title = "Cây ngọc ngân Thái", body, mediaIds = new[] { cover }, status = "Published" })).EnsureOk();
        var today = await (await Anonymous().GetAsync("/api/explore/today")).EnsureOk();
        Assert.Equal("cay-ngoc-ngan-thai", today.GetProperty("slug").GetString());
        Assert.Contains("thumb", today.GetProperty("cover").GetProperty("urls").EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, (await (await Anonymous().GetAsync("/api/explore")).EnsureOk()).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync($"/api/explore/{future.GetProperty("slug").GetString()}")).StatusCode);
        Assert.Equal("SLUG_TAKEN", await (await editor.PostAsJsonAsync("/api/admin/explore", new { title = "Cây ngọc ngân Thái", body, mediaIds = new[] { cover }, status = "Draft" })).ErrorCode());
        (await editor.DeleteAsync($"/api/admin/explore/{now.GetProperty("id").GetString()}")).EnsureSuccessStatusCode();
    }

    // ---------------- Giao dịch đảm bảo ----------------

    [Fact]
    public async Task Escrow_flow_delivery_dispute_partial_refund_payout_and_review()
    {
        var (seller, sellerId) = await VerifiedGarden();
        var listingId = await Listing(seller, escrow: true);
        var (buyer, buyerId) = await Member();

        Assert.Equal("CANNOT_BUY_OWN", await (await seller.PostAsJsonAsync("/api/escrow/orders", new { listingId, delivery = "SellerDelivery", deliveryAddress = "Q1" })).ErrorCode());
        var order = await (await buyer.PostAsJsonAsync("/api/escrow/orders", new { listingId, delivery = "SellerDelivery", shippingFee = 30_000, deliveryAddress = "12 Lê Lợi, Q1" })).EnsureOk();
        var id = order.GetProperty("id").GetString()!;
        Assert.Equal(380_000, order.GetProperty("total").GetInt64());
        Assert.False(order.TryGetProperty("pickupCode", out _));
        // Chỉ 1 cây: người thứ hai không giữ chỗ được (BR-ESC-17)
        Assert.Equal("OUT_OF_STOCK", await (await (await Member()).Client.PostAsJsonAsync("/api/escrow/orders", new { listingId, delivery = "SellerDelivery", deliveryAddress = "Q3" })).ErrorCode());

        (await buyer.PostAsync($"/api/escrow/orders/{id}/pay", null)).EnsureSuccessStatusCode();
        Assert.Equal("AwaitingSellerConfirm", (await PayWebhook(id, 380_000)).GetProperty("status").GetString());
        Assert.Equal("AwaitingSellerConfirm", (await PayWebhook(id, 380_000)).GetProperty("status").GetString()); // gửi lại: idempotent

        (await seller.PostAsync($"/api/escrow/orders/{id}/confirm", null)).EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync($"/api/escrow/orders/{id}/ship", new { })).EnsureSuccessStatusCode();
        Assert.Equal("PROOF_REQUIRED", await (await seller.PostAsJsonAsync($"/api/escrow/orders/{id}/delivered", new { })).ErrorCode());
        (await seller.PostAsJsonAsync($"/api/escrow/orders/{id}/delivered", new { mediaIds = new[] { await Upload(seller) } })).EnsureSuccessStatusCode();

        Assert.Equal("EVIDENCE_REQUIRED", await (await buyer.PostAsJsonAsync($"/api/escrow/orders/{id}/dispute", new { reason = "DeadOrWilted", description = "Cây héo rũ khi mở hộp", hasUnboxingVideo = true })).ErrorCode());
        (await buyer.PostAsJsonAsync($"/api/escrow/orders/{id}/dispute", new { reason = "DeadOrWilted", description = "Cây héo rũ khi mở hộp", mediaIds = new[] { await Upload(buyer) }, hasUnboxingVideo = true })).EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync($"/api/escrow/orders/{id}/dispute/respond", new { response = "OfferPartialRefund", amount = 100_000, note = "Hỗ trợ 100k" })).EnsureSuccessStatusCode();
        var resolved = await (await buyer.PostAsync($"/api/escrow/orders/{id}/dispute/accept-proposal", null)).EnsureOk();
        Assert.Equal("PartiallyRefunded", resolved.GetProperty("status").GetString());
        Assert.Equal(100_000, resolved.GetProperty("refundAmount").GetInt64());

        // Quyết toán T+1: kế toán duyệt sau hạn
        var accountant = await Admin("acc.x", AdminRoles.Accountant);
        Assert.Equal("TOO_EARLY", await (await accountant.PostAsync($"/api/admin/escrow/orders/{id}/payout", null)).ErrorCode());
        await Db.GetCollection<EscrowOrder>("escrowOrders").UpdateOneAsync(o => o.Id == id, Builders<EscrowOrder>.Update.Set(o => o.PayoutEligibleAt, DateTime.UtcNow.AddMinutes(-1)));
        var settled = await (await accountant.PostAsync($"/api/admin/escrow/orders/{id}/payout", null)).EnsureOk();
        Assert.Equal("Settled", settled.GetProperty("status").GetString());
        Assert.True(settled.GetProperty("payoutAmount").GetInt64() is > 0 and < 280_000);

        var review = await (await buyer.PostAsJsonAsync($"/api/escrow/orders/{id}/review", new { stars = 4, text = "Hỗ trợ nhanh" })).EnsureOk();
        Assert.Equal("Purchased", review.GetProperty("tier").GetString());
        Assert.Equal("ALREADY_REVIEWED", await (await buyer.PostAsJsonAsync($"/api/escrow/orders/{id}/review", new { stars = 5 })).ErrorCode());
        Assert.Contains((await (await buyer.GetAsync("/api/me/notifications")).EnsureOk()).GetProperty("items").EnumerateArray(), n => n.GetProperty("type").GetString() == "escrow");
        _ = sellerId; _ = buyerId;
    }

    [Fact]
    public async Task Escrow_pickup_by_qr_and_unpaid_order_expires_releasing_stock()
    {
        var (seller, _) = await VerifiedGarden();
        var listingId = await Listing(seller, escrow: true, quantity: 2);
        var (buyer, _) = await Member();

        // Đơn không thanh toán: hết hạn → trả lại số lượng
        var stale = await (await buyer.PostAsJsonAsync("/api/escrow/orders", new { listingId, quantity = 2, delivery = "BuyerPickup", pickupDate = DateTime.UtcNow.Date.AddDays(1) })).EnsureOk();
        Assert.Equal("OUT_OF_STOCK", await (await buyer.PostAsJsonAsync("/api/escrow/orders", new { listingId, delivery = "BuyerPickup", pickupDate = DateTime.UtcNow.Date.AddDays(1) })).ErrorCode());
        var staleId = stale.GetProperty("id").GetString();
        await Db.GetCollection<EscrowOrder>("escrowOrders").UpdateOneAsync(o => o.Id == staleId, Builders<EscrowOrder>.Update.Set(o => o.PaymentDueAt, DateTime.UtcNow.AddMinutes(-1)));
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<EscrowService>().RunDeadlinesAsync(default);
        // Tiền về muộn sau khi hủy: tự hoàn (BR-ESC-18)
        Assert.Equal("Cancelled", (await PayWebhook(staleId!, 700_000)).GetProperty("status").GetString());

        var order = await (await buyer.PostAsJsonAsync("/api/escrow/orders", new { listingId, delivery = "BuyerPickup", pickupDate = DateTime.UtcNow.Date.AddDays(1) })).EnsureOk();
        var id = order.GetProperty("id").GetString()!;
        await PayWebhook(id, order.GetProperty("total").GetInt64());
        (await seller.PostAsync($"/api/escrow/orders/{id}/confirm", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync($"/api/escrow/orders/{id}/pickup-code")).StatusCode);
        var code = (await (await buyer.GetAsync($"/api/escrow/orders/{id}/pickup-code")).EnsureOk()).GetProperty("code").GetString();
        Assert.Equal("INVALID_PICKUP_CODE", await (await seller.PostAsJsonAsync($"/api/escrow/orders/{id}/pickup", new { code = "WRONG" })).ErrorCode());
        var done = await (await seller.PostAsJsonAsync($"/api/escrow/orders/{id}/pickup", new { code })).EnsureOk();
        Assert.Equal("Completed", done.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Escrow_is_hidden_behind_feature_flag()
    {
        var super = await SuperAdmin();
        (await super.PutAsJsonAsync("/api/admin/features/escrow", new { enabled = false, note = "Chưa ký đối tác" })).EnsureSuccessStatusCode();
        var (buyer, _) = await Member();
        Assert.Equal("FEATURE_DISABLED", await (await buyer.PostAsJsonAsync("/api/escrow/orders", new { listingId = "000000000000000000000000", delivery = "SellerDelivery", deliveryAddress = "x" })).ErrorCode());
        Assert.False((await (await Anonymous().GetAsync("/api/features")).EnsureOk()).GetProperty("escrow").GetBoolean());
    }

    // ---------------- Cộng đồng ----------------

    [Fact]
    public async Task Community_questions_best_answer_points_sale_flag_and_auto_hide()
    {
        var (asker, _) = await Member();
        var (expert, expertId) = await Member();
        var q = await (await asker.PostAsJsonAsync("/api/community/posts", new { type = "Question", title = "Trầu bà bị vàng lá phải làm sao?", body = "Cây nhà mình vàng lá dưới gốc 1 tuần nay", speciesIds = new[] { "trau-ba" }, topics = new[] { "Cây nội thất" } })).EnsureOk();
        var postId = q.GetProperty("id").GetString()!;
        Assert.False(q.GetProperty("suggestListing").GetBoolean());

        var answer = await (await expert.PostAsJsonAsync($"/api/community/posts/{postId}/comments", new { body = "Giảm tưới, để nơi sáng gián tiếp" })).EnsureOk();
        Assert.Equal(HttpStatusCode.Forbidden, (await expert.PostAsync($"/api/community/posts/{postId}/best/{answer.GetProperty("id").GetString()}", null)).StatusCode);
        (await asker.PostAsync($"/api/community/posts/{postId}/best/{answer.GetProperty("id").GetString()}", null)).EnsureSuccessStatusCode();
        (await asker.PutAsync($"/api/community/posts/{postId}/like", null)).EnsureSuccessStatusCode();
        var profile = await (await Anonymous().GetAsync($"/api/community/users/{expertId}")).EnsureOk();
        Assert.Equal(10, profile.GetProperty("points").GetInt32());
        Assert.Equal("Mầm", profile.GetProperty("badge").GetString());

        var detail = await (await Anonymous().GetAsync($"/api/community/posts/{postId}")).EnsureOk();
        Assert.True(detail.GetProperty("comments")[0].GetProperty("isBest").GetBoolean());
        Assert.Equal(1, detail.GetProperty("post").GetProperty("likes").GetInt32());
        var list = await (await Anonymous().GetAsync("/api/community/posts?q=vang la&type=Question")).EnsureOk();
        Assert.Single(list.GetProperty("items").EnumerateArray());

        // BR-COM-01: bài có giá → gợi ý chuyển thành tin đăng
        var sale = await (await asker.PostAsJsonAsync("/api/community/posts", new { type = "Showcase", title = "Khoe chậu sen đá mới", body = "Ai cần inbox giá nhé, 150k một chậu", mediaIds = new[] { await Upload(asker) } })).EnsureOk();
        Assert.True(sale.GetProperty("suggestListing").GetBoolean());

        // BR-COM-02: 3 báo cáo → ẩn
        var saleId = sale.GetProperty("id").GetString();
        for (var i = 0; i < 3; i++)
            (await (await Member()).Client.PostAsJsonAsync($"/api/community/posts/{saleId}/report", new { reason = "Rao bán" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync($"/api/community/posts/{saleId}")).StatusCode);
        var editor = await Admin("editor.c", AdminRoles.Editor);
        Assert.Contains((await (await editor.GetAsync("/api/admin/community/reported")).EnsureOk()).GetProperty("posts").EnumerateArray(), p => p.GetProperty("id").GetString() == saleId);
    }

    // ---------------- Báo giá, thuê cây, xóa tài khoản ----------------

    [Fact]
    public async Task Quotes_on_buy_request_and_rental_calendar_prevents_overbooking()
    {
        var (buyer, _) = await Member();
        var requestId = await Listing(buyer, type: "Buy");
        var (quoter, _) = await Member();
        var quote = await (await quoter.PostAsJsonAsync($"/api/listings/{requestId}/quotes", new { unitPrice = 320_000, quantity = 1, note = "Có sẵn" })).EnsureOk();
        Assert.Single((await (await buyer.GetAsync($"/api/listings/{requestId}/quotes")).EnsureOk()).EnumerateArray());
        var selected = await (await buyer.PostAsync($"/api/quotes/{quote.GetProperty("id").GetString()}/select", null)).EnsureOk();
        Assert.Equal("Selected", selected.GetProperty("status").GetString());

        var (owner, _) = await Member();
        var rentId = await Listing(owner, type: "Rent", rent: new { unit = "Day", pricePerUnit = 50_000, deposit = 200_000, minUnits = 1 }, quantity: 1);
        var start = DateTime.UtcNow.Date.AddDays(3);
        var (r1, _) = await Member();
        var (r2, _) = await Member();
        var b1 = await (await r1.PostAsJsonAsync($"/api/listings/{rentId}/rentals", new { startDate = start, endDate = start.AddDays(2) })).EnsureOk();
        Assert.Equal(150_000, b1.GetProperty("priceEstimate").GetInt64());
        var b2 = await (await r2.PostAsJsonAsync($"/api/listings/{rentId}/rentals", new { startDate = start.AddDays(1), endDate = start.AddDays(4) })).EnsureOk();
        (await owner.PostAsJsonAsync($"/api/rentals/{b1.GetProperty("id").GetString()}/respond", new { accept = true })).EnsureSuccessStatusCode();
        Assert.Equal("NOT_AVAILABLE", await (await owner.PostAsJsonAsync($"/api/rentals/{b2.GetProperty("id").GetString()}/respond", new { accept = true })).ErrorCode());
        var cal = await (await Anonymous().GetAsync($"/api/listings/{rentId}/rental-calendar")).EnsureOk();
        Assert.Equal(3, cal.GetProperty("busyDays").GetArrayLength());
    }

    [Fact]
    public async Task Account_deletion_is_blocked_by_open_orders_then_anonymizes()
    {
        var (seller, _) = await VerifiedGarden();
        var listingId = await Listing(seller, escrow: true);
        var (buyer, buyerId) = await Member();
        var order = await (await buyer.PostAsJsonAsync("/api/escrow/orders", new { listingId, delivery = "SellerDelivery", deliveryAddress = "Q1" })).EnsureOk();
        await PayWebhook(order.GetProperty("id").GetString()!, order.GetProperty("total").GetInt64());

        var check = await (await buyer.GetAsync("/api/me/account/deletion-check")).EnsureOk();
        Assert.False(check.GetProperty("canDelete").GetBoolean());
        var otp = (await (await buyer.PostAsync("/api/me/account/deletion-otp", null)).EnsureOk()).GetProperty("devCode").GetString();
        Assert.Equal("OPEN_ORDERS", await (await buyer.PostAsJsonAsync("/api/me/account/delete", new { otpCode = otp, acknowledgeLosses = true })).ErrorCode());

        var (member, memberId) = await Member();
        (await member.PostAsJsonAsync("/api/me/garden/plants", new { name = "Sen đá" })).EnsureSuccessStatusCode();
        var code = (await (await member.PostAsync("/api/me/account/deletion-otp", null)).EnsureOk()).GetProperty("devCode").GetString();
        Assert.Equal("ACK_REQUIRED", await (await member.PostAsJsonAsync("/api/me/account/delete", new { otpCode = code, acknowledgeLosses = false })).ErrorCode());
        (await member.PostAsJsonAsync("/api/me/account/delete", new { otpCode = code, acknowledgeLosses = true })).EnsureSuccessStatusCode();
        var pub = await (await Anonymous().GetAsync($"/api/users/{memberId}")).EnsureOk();
        Assert.Equal("Người dùng đã xóa", pub.GetProperty("displayName").GetString());
        _ = buyerId;
    }

    [Fact]
    public async Task Admin_dashboard_members_and_support_tickets()
    {
        var (member, memberId) = await Member();
        var ticket = await (await member.PostAsJsonAsync("/api/support/tickets", new { topic = "Account", subject = "Không đổi được tên", message = "Mình đổi tên hiển thị không được ạ" })).EnsureOk();
        var support = await Admin("cskh.x", AdminRoles.Support);
        (await support.PostAsJsonAsync($"/api/admin/support/tickets/{ticket.GetProperty("id").GetString()}/reply", new { message = "Bạn thử lại giúp mình" })).EnsureSuccessStatusCode();
        var mine = await (await member.GetAsync($"/api/support/tickets/{ticket.GetProperty("id").GetString()}")).EnsureOk();
        Assert.Equal("WaitingCustomer", mine.GetProperty("status").GetString());

        var super = await SuperAdmin();
        var dash = await (await super.GetAsync("/api/admin/reports/dashboard")).EnsureOk();
        Assert.True(dash.GetProperty("users").GetProperty("total").GetInt64() >= 1);
        var csv = await super.GetAsync("/api/admin/reports/export/escrow-orders");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);

        var mod = await Admin("mod.z", AdminRoles.Moderator);
        (await mod.PostAsJsonAsync($"/api/admin/members/{memberId}/sanction", new { action = "lock", days = 3, reason = "Spam" })).EnsureSuccessStatusCode();
        Assert.Equal("Locked", (await (await mod.GetAsync($"/api/admin/members/{memberId}")).EnsureOk()).GetProperty("user").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsJsonAsync($"/api/admin/members/{memberId}/sanction", new { action = "ban", reason = "x" })).StatusCode);
    }
}
