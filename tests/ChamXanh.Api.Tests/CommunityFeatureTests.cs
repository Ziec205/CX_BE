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
    [InlineData("Bạn chuyển cọc trước 500k mình giữ cây nhé", true)]
    [InlineData("Đọc giúp mình mã OTP vừa gửi về máy", true)]
    [InlineData("Cây còn không bạn, mai mình qua xem", false)]
    public void Detects_scam_patterns(string text, bool warned) =>
        Assert.Equal(warned, ScamDetector.Check(text, false, false) is not null);

    [Fact]
    public void New_account_moving_off_platform_on_first_message() =>
        Assert.NotNull(ScamDetector.Check("Kết bạn zalo mình trao đổi nhé", senderIsNewAccount: true, isFirstMessageFromSender: true));

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

    async Task<string> Listing(HttpClient c, string type = "Sell")
    {
        var media = new List<string> { await Upload(c), await Upload(c), await Upload(c) };
        var l = await (await c.PostAsJsonAsync("/api/listings", new
        {
            listing = new
            {
                type, categoryId = "noi-that", speciesId = "trau-ba", title = "Trầu bà leo cột cao 1m2", description = "Cây khỏe, lá xanh, leo cột dừa, hợp phòng khách",
                price = type == "Buy" ? (long?)null : 350_000, budgetMax = type == "Buy" ? 400_000 : (long?)null,
                attributes = new { tinhTrang = "Trồng chậu", chieuCao = 120 }, mediaIds = type == "Buy" ? new List<string>() : media,
                lat = 10.03, lng = 105.78,
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
        var (quoter, quoterId) = await Member();
        var conv = await (await quoter.PostAsJsonAsync("/api/conversations", new { listingId = requestId })).EnsureOk();
        Assert.Equal(buyerId, conv.GetProperty("buyerId").GetString());
        Assert.Equal(quoterId, conv.GetProperty("sellerId").GetString());
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
