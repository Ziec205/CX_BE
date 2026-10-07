using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Listings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SkiaSharp;

namespace ChamXanh.Api.Tests;

[Collection(ApiCollection.Name)]
public class ListingApiTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    protected override void ConfigureSettings(IWebHostBuilder b)
    {
        b.UseSetting("Listings:RandomAuditRate", "0");
        b.UseSetting("Listings:ProSellerThreshold", "2");
    }

    int _imageSeed;

    /// <summary>Ảnh JPEG ngẫu nhiên (mỗi seed cho pHash khác nhau).</summary>
    static byte[] MakeJpeg(int seed)
    {
        var rnd = new Random(seed);
        using var bmp = new SKBitmap(480, 480);
        using (var canvas = new SKCanvas(bmp))
        {
            for (var i = 0; i < 12; i++)
                using (var paint = new SKPaint { Color = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)) })
                    canvas.DrawRect(rnd.Next(400), rnd.Next(400), 40 + rnd.Next(200), 40 + rnd.Next(200), paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    async Task<string> Upload(HttpClient c, int? seed = null, bool inApp = true, string kind = "ListingPhoto")
    {
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(MakeJpeg(seed ?? 1000 + Interlocked.Increment(ref _imageSeed))), "file", "cay.jpg" },
            { new StringContent(kind), "kind" },
            { new StringContent(inApp.ToString()), "capturedInApp" },
        };
        var res = await (await c.PostAsync("/api/media", content)).EnsureOk();
        return res.GetProperty("id").GetString()!;
    }

    async Task<List<string>> Photos(HttpClient c, int n = 3) { var l = new List<string>(); for (var i = 0; i < n; i++) l.Add(await Upload(c)); return l; }

    static object SenDa(List<string> media, string title = "Sen đá kim tuyến chậu nhỏ", long price = 80_000, string desc = "Cây khỏe mạnh, lên màu đẹp, giao nhanh trong TP.HCM") => new
    {
        listing = new
        {
            type = "Sell", categoryId = "sen-da", speciesId = "sen-da-kim-tuyen", title, description = desc, price,
            attributes = new { tinhTrang = "Trồng chậu", chieuCao = 12 }, mediaIds = media, lat = 10.7769, lng = 106.7009,
        },
        submit = true,
    };

    async Task<JsonElement> Create(HttpClient c, object body) => await (await c.PostAsJsonAsync("/api/listings", body)).EnsureOk();

    IMongoCollection<Listing> ListingsDb => Factory.Services.GetRequiredService<IMongoDatabase>().GetCollection<Listing>("listings");

    [Fact]
    public async Task Clean_listing_is_auto_approved_searchable_without_diacritics_and_by_species_alias()
    {
        var (seller, _) = await Member();
        var l = await Create(seller, SenDa(await Photos(seller)));
        Assert.Equal("Active", l.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, l.GetProperty("bumpedAt").ValueKind);

        var kimTien = await Create(seller, new
        {
            listing = new
            {
                type = "Sell", categoryId = "noi-that", speciesId = "kim-tien", title = "Cây văn phòng chậu sứ trắng", description = "Cây khỏe, hợp để bàn làm việc, chịu bóng tốt",
                price = 250_000, attributes = new { tinhTrang = "Trồng chậu", chieuCao = 40 }, mediaIds = await Photos(seller),
            },
            submit = true,
        });

        var anon = Anonymous();
        var r1 = await (await anon.GetAsync("/api/listings?q=sen%20da%20kim")).EnsureOk<SearchResult>();
        Assert.Contains(r1.Items, i => i.Id == l.GetProperty("id").GetString());
        // "kim phát tài" là tên khác của Kim tiền, tiêu đề tin không chứa cụm này
        var r2 = await (await anon.GetAsync("/api/listings?q=kim%20phat%20tai")).EnsureOk<SearchResult>();
        Assert.Contains(r2.Items, i => i.Id == kimTien.GetProperty("id").GetString());
        Assert.True(r2.Items.Single(i => i.Id == kimTien.GetProperty("id").GetString()).RealPhoto);

        var detail = await (await anon.GetAsync($"/api/listings/{l.GetProperty("id").GetString()}")).EnsureOk();
        Assert.Equal(3, detail.GetProperty("photoUrls").GetArrayLength());
        var photo = await anon.GetAsync(detail.GetProperty("photoUrls")[0].GetString());
        Assert.Equal("image/webp", photo.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", photo.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Profile_required_before_first_listing()
    {
        var (c, _) = await Member(completeProfile: false);
        var res = await c.PostAsJsonAsync("/api/listings", SenDa(await Photos(c)));
        Assert.Equal("PROFILE_REQUIRED", await res.ErrorCode());
    }

    [Fact]
    public async Task Validation_errors_contact_info_and_too_few_photos()
    {
        var (c, _) = await Member();
        var res = await c.PostAsJsonAsync("/api/listings", SenDa([], desc: "Liên hệ 0912345678 để có giá tốt nhất nhé"));
        Assert.Equal("VALIDATION_FAILED", await res.ErrorCode());
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("mediaIds", body);
        Assert.Contains("description", body);
    }

    [Fact]
    public async Task Banned_term_auto_rejected_without_violation_points()
    {
        var (c, userId) = await Member();
        var l = await Create(c, SenDa(await Photos(c), desc: "Bán kèm hạt giống cần sa nhập khẩu giá tốt"));
        Assert.Equal("Rejected", l.GetProperty("status").GetString());
        var me = await (await c.GetAsync("/api/me")).EnsureOk();
        Assert.Equal("Active", me.GetProperty("status").GetString());
        var super = await SuperAdmin();
        var cases = await (await super.GetAsync("/api/admin/moderation/cases")).EnsureOk();
        Assert.DoesNotContain(cases.EnumerateArray(), x => x.GetProperty("case").GetProperty("listingId").GetString() == l.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Restricted_category_goes_to_manual_review_and_moderator_decides()
    {
        var (c, _) = await Member();
        var l = await Create(c, new
        {
            listing = new
            {
                type = "Sell", categoryId = "phan-bon", title = "Phân bón hữu cơ trùn quế 5kg", description = "Phân trùn quế nguyên chất, dùng cho mọi loại cây",
                price = 90_000, attributes = new { loaiPhan = "Hữu cơ", soGiayPhep = "QĐ 123/2025" }, mediaIds = await Photos(c, 1), unit = "bao",
            },
            submit = true,
        });
        Assert.Equal("PendingReview", l.GetProperty("status").GetString());
        var id = l.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync($"/api/listings/{id}")).StatusCode);

        var mod = await Admin("mod.a", AdminRoles.Moderator);
        var cases = await (await mod.GetAsync("/api/admin/moderation/cases")).EnsureOk();
        var caseId = cases.EnumerateArray().First(x => x.GetProperty("case").GetProperty("listingId").GetString() == id).GetProperty("case").GetProperty("id").GetString();

        var noReason = await mod.PostAsJsonAsync($"/api/admin/moderation/cases/{caseId}/decide", new { decision = "Reject" });
        Assert.Equal("REASON_REQUIRED", await noReason.ErrorCode());
        (await mod.PostAsJsonAsync($"/api/admin/moderation/cases/{caseId}/decide", new { decision = "Approve" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync($"/api/listings/{id}")).StatusCode);
    }

    [Fact]
    public async Task Prohibited_rejection_by_moderator_locks_account()
    {
        var (c, _) = await Member();
        var l = await Create(c, SenDa(await Photos(c), desc: "Cây vườn nhà, không phải lan rừng tự nhiên đâu nhé"));
        Assert.Equal("PendingReview", l.GetProperty("status").GetString()); // phủ định → duyệt tay, không từ chối máy

        var mod = await Admin("mod.b", AdminRoles.Moderator);
        var cases = await (await mod.GetAsync("/api/admin/moderation/cases")).EnsureOk();
        var caseId = cases.EnumerateArray().First(x => x.GetProperty("case").GetProperty("listingId").GetString() == l.GetProperty("id").GetString())
            .GetProperty("case").GetProperty("id").GetString();
        (await mod.PostAsJsonAsync($"/api/admin/moderation/cases/{caseId}/decide", new { decision = "Reject", reasonCode = "PROHIBITED" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync("/api/listings/" + l.GetProperty("id").GetString())).StatusCode);

        // 10 điểm → khóa 30 ngày (BR-MOD-04): đăng nhập lại bị chặn
        var phone = (await (await c.GetAsync("/api/me")).EnsureOk()).GetProperty("phone").GetString();
        var otp = await (await Anonymous().PostAsJsonAsync("/api/auth/otp/request", new { phone })).EnsureOk();
        var locked = await Anonymous().PostAsJsonAsync("/api/auth/otp/verify", new { phone, code = otp.GetProperty("devCode").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
    }

    [Fact]
    public async Task Reposting_same_photo_is_rejected_as_duplicate()
    {
        var (c, _) = await Member();
        var photos = await Photos(c);
        await Create(c, SenDa(photos));
        var again = new List<string> { await Upload(c, seed: 1), await Upload(c), await Upload(c) };
        var first = new List<string> { await Upload(c, seed: 1), await Upload(c), await Upload(c) };
        await Create(c, SenDa(first, title: "Sen đá kim tuyến lô thứ hai"));
        var res = await c.PostAsJsonAsync("/api/listings", SenDa(again, title: "Sen đá kim tuyến đăng lại"));
        Assert.Equal("DUPLICATE_LISTING", await res.ErrorCode());
    }

    [Fact]
    public async Task Renew_after_expiry_keeps_bumped_at_and_price_edit_applies_immediately()
    {
        var (c, _) = await Member();
        var l = await Create(c, SenDa(await Photos(c)));
        var id = l.GetProperty("id").GetString()!;
        var bumped = (await ListingsDb.Find(x => x.Id == id).FirstAsync()).BumpedAt;

        await ListingsDb.UpdateOneAsync(x => x.Id == id, Builders<Listing>.Update.Set(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListingService>().ExpireDueAsync(default);
        Assert.Equal(ListingStatus.Expired, (await ListingsDb.Find(x => x.Id == id).FirstAsync()).Status);

        var renewed = await (await c.PostAsync($"/api/listings/{id}/renew", null)).EnsureOk();
        Assert.Equal("Active", renewed.GetProperty("status").GetString());
        var after = await ListingsDb.Find(x => x.Id == id).FirstAsync();
        Assert.Equal(bumped, after.BumpedAt); // BR-LST-15: gia hạn không phải đẩy tin miễn phí

        var edited = await (await c.PutAsJsonAsync($"/api/listings/{id}", SenDa(after.Media.Select(m => m.MediaId).ToList(), price: 70_000))).EnsureOk();
        Assert.Equal(70_000, edited.GetProperty("price").GetInt64());
        Assert.Equal(bumped, (await ListingsDb.Find(x => x.Id == id).FirstAsync()).BumpedAt);
    }

    [Fact]
    public async Task Edit_that_turns_into_another_item_requires_new_listing()
    {
        var (c, _) = await Member();
        var l = await Create(c, SenDa(await Photos(c)));
        var res = await c.PutAsJsonAsync($"/api/listings/{l.GetProperty("id").GetString()}", new
        {
            listing = new
            {
                type = "Sell", categoryId = "chau", title = "Chậu xi măng tròn 40cm", description = "Chậu xi măng đúc, có lỗ thoát nước, màu xám",
                price = 150_000, attributes = new { chatLieu = "Xi măng", dkMieng = 40 }, mediaIds = await Photos(c, 1),
            },
        });
        Assert.Equal("NEW_LISTING_REQUIRED", await res.ErrorCode());
    }

    [Fact]
    public async Task Weighted_reports_hide_listing_and_dismissal_restores_it()
    {
        var (seller, _) = await Member();
        var l = await Create(seller, SenDa(await Photos(seller)));
        var id = l.GetProperty("id").GetString()!;

        // Tài khoản mới: trọng số 0,3 → 3 báo cáo chưa đủ để tự ẩn
        var fresh = new List<HttpClient>();
        for (var i = 0; i < 3; i++) fresh.Add((await Member()).Client);
        foreach (var r in fresh) (await r.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "FakePhoto" })).EnsureSuccessStatusCode();
        Assert.Equal(ListingStatus.Active, (await ListingsDb.Find(x => x.Id == id).FirstAsync()).Status);
        var dup = await fresh[0].PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "FakePhoto" });
        Assert.Equal("ALREADY_REPORTED", await dup.ErrorCode());

        // Tài khoản lâu năm: trọng số 1
        var users = Factory.Services.GetRequiredService<IMongoDatabase>().GetCollection<Modules.Identity.User>("users");
        for (var i = 0; i < 3; i++)
        {
            var (r, uid) = await Member();
            await users.UpdateOneAsync(u => u.Id == uid, Builders<Modules.Identity.User>.Update.Set(u => u.CreatedAt, DateTime.UtcNow.AddDays(-60)));
            (await r.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "Scam", note = "ảnh mạng" })).EnsureSuccessStatusCode();
        }
        Assert.Equal(ListingStatus.TempHidden, (await ListingsDb.Find(x => x.Id == id).FirstAsync()).Status);

        var mod = await Admin("mod.c", AdminRoles.Moderator);
        var cases = await (await mod.GetAsync("/api/admin/moderation/cases?queue=Priority")).EnsureOk();
        var caseId = cases.EnumerateArray().First(x => x.GetProperty("case").GetProperty("listingId").GetString() == id).GetProperty("case").GetProperty("id").GetString();
        (await mod.PostAsJsonAsync($"/api/admin/moderation/cases/{caseId}/decide", new { decision = "Dismiss" })).EnsureSuccessStatusCode();
        Assert.Equal(ListingStatus.Active, (await ListingsDb.Find(x => x.Id == id).FirstAsync()).Status);
    }

    [Fact]
    public async Task Removed_listing_can_be_appealed_once_and_reviewed_by_another_moderator()
    {
        var (seller, _) = await Member();
        var l = await Create(seller, SenDa(await Photos(seller)));
        var id = l.GetProperty("id").GetString()!;
        var (reporter, _) = await Member();
        (await reporter.PostAsJsonAsync($"/api/listings/{id}/reports", new { reason = "WrongCategory" })).EnsureSuccessStatusCode();

        var modA = await Admin("mod.d", AdminRoles.Moderator);
        var caseId = (await (await modA.GetAsync("/api/admin/moderation/cases")).EnsureOk()).EnumerateArray()
            .First(x => x.GetProperty("case").GetProperty("listingId").GetString() == id).GetProperty("case").GetProperty("id").GetString();
        (await modA.PostAsJsonAsync($"/api/admin/moderation/cases/{caseId}/decide", new { decision = "Remove", reasonCode = "WRONG_CATEGORY" })).EnsureSuccessStatusCode();

        var appeal = await (await seller.PostAsJsonAsync($"/api/listings/{id}/appeal", new { reason = "Tôi đăng đúng danh mục sen đá" })).EnsureOk();
        Assert.Equal("APPEAL_USED", await (await seller.PostAsJsonAsync($"/api/listings/{id}/appeal", new { reason = "lần 2" })).ErrorCode());
        var appealCase = appeal.GetProperty("caseId").GetString();

        var sameMod = await modA.PostAsJsonAsync($"/api/admin/moderation/cases/{appealCase}/decide", new { decision = "Approve" });
        Assert.Equal(HttpStatusCode.Forbidden, sameMod.StatusCode);
        var modB = await Admin("mod.e", AdminRoles.Moderator);
        (await modB.PostAsJsonAsync($"/api/admin/moderation/cases/{appealCase}/decide", new { decision = "Approve" })).EnsureSuccessStatusCode();
        Assert.Equal(ListingStatus.Active, (await ListingsDb.Find(x => x.Id == id).FirstAsync()).Status);
    }

    [Fact]
    public async Task High_value_listing_needs_verification_photo_and_negotiable_price_only_for_bonsai()
    {
        var (c, _) = await Member();
        object Bonsai(string? verification, string category = "bonsai-mini") => new
        {
            listing = new
            {
                type = "Sell", categoryId = category, speciesId = "sanh", title = "Bonsai sanh dáng trực 25 năm tuổi", description = "Cây đã thuần, lá nhỏ, rễ đẹp, chậu gốm Bát Tràng",
                priceMode = "Negotiable", priceRefMin = 15_000_000, priceRefMax = 25_000_000,
                attributes = new { tinhTrang = "Trồng chậu", chieuCao = 28, dangThe = "Trực" }, mediaIds = new List<string>(), verificationMediaId = verification,
            },
            submit = true,
        };
        async Task<object> WithPhotos(string? verification, string category = "bonsai-mini")
        {
            var body = JsonSerializer.SerializeToNode(Bonsai(verification, category))!;
            body["listing"]!["mediaIds"] = JsonSerializer.SerializeToNode(await Photos(c));
            return body;
        }

        Assert.Equal("VERIFICATION_PHOTO_REQUIRED", await (await c.PostAsJsonAsync("/api/listings", await WithPhotos(null))).ErrorCode());
        Assert.Equal("VALIDATION_FAILED", await (await c.PostAsJsonAsync("/api/listings", await WithPhotos(null, "sen-da"))).ErrorCode());
        var ok = await Create(c, await WithPhotos(await Upload(c)));
        // Tài khoản cá nhân mới (+15) bán cây giá cao (+20) = 35 ≥ 30 → phải qua người duyệt
        Assert.Equal("PendingReview", ok.GetProperty("status").GetString());
        Assert.Equal(35, ok.GetProperty("riskScore").GetInt32());
    }

    [Fact]
    public async Task Pro_seller_label_hidden_phone_and_radius_search()
    {
        var (seller, sellerId) = await Member();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add((await Create(seller, SenDa(await Photos(seller), title: $"Sen đá kim tuyến lô số {i + 1}"))).GetProperty("id").GetString()!);
        var profile = await (await Anonymous().GetAsync($"/api/users/{sellerId}")).EnsureOk();
        Assert.True(profile.GetProperty("flags").GetProperty("isProSeller").GetBoolean()); // ngưỡng test = 2

        var (buyer, _) = await Member();
        var phone = await (await buyer.GetAsync($"/api/listings/{ids[0]}/phone")).EnsureOk();
        Assert.StartsWith("+84", phone.GetProperty("phone").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync($"/api/listings/{ids[0]}/phone")).StatusCode);
        (await seller.PutAsJsonAsync("/api/me", new { hidePhone = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.GetAsync($"/api/listings/{ids[1]}/phone")).StatusCode);

        // Q.1 TP.HCM: trong bán kính 5km có; từ Cần Thơ (≈ 140km) bán kính 20km thì không
        var near = await (await Anonymous().GetAsync($"/api/listings?sellerId={sellerId}&lat=10.78&lng=106.70&radiusKm=5&sort=Nearest")).EnsureOk<SearchResult>();
        Assert.Equal(3, near.Items.Count);
        Assert.All(near.Items, i => Assert.True(i.DistanceKm < 5));
        var far = await (await Anonymous().GetAsync($"/api/listings?sellerId={sellerId}&lat=10.03&lng=105.78&radiusKm=20")).EnsureOk<SearchResult>();
        Assert.Empty(far.Items);
    }

    [Fact]
    public async Task Dynamic_attribute_filter_and_secure_media_not_public()
    {
        var (c, _) = await Member();
        await Create(c, SenDa(await Photos(c), title: "Sen đá kim tuyến cây cao 12cm"));
        var tall = SenDa(await Photos(c), title: "Sen đá kim tuyến cây cao 30cm");
        var node = JsonSerializer.SerializeToNode(tall)!;
        node["listing"]!["attributes"]!["chieuCao"] = 30;
        await Create(c, node);

        var res = await (await Anonymous().GetAsync("/api/listings?categoryId=sen-da&attr.chieuCao=20..40&q=kim%20tuyen")).EnsureOk<SearchResult>();
        Assert.All(res.Items, i => Assert.Contains("30cm", i.Title));
        Assert.NotEmpty(res.Items);

        var kyc = await Upload(c, kind: "KycDocument");
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync($"/media/{kyc}/card.webp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/secure-media/{kyc}/card")).StatusCode);
        var (other, _) = await Member();
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/secure-media/{kyc}/card")).StatusCode);
    }

    [Fact]
    public async Task Catalog_endpoints_and_banned_species_hidden_from_search()
    {
        var anon = Anonymous();
        var cats = await (await anon.GetAsync("/api/categories")).EnsureOk();
        Assert.Equal(5, cats.GetArrayLength()); // Cây cảnh, Bonsai, Cây giống, Lan & hoa, Vật tư
        // Mỗi nhóm có mục "khác" đứng cuối để đăng được cây không có trong danh sách.
        foreach (var root in cats.EnumerateArray())
            Assert.EndsWith("khác", root.GetProperty("children").EnumerateArray().Last().GetProperty("name").GetString());
        var bonsai = await (await anon.GetAsync("/api/categories/bonsai-mini")).EnsureOk();
        Assert.Contains(bonsai.GetProperty("attributes").EnumerateArray(), a => a.GetProperty("key").GetString() == "dangThe");
        var sp = await (await anon.GetAsync("/api/species?q=ho%20vi")).EnsureOk();
        Assert.Contains(sp.EnumerateArray(), s => s.GetProperty("id").GetString() == "luoi-ho");
        var banned = await (await anon.GetAsync("/api/species?q=can%20sa")).EnsureOk();
        Assert.Equal(0, banned.GetArrayLength());

        var editor = await Admin("editor.b", AdminRoles.Editor);
        var sanh = await (await anon.GetAsync("/api/species/sanh")).EnsureOk();
        var node = JsonSerializer.SerializeToNode(sanh)!;
        node["legalFlag"] = "Banned";
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync("/api/admin/catalog/species/sanh", node)).StatusCode);
    }
}
