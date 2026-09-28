using System.Text.RegularExpressions;
using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Pricing;
using ChamXanh.Api.Modules.Wallet;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace ChamXanh.Api.Modules.Gardens;

public record GardenApplication(
    string Name, GardenType Type, string? Description, string Address, string ProvinceId, string? WardId, double Lat, double Lng,
    string? OpeningHours, bool AllowVisit, List<string> PhotoMediaIds,
    VerificationMethod Method, string? BusinessLicenseNo,
    string IdNumber, string FullName, DateOnly DateOfBirth, List<string> DocumentMediaIds,
    string BankCode, string BankAccountNo, string BankAccountName);

public record GardenUpdate(string? Name, string? Description, string? Address, string? WardId, double? Lat, double? Lng,
    string? OpeningHours, bool? AllowVisit, List<string>? PhotoMediaIds, string? CoverMediaId, List<string>? PinnedListingIds);

public record BuyPlanRequest(int Months, int ExpectedPriceBookVersion, long ExpectedPriceXu, string IdempotencyKey, bool AutoRenew = true);

public enum ReviewDecision { Approve, NeedsInfo, Reject, Revoke }
public record ReviewRequest(ReviewDecision Decision, string? Note);
public record GrantPlanRequest(int Months, bool Founding);

public partial class GardenService(IMongoDatabase db, IMongoClient client, TimeProvider clock, UserService users, MediaService media,
    DataProtector protector, WalletService wallets, PricingService pricing, ListingService listings)
{
    public IMongoCollection<GardenProfile> Gardens { get; } = db.GetCollection<GardenProfile>("gardenProfiles");

    [GeneratedRegex(@"^\d{12}$")] private static partial Regex CccdFormat();

    public async Task EnsureIndexesAsync()
    {
        var k = Builders<GardenProfile>.IndexKeys;
        await Gardens.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<GardenProfile>(k.Ascending(g => g.OwnerId), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<GardenProfile>(k.Ascending(g => g.Slug), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<GardenProfile>(k.Ascending("kyc.idNumberHmac"), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<GardenProfile>(k.Geo2DSphere(g => g.Location)),
            new CreateIndexModel<GardenProfile>(k.Ascending(g => g.Status).Ascending("plan.endAt")),
        ]);
    }

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<GardenProfile?> FindByOwnerAsync(string ownerId, CancellationToken ct) =>
        Gardens.Find(g => g.OwnerId == ownerId).FirstOrDefaultAsync(ct)!;

    public async Task<GardenProfile> GetAsync(string id, CancellationToken ct) =>
        (ObjectId.TryParse(id, out _) ? await Gardens.Find(g => g.Id == id).FirstOrDefaultAsync(ct) : null) ?? throw DomainException.NotFound("hồ sơ nhà vườn");

    /// <summary>Nộp (hoặc nộp lại khi bị yêu cầu bổ sung/từ chối) hồ sơ xác minh Nhà vườn/Shop — T1, tài liệu 02 §3.</summary>
    public async Task<GardenProfile> ApplyAsync(string userId, GardenApplication a, CancellationToken ct)
    {
        var owner = await users.RequireActiveAsync(userId, ct);
        if (owner.Phone is null)
            throw new DomainException("PHONE_REQUIRED", "Nhà vườn/Shop cần xác thực số điện thoại trước khi nộp hồ sơ (Tài khoản → Số điện thoại)");
        var existing = await FindByOwnerAsync(userId, ct);
        if (existing is not null && existing.Status is not (VerificationStatus.NeedsInfo or VerificationStatus.Rejected))
            throw DomainException.Conflict("ALREADY_APPLIED", "Bạn đã có hồ sơ Nhà vườn/Shop");

        var errors = new List<string>();
        if (a.Name?.Trim().Length is not (>= 3 and <= 80)) errors.Add("Tên vườn/shop dài 3–80 ký tự");
        if (string.IsNullOrWhiteSpace(a.Address)) errors.Add("Thiếu địa chỉ");
        if (a.Lat is < -90 or > 90 || a.Lng is < -180 or > 180) errors.Add("Tọa độ không hợp lệ");
        if (!CccdFormat().IsMatch(a.IdNumber ?? "")) errors.Add("Số CCCD phải gồm 12 chữ số");
        if (a.DateOfBirth > DateOnly.FromDateTime(Now).AddYears(-18)) errors.Add("Chủ vườn/shop phải từ 18 tuổi");
        if (a.Method == VerificationMethod.BusinessLicense && string.IsNullOrWhiteSpace(a.BusinessLicenseNo)) errors.Add("Thiếu số GPKD/MST");
        if (a.PhotoMediaIds is not { Count: >= 2 and <= 20 }) errors.Add("Cần 2–20 ảnh vườn/shop thật");
        if (a.DocumentMediaIds is not { Count: >= 2 and <= 4 }) errors.Add("Cần ảnh 2 mặt CCCD (2–4 ảnh)");
        // BR-ESC-01: tài khoản ngân hàng nhận tiền phải trùng tên CCCD.
        if (VietnameseText.Normalize(a.BankAccountName) != VietnameseText.Normalize(a.FullName)) errors.Add("Tên chủ tài khoản ngân hàng phải trùng tên trên CCCD");
        if (string.IsNullOrWhiteSpace(a.BankCode) || a.BankAccountNo?.Length is not (>= 6 and <= 20)) errors.Add("Thông tin tài khoản ngân hàng không hợp lệ");
        if (errors.Count > 0) throw new DomainException("VALIDATION_FAILED", "Hồ sơ chưa hợp lệ", details: errors);

        var photos = await media.GetOwnedAsync(a.PhotoMediaIds!, userId, ct);
        var docs = await media.GetOwnedAsync(a.DocumentMediaIds!, userId, ct);
        if (docs.Any(d => d.Kind != MediaKind.KycDocument)) throw new DomainException("INVALID_MEDIA", "Ảnh CCCD phải được tải lên với loại KycDocument (kho riêng tư)");

        var hmac = protector.Hmac(a.IdNumber!);
        var dup = await Gardens.Find(Builders<GardenProfile>.Filter.Eq("kyc.idNumberHmac", hmac) & Builders<GardenProfile>.Filter.Ne(g => g.OwnerId, userId)).AnyAsync(ct);
        if (dup) throw DomainException.Conflict("CCCD_ALREADY_USED", "Số CCCD này đã đứng tên một hồ sơ Nhà vườn/Shop khác"); // BR-AUTH-02

        var profile = existing ?? new GardenProfile { OwnerId = userId, CreatedAt = Now, Slug = await UniqueSlugAsync(a.Name!, ct) };
        profile.Name = a.Name!.Trim();
        profile.Type = a.Type;
        profile.Description = a.Description?.Trim();
        profile.Address = a.Address.Trim();
        profile.ProvinceId = a.ProvinceId;
        profile.WardId = a.WardId;
        profile.Location = GeoJson.Point(GeoJson.Geographic(a.Lng, a.Lat));
        profile.OpeningHours = a.OpeningHours;
        profile.AllowVisit = a.AllowVisit;
        profile.PhotoMediaIds = photos.Select(p => p.Id).ToList();
        profile.Method = a.Method;
        profile.BusinessLicenseNo = a.BusinessLicenseNo?.Trim();
        profile.Kyc = new KycData
        {
            IdNumberHmac = hmac, IdNumberEnc = protector.Encrypt(a.IdNumber!), FullNameEnc = protector.Encrypt(a.FullName.Trim()),
            FullNameNormalized = VietnameseText.Normalize(a.FullName), DobEnc = protector.Encrypt(a.DateOfBirth.ToString("yyyy-MM-dd")),
            DocumentMediaIds = docs.Select(d => d.Id).ToList(),
        };
        profile.Bank = new BankAccount
        {
            BankCode = a.BankCode.Trim().ToUpperInvariant(), AccountNoEnc = protector.Encrypt(a.BankAccountNo!), AccountNoMasked = DataProtector.MaskTail(a.BankAccountNo!, 4),
            AccountName = a.BankAccountName.Trim().ToUpperInvariant(),
        };
        profile.Status = VerificationStatus.Submitted;
        profile.UpdatedAt = Now;
        await Gardens.ReplaceOneAsync(g => g.Id == profile.Id, profile, new ReplaceOptions { IsUpsert = true }, ct);
        await media.AttachAsync(profile.PhotoMediaIds.Concat(profile.Kyc.DocumentMediaIds), $"garden:{profile.Id}", ct);
        return profile;
    }

    public async Task<GardenProfile> UpdateAsync(string userId, GardenUpdate u, CancellationToken ct)
    {
        var g = await FindByOwnerAsync(userId, ct) ?? throw DomainException.NotFound("hồ sơ nhà vườn");
        var up = Builders<GardenProfile>.Update.Set(x => x.UpdatedAt, Now);
        if (u.Name is { } name) up = up.Set(x => x.Name, name.Trim());
        if (u.Description is not null) up = up.Set(x => x.Description, u.Description.Trim());
        if (u.OpeningHours is not null) up = up.Set(x => x.OpeningHours, u.OpeningHours);
        if (u.AllowVisit is { } av) up = up.Set(x => x.AllowVisit, av);
        if (u.PhotoMediaIds is { } photos) up = up.Set(x => x.PhotoMediaIds, (await media.GetOwnedAsync(photos, userId, ct)).Select(p => p.Id).ToList());
        if (u.CoverMediaId is { } cover) up = up.Set(x => x.CoverMediaId, (await media.GetOwnedAsync([cover], userId, ct))[0].Id);
        if (u.PinnedListingIds is { } pins)
        {
            if (pins.Count > 6) throw new DomainException("TOO_MANY_PINS", "Ghim tối đa 6 tin nổi bật");
            foreach (var id in pins) await listings.GetOwnedAsync(id, userId, ct);
            up = up.Set(x => x.PinnedListingIds, pins);
        }
        // BR-MAP-06: đổi vị trí/địa chỉ của hồ sơ đã xác minh thì phải xác minh lại.
        var moved = (u.Lat is not null || u.Lng is not null || u.Address is not null) && g.Status == VerificationStatus.Verified;
        if (u.Lat is { } lat && u.Lng is { } lng) up = up.Set(x => x.Location, GeoJson.Point(GeoJson.Geographic(lng, lat)));
        if (u.Address is not null) up = up.Set(x => x.Address, u.Address.Trim());
        if (u.WardId is not null) up = up.Set(x => x.WardId, u.WardId);
        if (moved) up = up.Set(x => x.Status, VerificationStatus.Submitted);
        await Gardens.UpdateOneAsync(x => x.Id == g.Id, up, cancellationToken: ct);
        if (moved) await SyncFlagsAsync(g.OwnerId, ct);
        return await GetAsync(g.Id, ct);
    }

    public async Task<GardenProfile> ReviewAsync(string id, ReviewRequest r, string reviewerId, CancellationToken ct)
    {
        var g = await GetAsync(id, ct);
        if (r.Decision != ReviewDecision.Approve && string.IsNullOrWhiteSpace(r.Note)) throw new DomainException("NOTE_REQUIRED", "Cần ghi chú lý do");
        var (from, to) = r.Decision switch
        {
            ReviewDecision.Approve => (new[] { VerificationStatus.Submitted }, VerificationStatus.Verified),
            ReviewDecision.NeedsInfo => (new[] { VerificationStatus.Submitted }, VerificationStatus.NeedsInfo),
            ReviewDecision.Reject => (new[] { VerificationStatus.Submitted }, VerificationStatus.Rejected),
            _ => (new[] { VerificationStatus.Verified, VerificationStatus.Submitted }, VerificationStatus.Revoked),
        };
        if (!from.Contains(g.Status)) throw DomainException.Conflict("INVALID_STATE", $"Không thể {r.Decision} hồ sơ đang ở trạng thái {g.Status}");
        var u = Builders<GardenProfile>.Update.Set(x => x.Status, to).Set(x => x.ReviewNote, r.Note).Set(x => x.ReviewedById, reviewerId).Set(x => x.UpdatedAt, Now);
        if (to == VerificationStatus.Verified) u = u.Set(x => x.VerifiedAt, Now);
        await Gardens.UpdateOneAsync(x => x.Id == id, u, cancellationToken: ct);
        await SyncFlagsAsync(g.OwnerId, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Mua / gia hạn gói bằng Xu. Giá gói lấy từ bảng giá đang hiệu lực, 1 Xu = 1.000đ.</summary>
    public async Task<GardenProfile> BuyPlanAsync(string userId, BuyPlanRequest req, CancellationToken ct)
    {
        var g = await FindByOwnerAsync(userId, ct) ?? throw DomainException.NotFound("hồ sơ nhà vườn");
        if (g.Status != VerificationStatus.Verified) throw DomainException.Forbidden("Hồ sơ phải được xác minh trước khi mua gói");
        var book = await pricing.GetActiveAsync(ct) ?? throw new DomainException("NO_ACTIVE_PRICE_BOOK", "Chưa có bảng giá");
        var plan = book.GardenPlans.FirstOrDefault(p => p.Months == req.Months && p.Enabled) ?? throw DomainException.NotFound("gói");
        var price = PlanPriceXu(plan.PriceVnd, g.IsFounding, book.GardenPlanRules.FounderDiscountPct);
        if (book.Version != req.ExpectedPriceBookVersion || price != req.ExpectedPriceXu)
            throw DomainException.Conflict("PRICE_CHANGED", "Giá gói đã thay đổi, vui lòng xác nhận lại", new { book.Version, priceXu = price });

        using var session = await client.StartSessionAsync(cancellationToken: ct);
        await session.WithTransactionAsync(async (s, token) =>
        {
            var (_, duplicate) = await wallets.DebitAsync(s, userId, price, $"plan:{userId}:{req.IdempotencyKey}", "gardenPlan", g.Id, $"Gói Nhà vườn {req.Months} tháng", token);
            if (duplicate) return true;
            var fresh = await Gardens.Find(s, x => x.Id == g.Id).FirstAsync(token);
            var start = fresh.Plan is { } cur && cur.EndAt > Now ? cur.EndAt : Now;
            var end = start.AddMonths(req.Months);
            await Gardens.UpdateOneAsync(s, x => x.Id == g.Id, Builders<GardenProfile>.Update.Set(x => x.Plan, new GardenPlan
            {
                Months = req.Months, StartAt = fresh.Plan?.EndAt > Now ? fresh.Plan.StartAt : Now, EndAt = end,
                GraceEndAt = end.AddDays(book.GardenPlanRules.GraceDays), AutoRenew = req.AutoRenew && book.GardenPlanRules.AutoRenewAllowed,
                LastPriceXu = price, PriceBookVersion = book.Version,
            }), cancellationToken: token);
            return true;
        }, cancellationToken: ct);
        await SyncFlagsAsync(userId, ct);
        return await GetAsync(g.Id, ct);
    }

    public static long PlanPriceXu(long priceVnd, bool founding, int founderDiscountPct) =>
        (long)Math.Ceiling(priceVnd / 1000m * (founding ? (100 - founderDiscountPct) / 100m : 1m));

    /// <summary>Ưu đãi Nhà vườn Sáng lập: tặng tháng miễn phí (tài liệu 04 §4.2).</summary>
    public async Task<GardenProfile> GrantPlanAsync(string id, GrantPlanRequest r, CancellationToken ct)
    {
        if (r.Months is < 1 or > 12) throw new DomainException("INVALID_MONTHS", "Tặng 1–12 tháng");
        var g = await GetAsync(id, ct);
        if (g.Status != VerificationStatus.Verified) throw DomainException.Conflict("INVALID_STATE", "Chỉ tặng gói cho hồ sơ đã xác minh");
        var book = await pricing.GetActiveAsync(ct);
        var start = g.Plan?.EndAt > Now ? g.Plan.EndAt : Now;
        var end = start.AddMonths(r.Months);
        await Gardens.UpdateOneAsync(x => x.Id == id, Builders<GardenProfile>.Update
            .Set(x => x.IsFounding, g.IsFounding || r.Founding)
            .Set(x => x.Plan, new GardenPlan { Months = r.Months, StartAt = g.Plan?.EndAt > Now ? g.Plan.StartAt : Now, EndAt = end,
                GraceEndAt = end.AddDays(book?.GardenPlanRules.GraceDays ?? 7), AutoRenew = g.Plan?.AutoRenew ?? false }), cancellationToken: ct);
        await SyncFlagsAsync(g.OwnerId, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Đồng bộ cờ trên tài khoản: đã xác minh (dùng escrow) và gói còn hạn (tick, gian hàng, pin).</summary>
    public async Task SyncFlagsAsync(string ownerId, CancellationToken ct)
    {
        var g = await FindByOwnerAsync(ownerId, ct);
        var verified = g?.Status == VerificationStatus.Verified;
        var active = g?.PlanActive(Now) == true;
        await users.Users.UpdateOneAsync(u => u.Id == ownerId, Builders<User>.Update
            .Set(u => u.Flags.HasVerifiedGarden, verified).Set(u => u.Flags.HasActivePlan, active), cancellationToken: ct);
        if (!verified)
            await listings.Listings.UpdateManyAsync(l => l.SellerId == ownerId && l.EscrowEnabled, Builders<Listing>.Update.Set(l => l.EscrowEnabled, false), cancellationToken: ct);
        await listings.RecomputeProSellerAsync(ownerId, ct);
    }

    /// <summary>Job: gói hết hạn → tự gia hạn bằng Xu nếu bật và đủ Xu, nếu không thì tắt cờ gói (BR-PKG-02..04).</summary>
    public async Task<int> ProcessPlanExpiryAsync(CancellationToken ct)
    {
        var now = Now;
        var ended = await Gardens.Find(g => g.Plan != null && g.Plan.EndAt <= now && g.Status == VerificationStatus.Verified)
            .Limit(500).ToListAsync(ct);
        var changed = 0;
        foreach (var g in ended)
        {
            if (g.Plan!.AutoRenew && g.Plan.EndAt > now.AddDays(-1))
            {
                try
                {
                    var book = await pricing.GetActiveAsync(ct);
                    var plan = book?.GardenPlans.FirstOrDefault(p => p.Months == g.Plan.Months && p.Enabled);
                    if (book is not null && plan is not null)
                    {
                        var price = PlanPriceXu(plan.PriceVnd, g.IsFounding, book.GardenPlanRules.FounderDiscountPct);
                        await BuyPlanAsync(g.OwnerId, new BuyPlanRequest(g.Plan.Months, book.Version, price, $"auto-renew:{g.Plan.EndAt:O}"), ct);
                        changed++;
                        continue;
                    }
                }
                catch (DomainException) { /* không đủ Xu: rơi xuống hết hạn */ }
            }
            var user = await users.Users.Find(u => u.Id == g.OwnerId).FirstOrDefaultAsync(ct);
            if (user?.Flags.HasActivePlan == true) { await SyncFlagsAsync(g.OwnerId, ct); changed++; }
        }
        return changed;
    }

    async Task<string> UniqueSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = VietnameseText.Slugify(name);
        if (baseSlug.Length == 0) baseSlug = "vuon";
        var slug = baseSlug;
        for (var i = 2; await Gardens.Find(g => g.Slug == slug).AnyAsync(ct); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }
}
