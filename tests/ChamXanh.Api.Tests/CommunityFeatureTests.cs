using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Messaging;
using ChamXanh.Api.Modules.Reviews;
using ChamXanh.Api.Modules.Wallet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SkiaSharp;

namespace ChamXanh.Api.Tests;

public class ScamDetectorTests
{
    [Theory]
    [InlineData("Bạn chuyển cọc trước 500k mình giữ cây nhé", false, true)]
    [InlineData("Đọc giúp mình mã OTP vừa gửi về máy", true, true)]
    [InlineData("Cây còn không bạn, mai mình qua xem", false, false)]
    [InlineData("Bạn chuyển cọc trước nhé", true, false)] // tin có Giao dịch đảm bảo: không cảnh báo cọc
    public void Detects_scam_patterns(string text, bool escrow, bool warned) =>
        Assert.Equal(warned, ScamDetector.Check(text, escrow, false, false) is not null);

    [Fact]
    public void New_account_moving_off_platform_on_first_message() =>
        Assert.NotNull(ScamDetector.Check("Kết bạn zalo mình trao đổi nhé", false, senderIsNewAccount: true, isFirstMessageFromSender: true));

    [Fact]
    public void Weighted_score_needs_three_reviews()
    {
        var two = new List<Review> { new() { Stars = 5 }, new() { Stars = 1 } };
        Assert.Null(ReviewService.Score(two).Score);
        var three = new List<Review> { new() { Stars = 5, Tier = ReviewTier.Purchased }, new() { Stars = 1 }, new() { Stars = 1 } };
        Assert.Equal(3.4, ReviewService.Score(three).Score); // (5×3 + 1 + 1) / 5
    }
}

[Collection(ApiCollection.Name)]
public class CommunityFeatureTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    protected override void ConfigureSettings(IWebHostBuilder b) => b.UseSetting("Listings:RandomAuditRate", "0");

    static int _seed = 90_000;
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

    async Task<string> Upload(HttpClient c, string kind = "ListingPhoto")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Jpeg()), "file", "a.jpg" }, { new StringContent(kind), "kind" } };
        return (await (await c.PostAsync("/api/media", form)).EnsureOk()).GetProperty("id").GetString()!;
    }

    async Task<string> Listing(HttpClient c, bool escrow = false, string type = "Sell")
    {
        var media = new List<string> { await Upload(c), await Upload(c), await Upload(c) };
        var l = await (await c.PostAsJsonAsync("/api/listings", new
        {
            listing = new
            {
                type, categoryId = "noi-that", speciesId = "trau-ba", title = "Trầu bà leo cột cao 1m2", description = "Cây khỏe, lá xanh, leo cột dừa, hợp phòng khách",
                price = type == "Buy" ? (long?)null : 350_000, budgetMax = type == "Buy" ? 400_000 : (long?)null,
                attributes = new { tinhTrang = "Trồng chậu", chieuCao = 120 }, mediaIds = type == "Buy" ? new List<string>() : media,
                escrowEnabled = escrow, lat = 10.03, lng = 105.78,
            },
            submit = true,
        })).EnsureOk();
        return l.GetProperty("id").GetString()!;
    }

    IMongoDatabase Db => Factory.Services.GetRequiredService<IMongoDatabase>();

    [Fact]
    public async Task Chat_offer_flow_realtime_and_scam_warning_only_for_recipient()
    {
        var (seller, sellerId) = await Member();
        var (buyer, _) = await Member();
        var listingId = await Listing(seller);

        var token = seller.DefaultRequestHeaders.Authorization!.Parameter!;
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            }).Build();
        var received = new TaskCompletionSource<JsonElement>();
        var offerPushed = new TaskCompletionSource<JsonElement>();
        hub.On<JsonElement>("message", m =>
        {
            if (m.GetProperty("type").GetString() == "Offer" && m.GetProperty("offer").GetProperty("status").GetString() == "Accepted") offerPushed.TrySetResult(m);
            else received.TrySetResult(m);
        });
        await hub.StartAsync();

        var conv = await (await buyer.PostAsJsonAsync("/api/conversations", new { listingId })).EnsureOk();
        var convId = conv.GetProperty("id").GetString();
        Assert.Equal(sellerId, conv.GetProperty("sellerId").GetString());
        // Hội thoại mới chưa có tin nhắn: không nằm trong danh sách nhưng lấy được theo id
        Assert.Equal(0, (await (await buyer.GetAsync("/api/conversations")).EnsureOk()).GetArrayLength());
        Assert.Equal("buyer", (await (await buyer.GetAsync($"/api/conversations/{convId}")).EnsureOk()).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await (await Member()).Client.GetAsync($"/api/conversations/{convId}")).StatusCode);
        (await buyer.PostAsJsonAsync($"/api/conversations/{convId}/messages", new { type = "Text", text = "Bạn chuyển cọc trước giúp mình để giữ cây nhé" })).EnsureSuccessStatusCode();

        var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("chuyển cọc", pushed.GetProperty("text").GetString());
        Assert.Equal("Text", pushed.GetProperty("type").GetString()); // enum dạng chữ giống REST
        Assert.Equal(convId, pushed.GetProperty("conversationId").GetString());

        var sellerView = await (await seller.GetAsync($"/api/conversations/{convId}/messages")).EnsureOk();
        Assert.NotEqual(JsonValueKind.Null, sellerView[0].GetProperty("warning").ValueKind);
        var buyerView = await (await buyer.GetAsync($"/api/conversations/{convId}/messages")).EnsureOk();
        Assert.Equal(JsonValueKind.Null, buyerView[0].GetProperty("warning").ValueKind);

        var inbox = await (await seller.GetAsync("/api/conversations?role=selling")).EnsureOk();
        Assert.Equal(1, inbox[0].GetProperty("unread").GetInt32());

        var offer = await (await buyer.PostAsJsonAsync($"/api/conversations/{convId}/messages", new { type = "Offer", offerAmount = 300_000 })).EnsureOk();
        Assert.Equal("INVALID_OFFER", await (await seller.PostAsJsonAsync($"/api/conversations/{convId}/messages", new { type = "Offer", offerAmount = 1 })).ErrorCode());
        var accepted = await (await seller.PostAsJsonAsync($"/api/conversations/{convId}/offers/{offer.GetProperty("id").GetString()}/respond", new { accept = true })).EnsureOk();
        Assert.Equal("Accepted", accepted.GetProperty("offer").GetProperty("status").GetString());
        // Trạng thái mới của đề nghị giá được đẩy realtime để mọi thiết bị cập nhật
        Assert.Equal(offer.GetProperty("id").GetString(), (await offerPushed.Task.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("id").GetString());
        await hub.DisposeAsync();
        Assert.Equal("OFFER_CLOSED", await (await seller.PostAsJsonAsync($"/api/conversations/{convId}/offers/{offer.GetProperty("id").GetString()}/respond", new { accept = false })).ErrorCode());

        var stats = await (await Anonymous().GetAsync($"/api/users/{sellerId}/response-stats")).EnsureOk();
        Assert.Equal(1.0, stats.GetProperty("responseRate").GetDouble());
    }

    [Fact]
    public async Task Hub_token_opens_realtime_but_is_rejected_by_rest_api()
    {
        var (member, _) = await Member();
        var hubToken = (await (await member.PostAsync("/api/auth/hub-token", null)).EnsureOk()).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(hubToken).GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(hubToken).PostAsync("/api/auth/hub-token", null)).StatusCode);

        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(hubToken);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            }).Build();
        await hub.StartAsync();
        Assert.Equal(HubConnectionState.Connected, hub.State);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Buy_request_conversation_makes_quoting_member_the_seller()
    {
        var (buyer, buyerId) = await Member();
        var requestId = await Listing(buyer, type: "Buy");
        var (garden, gardenId) = await Member();
        var conv = await (await garden.PostAsJsonAsync("/api/conversations", new { listingId = requestId })).EnsureOk();
        Assert.Equal(buyerId, conv.GetProperty("buyerId").GetString());
        Assert.Equal(gardenId, conv.GetProperty("sellerId").GetString());
    }

    object Application(List<string> photos, List<string> docs, string idNumber, string name = "Nguyễn Văn Tư", string bankName = "NGUYEN VAN TU") => new
    {
        name = "Vườn kiểng Tư Chợ Lách", type = "Garden", description = "Chuyên mai vàng, bonsai", address = "Ấp Phú Hòa, xã Chợ Lách",
        provinceId = "86", lat = 10.26, lng = 106.13, openingHours = "7h-17h", allowVisit = true, photoMediaIds = photos,
        method = "VideoCall", idNumber, fullName = name, dateOfBirth = "1980-05-12", documentMediaIds = docs,
        bankCode = "VCB", bankAccountNo = "0123456789", bankAccountName = bankName,
    };

    [Fact]
    public async Task Garden_verification_plan_purchase_escrow_and_map()
    {
        var (owner, ownerId) = await Member();
        var photos = new List<string> { await Upload(owner), await Upload(owner) };
        var docs = new List<string> { await Upload(owner, "KycDocument"), await Upload(owner, "KycDocument") };

        Assert.Equal("VALIDATION_FAILED", await (await owner.PostAsJsonAsync("/api/garden/application", Application(photos, docs, "079080001234", bankName: "TRAN THI B"))).ErrorCode());
        Assert.Equal("INVALID_MEDIA", await (await owner.PostAsJsonAsync("/api/garden/application", Application(photos, photos, "079080001234"))).ErrorCode());
        var applied = await (await owner.PostAsJsonAsync("/api/garden/application", Application(photos, docs, "079080001234"))).EnsureOk();
        Assert.Equal("Submitted", applied.GetProperty("status").GetString());
        Assert.Equal("••••••6789", applied.GetProperty("bank").GetProperty("accountNoMasked").GetString());

        // Chưa xác minh: không bật được Giao dịch đảm bảo
        Assert.Equal("VALIDATION_FAILED", await (await owner.PostAsJsonAsync("/api/listings", new
        {
            listing = new { type = "Sell", categoryId = "chau", title = "Chậu xi măng tròn 40cm", description = "Chậu đúc dày, có lỗ thoát nước", price = 150_000,
                attributes = new { chatLieu = "Xi măng", dkMieng = 40 }, mediaIds = new[] { await Upload(owner) }, escrowEnabled = true },
        })).ErrorCode());

        // Cùng CCCD cho hồ sơ khác → chặn (BR-AUTH-02)
        var (other, _) = await Member();
        var otherPhotos = new List<string> { await Upload(other), await Upload(other) };
        var otherDocs = new List<string> { await Upload(other, "KycDocument"), await Upload(other, "KycDocument") };
        Assert.Equal("CCCD_ALREADY_USED", await (await other.PostAsJsonAsync("/api/garden/application", Application(otherPhotos, otherDocs, "079080001234"))).ErrorCode());

        var verifier = await Admin("verify.a", AdminRoles.Verification);
        var queue = await (await verifier.GetAsync("/api/admin/gardens")).EnsureOk();
        var gardenId = queue.EnumerateArray().First(g => g.GetProperty("profile").GetProperty("ownerId").GetString() == ownerId).GetProperty("profile").GetProperty("id").GetString();
        var kyc = await (await verifier.GetAsync($"/api/admin/gardens/{gardenId}/kyc")).EnsureOk();
        Assert.Equal("079080001234", kyc.GetProperty("idNumber").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Admin("mod.x", AdminRoles.Moderator)).GetAsync($"/api/admin/gardens/{gardenId}/kyc")).StatusCode);
        (await verifier.PostAsJsonAsync($"/api/admin/gardens/{gardenId}/review", new { decision = "Approve" })).EnsureSuccessStatusCode();

        var me = await (await owner.GetAsync("/api/me")).EnsureOk();
        Assert.True(me.GetProperty("flags").GetProperty("hasVerifiedGarden").GetBoolean());
        Assert.False(me.GetProperty("flags").GetProperty("hasActivePlan").GetBoolean());
        var escrowListing = await Listing(owner, escrow: true); // đã xác minh: bật được, chưa cần gói (sửa L31)

        // Mua gói 1 tháng = 199 Xu
        var tu = await (await owner.PostAsJsonAsync("/api/wallet/topups", new { packageCode = "CAY", expectedPriceBookVersion = 1 })).EnsureOk();
        var raw = JsonSerializer.Serialize(new { topUpId = tu.GetProperty("id").GetString(), status = "PAID", amountVnd = 200_000, gatewayRef = "GW-garden" }, JsonSerializerOptions.Web);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Signature", TopUpService.Sign("dev-webhook-secret", raw));
        (await Anonymous().SendAsync(req)).EnsureSuccessStatusCode();
        Assert.Equal("PRICE_CHANGED", await (await owner.PostAsJsonAsync("/api/garden/plan", new { months = 1, expectedPriceBookVersion = 1, expectedPriceXu = 100, idempotencyKey = "p1" })).ErrorCode());
        var plan = await (await owner.PostAsJsonAsync("/api/garden/plan", new { months = 1, expectedPriceBookVersion = 1, expectedPriceXu = 199, idempotencyKey = "p1" })).EnsureOk();
        Assert.True(plan.GetProperty("profile").GetProperty("tick").GetBoolean());

        var slug = plan.GetProperty("profile").GetProperty("slug").GetString();
        Assert.Equal("vuon-kieng-tu-cho-lach", slug);
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync($"/api/gardens/{slug}")).StatusCode);
        var map = await (await Anonymous().GetAsync("/api/gardens/map?lat=10.25&lng=106.12&radiusKm=10&speciesId=trau-ba")).EnsureOk();
        Assert.Contains(map.EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);
        var mapOtherSpecies = await (await Anonymous().GetAsync("/api/gardens/map?lat=10.25&lng=106.12&radiusKm=10&speciesId=mai-vang")).EnsureOk();
        Assert.Empty(mapOtherSpecies.EnumerateArray());

        // Đổi vị trí → phải xác minh lại, escrow bị tắt (BR-MAP-06)
        (await owner.PutAsJsonAsync("/api/garden", new { lat = 10.30, lng = 106.20 })).EnsureSuccessStatusCode();
        var after = await (await owner.GetAsync("/api/me")).EnsureOk();
        Assert.False(after.GetProperty("flags").GetProperty("hasVerifiedGarden").GetBoolean());
        var detail = await (await Anonymous().GetAsync($"/api/listings/{escrowListing}")).EnsureOk();
        Assert.False(detail.GetProperty("card").GetProperty("escrow").GetBoolean());
    }

    [Fact]
    public async Task Review_requires_seller_reply_after_24h_one_per_pair_and_single_reply()
    {
        var (seller, sellerId) = await Member();
        var (buyer, _) = await Member();
        var listingId = await Listing(seller);
        var conv = await (await buyer.PostAsJsonAsync("/api/conversations", new { listingId })).EnsureOk();
        var convId = conv.GetProperty("id").GetString()!;
        (await buyer.PostAsJsonAsync($"/api/conversations/{convId}/messages", new { type = "Text", text = "Cây còn không bạn?" })).EnsureSuccessStatusCode();

        var review = new { sellerId, stars = 5, tags = new[] { "Nhiệt tình" }, text = "Người bán dễ thương" };
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.PostAsJsonAsync("/api/reviews", review)).StatusCode); // chưa trả lời

        (await seller.PostAsJsonAsync($"/api/conversations/{convId}/messages", new { type = "Text", text = "Còn bạn nhé" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.PostAsJsonAsync("/api/reviews", review)).StatusCode); // chưa đủ 24h

        await Db.GetCollection<Conversation>("conversations").UpdateOneAsync(c => c.Id == convId,
            Builders<Conversation>.Update.Set(c => c.CreatedAt, DateTime.UtcNow.AddDays(-2)));
        var created = await (await buyer.PostAsJsonAsync("/api/reviews", review)).EnsureOk();
        (await buyer.PostAsJsonAsync("/api/reviews", review with { stars = 4 })).EnsureSuccessStatusCode(); // sửa lần 1
        Assert.Equal("REVIEW_LOCKED", await (await buyer.PostAsJsonAsync("/api/reviews", review with { stars = 3 })).ErrorCode());

        var id = created.GetProperty("id").GetString();
        (await seller.PostAsJsonAsync($"/api/reviews/{id}/reply", new { text = "Cảm ơn bạn!" })).EnsureSuccessStatusCode();
        Assert.Equal("REPLY_NOT_ALLOWED", await (await seller.PostAsJsonAsync($"/api/reviews/{id}/reply", new { text = "lần 2" })).ErrorCode());

        var summary = await (await Anonymous().GetAsync($"/api/users/{sellerId}/reviews")).EnsureOk();
        Assert.Equal(1, summary.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("score").ValueKind); // < 3 đánh giá chưa hiện điểm
    }

    [Fact]
    public async Task Favorites_follows_and_saved_searches()
    {
        var (seller, sellerId) = await Member();
        var (buyer, _) = await Member();
        var listingId = await Listing(seller);

        (await buyer.PutAsync($"/api/me/favorites/{listingId}", null)).EnsureSuccessStatusCode();
        (await seller.PutAsJsonAsync($"/api/listings/{listingId}", new
        {
            listing = new
            {
                type = "Sell", categoryId = "noi-that", speciesId = "trau-ba", title = "Trầu bà leo cột cao 1m2", description = "Cây khỏe, lá xanh, leo cột dừa, hợp phòng khách",
                price = 300_000, attributes = new { tinhTrang = "Trồng chậu", chieuCao = 120 },
                mediaIds = (await (await seller.GetAsync($"/api/listings/{listingId}")).EnsureOk()).GetProperty("photoUrls").EnumerateArray()
                    .Select(u => u.GetString()!.Split('/')[2]).ToList(),
                lat = 10.03, lng = 105.78,
            },
        })).EnsureSuccessStatusCode();
        var favs = await (await buyer.GetAsync("/api/me/favorites")).EnsureOk();
        Assert.True(favs[0].GetProperty("priceDropped").GetBoolean());

        (await buyer.PutAsync($"/api/me/follows/{sellerId}", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await (await Anonymous().GetAsync($"/api/users/{sellerId}/followers/count")).EnsureOk()).GetProperty("count").GetInt32());

        var saved = await (await buyer.PostAsJsonAsync("/api/me/saved-searches", new { name = "Trầu bà", query = new { q = "trau ba" } })).EnsureOk();
        await Listing(seller);
        var fresh = await (await buyer.GetAsync($"/api/me/saved-searches/{saved.GetProperty("id").GetString()}/new")).EnsureOk();
        Assert.True(fresh.GetArrayLength() >= 1);
    }
}
