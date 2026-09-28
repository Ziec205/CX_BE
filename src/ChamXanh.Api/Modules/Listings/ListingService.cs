using System.Security.Claims;
using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Moderation;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace ChamXanh.Api.Modules.Listings;

public record ListingInput(
    ListingType Type, string CategoryId, string? SpeciesId, string Title, string Description,
    long? Price = null, PriceMode PriceMode = PriceMode.Fixed, bool PriceNegotiable = false, long? PriceRefMin = null, long? PriceRefMax = null,
    long? BudgetMin = null, long? BudgetMax = null, DateTime? NeededBy = null, RentTerms? Rent = null, string? WantInExchange = null,
    int Quantity = 1, string? Unit = null, Dictionary<string, JsonElement>? Attributes = null,
    string? ProvinceId = null, string? WardId = null, double? Lat = null, double? Lng = null,
    List<string>? PickupOptions = null, bool EscrowEnabled = false, List<string>? MediaIds = null, string? VerificationMediaId = null,
    List<string>? CollectionIds = null);

public class ListingService(
    IMongoDatabase db, TimeProvider clock, ListingOptions options, UserService users, CatalogService catalog,
    MediaService media, ModerationService moderation)
{
    public IMongoCollection<Listing> Listings { get; } = db.GetCollection<Listing>("listings");
    static readonly string[] PickupValues = ["PICKUP", "SELLER_DELIVERY", "SELF_ARRANGED_CARRIER"];

    public async Task EnsureIndexesAsync()
    {
        var k = Builders<Listing>.IndexKeys;
        await Listings.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Listing>(k.Ascending(l => l.Status).Ascending(l => l.CategoryId).Descending(l => l.BumpedAt)),
            new CreateIndexModel<Listing>(k.Ascending(l => l.Status).Descending(l => l.BumpedAt)),
            new CreateIndexModel<Listing>(k.Ascending(l => l.Status).Ascending(l => l.PriorityUntil)),
            new CreateIndexModel<Listing>(k.Ascending(l => l.SellerId).Ascending(l => l.Status)),
            new CreateIndexModel<Listing>(k.Ascending(l => l.Status).Ascending(l => l.ExpiresAt)),
            new CreateIndexModel<Listing>(k.Ascending(l => l.SpeciesId).Ascending(l => l.Status)),
            new CreateIndexModel<Listing>(k.Geo2DSphere(l => l.Location)),
            new CreateIndexModel<Listing>(k.Ascending("media.pHash")),
        ]);
    }

    // ------------------------------------------------------------------ đọc

    public async Task<Listing> GetAsync(string id, CancellationToken ct = default) =>
        (ObjectId.TryParse(id, out _) ? await Listings.Find(l => l.Id == id && l.Status != ListingStatus.Deleted).FirstOrDefaultAsync(ct) : null)
        ?? throw DomainException.NotFound("tin đăng");

    public async Task<Listing> GetOwnedAsync(string id, string userId, CancellationToken ct)
    {
        var l = await GetAsync(id, ct);
        if (l.SellerId != userId) throw DomainException.Forbidden("Bạn không phải chủ tin này");
        return l;
    }

    // ------------------------------------------------------------------ tạo, gửi duyệt

    public async Task<Listing> CreateAsync(string userId, ListingInput input, bool submit, CancellationToken ct, string? proxyActor = null)
    {
        var user = await users.RequireActiveAsync(userId, ct);
        users.EnsureCanPost(user);
        var now = Now;

        // BR-LST-01: giới hạn tốc độ (công cụ đăng hộ của Admin không bị giới hạn — BR-LST-14).
        if (proxyActor is null)
        {
            var limit = user.Flags.HasActivePlan ? options.MaxNewPerHourGarden : options.MaxNewPerHour;
            var lastHour = await Listings.CountDocumentsAsync(l => l.SellerId == userId && l.CreatedAt > now.AddHours(-1), cancellationToken: ct);
            if (lastHour >= limit) throw DomainException.TooMany($"Bạn chỉ được đăng tối đa {limit} tin mỗi giờ");
        }

        var listing = new Listing { SellerId = userId, CreatedAt = now };
        await ApplyInputAsync(listing, input, user, ct);
        listing.UpdatedAt = now;
        await Listings.InsertOneAsync(listing, cancellationToken: ct);
        await media.AttachAsync(listing.Media.Select(m => m.MediaId).Append(listing.VerificationMediaId).OfType<string>(), listing.Id, ct);
        return submit ? await SubmitAsync(listing.Id, userId, ct) : listing;
    }

    public async Task<Listing> SubmitAsync(string id, string userId, CancellationToken ct)
    {
        var listing = await GetOwnedAsync(id, userId, ct);
        if (listing.Status is not (ListingStatus.Draft or ListingStatus.Rejected))
            throw DomainException.Conflict("INVALID_STATE", "Chỉ gửi đăng được tin nháp hoặc tin bị từ chối");
        var user = await users.RequireActiveAsync(userId, ct);
        users.EnsureCanPost(user);
        var category = await catalog.GetLeafForPostingAsync(listing.CategoryId, ct);

        await EnsureNewAccountHighValueLimitAsync(user, listing, ct);
        await EnsureNotDuplicateAsync(listing, listing.Media, ct);

        var risk = await EvaluateRiskAsync(listing, listing.Title, listing.Description, category, listing.SpeciesId, listing.Attributes, listing.Media, user, ct);
        return await RouteAsync(listing, risk, CaseTrigger.New, ct);
    }

    async Task<Listing> RouteAsync(Listing listing, RiskResult risk, CaseTrigger trigger, CancellationToken ct)
    {
        listing.RiskScore = risk.Score;
        switch (risk.Route)
        {
            case RiskRoute.AutoReject:
                await SetFieldsAsync(listing.Id, Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Rejected)
                    .Set(l => l.RejectReason, risk.RejectReason).Set(l => l.RiskScore, risk.Score), ct);
                var auto = await moderation.OpenCaseAsync(listing.Id, listing.SellerId, trigger, CaseQueue.Normal, risk.Score, risk.Signals, ct);
                await moderation.ResolveAsync(auto, Decision.Reject, "PROHIBITED", risk.RejectReason, "system", "Hệ thống", ct);
                break;
            case RiskRoute.AutoApprove:
                await PublishAsync(listing, ct);
                if (Random.Shared.NextDouble() < options.RandomAuditRate)
                    await moderation.OpenCaseAsync(listing.Id, listing.SellerId, CaseTrigger.RandomAudit, CaseQueue.Audit, risk.Score, risk.Signals, ct);
                break;
            default:
                await SetFieldsAsync(listing.Id, Builders<Listing>.Update.Set(l => l.Status, ListingStatus.PendingReview)
                    .Set(l => l.RiskScore, risk.Score).Set(l => l.RejectReason, null), ct);
                await moderation.OpenCaseAsync(listing.Id, listing.SellerId, trigger,
                    risk.Route == RiskRoute.ManualPriority ? CaseQueue.Priority : CaseQueue.Normal, risk.Score, risk.Signals, ct);
                break;
        }
        return await GetAsync(listing.Id, ct);
    }

    /// <summary>Hiển thị tin. bumpedAt/expiresAt chỉ đặt ở lần hiển thị đầu (BR-LST-15, BR-LST-02).</summary>
    async Task PublishAsync(Listing listing, CancellationToken ct)
    {
        var now = Now;
        var u = Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Active).Set(l => l.RejectReason, null).Set(l => l.UpdatedAt, now);
        if (listing.FirstPublishedAt is null)
            u = u.Set(l => l.FirstPublishedAt, now).Set(l => l.BumpedAt, now).Set(l => l.ExpiresAt, ListingRules.ComputeExpiry(now, options));
        await SetFieldsAsync(listing.Id, u, ct);
        await RecomputeProSellerAsync(listing.SellerId, ct);
    }

    // ------------------------------------------------------------------ sửa tin

    public async Task<Listing> UpdateAsync(string id, string userId, ListingInput input, CancellationToken ct)
    {
        var current = await GetOwnedAsync(id, userId, ct);
        var user = await users.RequireActiveAsync(userId, ct);
        if (input.Type != current.Type) throw new DomainException("TYPE_IMMUTABLE", "Không đổi được loại tin, hãy tạo tin mới");

        if (current.Status is ListingStatus.Draft or ListingStatus.Rejected)
        {
            await ApplyInputAsync(current, input, user, ct);
            current.UpdatedAt = Now;
            await Listings.ReplaceOneAsync(l => l.Id == id, current, cancellationToken: ct);
            await media.AttachAsync(current.Media.Select(m => m.MediaId), id, ct);
            return current;
        }
        if (current.Status is not (ListingStatus.Active or ListingStatus.Hidden or ListingStatus.SoldOut))
            throw DomainException.Conflict("INVALID_STATE", "Tin ở trạng thái này không sửa được");
        if (current.PendingRevision is not null)
            throw DomainException.Conflict("REVISION_PENDING", "Tin đang có bản sửa chờ duyệt");

        users.EnsureCanPost(user);
        var draft = new Listing { Id = current.Id, SellerId = userId, CreatedAt = current.CreatedAt };
        await ApplyInputAsync(draft, input, user, ct);

        // BR-LST-04: đổi danh mục cấp 1 hoặc đổi loài khác họ thì phải tạo tin mới (chống tái sử dụng tin đã đẩy).
        if (draft.CategoryRootId != current.CategoryRootId
            || (current.SpeciesFamily is not null && draft.SpeciesFamily is not null && draft.SpeciesFamily != current.SpeciesFamily))
            throw new DomainException("NEW_LISTING_REQUIRED", "Thay đổi này biến tin thành một món hàng khác, hãy tạo tin mới");

        var criticalChanged = draft.Title != current.Title || draft.CategoryId != current.CategoryId || draft.SpeciesId != current.SpeciesId
            || !draft.Media.Select(m => m.MediaId).SequenceEqual(current.Media.Select(m => m.MediaId));

        // Trường không trọng yếu: áp dụng ngay (BR-LST-03). Giá áp dụng ngay để thông báo giảm giá chạy được.
        var nonCritical = Builders<Listing>.Update
            .Set(l => l.Description, draft.Description).Set(l => l.Quantity, Math.Max(draft.Quantity, current.Reserved + current.Sold))
            .Set(l => l.Unit, draft.Unit).Set(l => l.Attributes, draft.Attributes).Set(l => l.PickupOptions, draft.PickupOptions)
            .Set(l => l.Location, draft.Location).Set(l => l.WardId, draft.WardId).Set(l => l.ProvinceId, draft.ProvinceId)
            .Set(l => l.EscrowEnabled, draft.EscrowEnabled).Set(l => l.PriceNegotiable, draft.PriceNegotiable)
            .Set(l => l.Rent, draft.Rent).Set(l => l.BudgetMin, draft.BudgetMin).Set(l => l.BudgetMax, draft.BudgetMax)
            .Set(l => l.NeededBy, draft.NeededBy).Set(l => l.WantInExchange, draft.WantInExchange).Set(l => l.CollectionIds, draft.CollectionIds)
            .Set(l => l.PriceMode, draft.PriceMode).Set(l => l.PriceRefMin, draft.PriceRefMin).Set(l => l.PriceRefMax, draft.PriceRefMax)
            .Set(l => l.UpdatedAt, Now).Set(l => l.RevisionRejectReason, null);

        var category = await catalog.GetLeafForPostingAsync(draft.CategoryId, ct);
        var median = await SpeciesMedianAsync(draft.SpeciesId, ct);
        var priceNeedsReview = draft.Price != current.Price && draft.Price is > 0 && median is > 0
            && Math.Abs(draft.Price.Value - median.Value) * 2 > median.Value;
        if (!priceNeedsReview) nonCritical = nonCritical.Set(l => l.Price, draft.Price).Set(l => l.SortPrice, draft.SortPrice);
        await SetFieldsAsync(id, nonCritical, ct);

        if (!criticalChanged && !priceNeedsReview) return await GetAsync(id, ct);

        await EnsureNotDuplicateAsync(draft, draft.Media.Where(m => current.Media.All(c => c.MediaId != m.MediaId)).ToList(), ct);
        var risk = await EvaluateRiskAsync(draft, draft.Title, draft.Description, category, draft.SpeciesId, draft.Attributes, draft.Media, user, ct);
        if (risk.Route == RiskRoute.AutoReject) throw new DomainException("EDIT_REJECTED", risk.RejectReason ?? "Nội dung sửa không hợp lệ");

        var revision = new ListingRevision
        {
            Title = draft.Title, Description = draft.Description, CategoryId = draft.CategoryId, SpeciesId = draft.SpeciesId,
            Media = draft.Media, Price = draft.Price, Attributes = draft.Attributes, SubmittedAt = Now,
        };
        await media.AttachAsync(draft.Media.Select(m => m.MediaId), id, ct);
        if (risk.Route == RiskRoute.AutoApprove)
        {
            await ApplyRevisionAsync(id, revision, ct);
            return await GetAsync(id, ct);
        }
        var mc = await moderation.OpenCaseAsync(id, userId, CaseTrigger.Edit,
            risk.Route == RiskRoute.ManualPriority ? CaseQueue.Priority : CaseQueue.Normal, risk.Score, risk.Signals, ct);
        revision.ModerationCaseId = mc.Id;
        await SetFieldsAsync(id, Builders<Listing>.Update.Set(l => l.PendingRevision, revision), ct);
        return await GetAsync(id, ct);
    }

    async Task ApplyRevisionAsync(string id, ListingRevision r, CancellationToken ct)
    {
        var species = r.SpeciesId is null or "khac" ? null : await catalog.GetSpeciesAsync(r.SpeciesId, ct);
        var current = await GetAsync(id, ct);
        await SetFieldsAsync(id, Builders<Listing>.Update
            .Set(l => l.Title, r.Title).Set(l => l.Description, r.Description).Set(l => l.CategoryId, r.CategoryId)
            .Set(l => l.SpeciesId, r.SpeciesId).Set(l => l.SpeciesFamily, species?.Family).Set(l => l.Media, r.Media)
            .Set(l => l.Price, r.Price ?? current.Price).Set(l => l.SortPrice, SortPriceOf(current, r.Price ?? current.Price))
            .Set(l => l.Attributes, r.Attributes).Set(l => l.PendingRevision, null)
            .Set(l => l.SearchText, BuildSearchText(r.Title, r.Description, species)).Set(l => l.UpdatedAt, Now), ct);
    }

    // ------------------------------------------------------------------ vòng đời do người bán thao tác

    public async Task<Listing> HideAsync(string id, string userId, CancellationToken ct) =>
        await TransitionAsync(id, userId, [ListingStatus.Active], ListingStatus.Hidden, ct);

    public async Task<Listing> UnhideAsync(string id, string userId, CancellationToken ct)
    {
        var l = await GetOwnedAsync(id, userId, ct);
        if (l.ExpiresAt < Now) throw new DomainException("EXPIRED", "Tin đã hết hạn, hãy gia hạn");
        return await TransitionAsync(id, userId, [ListingStatus.Hidden], ListingStatus.Active, ct);
    }

    public async Task<Listing> MarkSoldAsync(string id, string userId, CancellationToken ct) =>
        await TransitionAsync(id, userId, [ListingStatus.Active, ListingStatus.Hidden], ListingStatus.SoldOut, ct);

    /// <summary>Nhập thêm hàng: tin hết hàng quay lại hiển thị nếu còn hạn (sửa L13).</summary>
    public async Task<Listing> RestockAsync(string id, string userId, int quantity, CancellationToken ct)
    {
        var l = await GetOwnedAsync(id, userId, ct);
        if (quantity < 1 || quantity < l.Reserved) throw new DomainException("INVALID_QUANTITY", "Số lượng không hợp lệ");
        var u = Builders<Listing>.Update.Set(x => x.Quantity, l.Sold + quantity).Set(x => x.UpdatedAt, Now);
        if (l.Status == ListingStatus.SoldOut && l.ExpiresAt > Now) u = u.Set(x => x.Status, ListingStatus.Active);
        await SetFieldsAsync(id, u, ct);
        await RecomputeProSellerAsync(userId, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Gia hạn miễn phí: KHÔNG đổi bumpedAt (BR-LST-15, sửa L01).</summary>
    public async Task<Listing> RenewAsync(string id, string userId, CancellationToken ct)
    {
        var l = await GetOwnedAsync(id, userId, ct);
        var user = await users.RequireActiveAsync(userId, ct);
        users.EnsureCanPost(user);
        if (l.Status != ListingStatus.Expired) throw DomainException.Conflict("INVALID_STATE", "Chỉ gia hạn được tin đã hết hạn");
        var next = l.Available > 0 ? ListingStatus.Active : ListingStatus.SoldOut;
        await SetFieldsAsync(id, Builders<Listing>.Update.Set(x => x.Status, next)
            .Set(x => x.ExpiresAt, Now.AddDays(options.ExpiryDays)).Set(x => x.UpdatedAt, Now), ct);
        await RecomputeProSellerAsync(userId, ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(string id, string userId, CancellationToken ct)
    {
        var l = await GetOwnedAsync(id, userId, ct);
        if (l.Reserved > 0) throw DomainException.Conflict("HAS_OPEN_ORDERS", "Tin đang có đơn giao dịch đảm bảo chưa hoàn tất");
        await SetFieldsAsync(id, Builders<Listing>.Update.Set(x => x.Status, ListingStatus.Deleted).Set(x => x.DeletedAt, Now), ct);
        await moderation.CloseOpenCasesForListingAsync(id, "Người bán đã xóa tin", ct);
        await RecomputeProSellerAsync(userId, ct);
    }

    async Task<Listing> TransitionAsync(string id, string userId, ListingStatus[] from, ListingStatus to, CancellationToken ct)
    {
        var l = await GetOwnedAsync(id, userId, ct);
        if (!from.Contains(l.Status)) throw DomainException.Conflict("INVALID_STATE", $"Không thể chuyển tin từ trạng thái {l.Status} sang {to}");
        var res = await Listings.UpdateOneAsync(x => x.Id == id && x.Status == l.Status,
            Builders<Listing>.Update.Set(x => x.Status, to).Set(x => x.UpdatedAt, Now).Inc(x => x.Version, 1), cancellationToken: ct);
        if (res.ModifiedCount == 0) throw DomainException.Conflict("CONCURRENT_UPDATE", "Tin vừa được cập nhật, vui lòng thử lại");
        await RecomputeProSellerAsync(userId, ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------ kiểm duyệt

    public async Task<Listing> ApplyDecisionAsync(ModerationCase mc, Decision decision, string? reasonCode, string? note,
        ClaimsPrincipal actor, CancellationToken ct)
    {
        var listing = await GetAsync(mc.ListingId, ct);
        var reasonLabel = reasonCode is not null ? ReasonCodes.All[reasonCode].Label : null;
        var message = string.Join(": ", new[] { reasonLabel, note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var penalize = false;

        switch (mc.Trigger, decision)
        {
            case (CaseTrigger.New, Decision.Approve):
                await PublishAsync(listing, ct);
                break;
            case (CaseTrigger.New, Decision.Reject or Decision.RequestChanges):
                await SetFieldsAsync(listing.Id, Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Rejected).Set(l => l.RejectReason, message), ct);
                penalize = decision == Decision.Reject;
                break;
            case (CaseTrigger.Edit, Decision.Approve):
                if (listing.PendingRevision is { } rev) await ApplyRevisionAsync(listing.Id, rev, ct);
                break;
            case (CaseTrigger.Edit, Decision.Reject or Decision.RequestChanges):
                await SetFieldsAsync(listing.Id, Builders<Listing>.Update.Set(l => l.PendingRevision, null).Set(l => l.RevisionRejectReason, message), ct);
                penalize = decision == Decision.Reject;
                break;
            case (CaseTrigger.Report or CaseTrigger.RandomAudit, Decision.Dismiss or Decision.Approve):
                if (listing.Status == ListingStatus.TempHidden)
                    await SetFieldsAsync(listing.Id, Builders<Listing>.Update.Set(l => l.Status,
                        listing.ExpiresAt < Now ? ListingStatus.Expired : ListingStatus.Active), ct);
                await moderation.SetReportsStatusAsync(listing.Id, "Dismissed", ct);
                break;
            case (CaseTrigger.Report or CaseTrigger.RandomAudit, Decision.Remove):
                await SetFieldsAsync(listing.Id, Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Removed).Set(l => l.RejectReason, message), ct);
                await moderation.SetReportsStatusAsync(listing.Id, "Accepted", ct);
                penalize = true;
                break;
            case (CaseTrigger.Appeal, Decision.Approve):
                if (listing.Status == ListingStatus.Removed || listing.Status == ListingStatus.Rejected)
                {
                    if (listing.FirstPublishedAt is null) await PublishAsync(listing, ct);
                    else await SetFieldsAsync(listing.Id, Builders<Listing>.Update.Set(l => l.Status,
                        listing.ExpiresAt < Now ? ListingStatus.Expired : ListingStatus.Active).Set(l => l.RejectReason, null), ct);
                }
                break;
            case (CaseTrigger.Appeal, Decision.Reject or Decision.Dismiss):
                break;
            default:
                throw new DomainException("INVALID_DECISION", $"Quyết định {decision} không áp dụng cho hồ sơ {mc.Trigger}");
        }

        if (penalize && reasonCode is not null)
            await moderation.AddViolationAsync(listing.SellerId, reasonCode, ReasonCodes.All[reasonCode].Points, listing.Id, actor.UserId(), ct);
        await RecomputeProSellerAsync(listing.SellerId, ct);
        return await GetAsync(listing.Id, ct);
    }

    /// <summary>BR-MOD-05: người bị xử lý được khiếu nại một lần.</summary>
    public async Task<ModerationCase> AppealAsync(string id, string userId, string reason, CancellationToken ct)
    {
        var l = await GetOwnedAsync(id, userId, ct);
        if (l.Status is not (ListingStatus.Removed or ListingStatus.Rejected)) throw DomainException.Conflict("INVALID_STATE", "Chỉ khiếu nại được tin bị gỡ hoặc bị từ chối");
        if (l.AppealUsed) throw DomainException.Conflict("APPEAL_USED", "Tin này đã khiếu nại một lần");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("REASON_REQUIRED", "Vui lòng nêu lý do khiếu nại");
        var last = await moderation.Cases.Find(c => c.ListingId == id && c.Status == CaseStatus.Resolved).SortByDescending(c => c.DecidedAt).FirstOrDefaultAsync(ct);
        await SetFieldsAsync(id, Builders<Listing>.Update.Set(x => x.AppealUsed, true), ct);
        return await moderation.OpenCaseAsync(id, userId, CaseTrigger.Appeal, CaseQueue.Normal, l.RiskScore, [$"appeal:{reason.Trim()}"], ct, last?.DecidedById);
    }

    // ------------------------------------------------------------------ báo cáo

    public async Task ReportAsync(string id, string reporterId, ReportReason reason, string? note, CancellationToken ct)
    {
        var listing = await GetAsync(id, ct);
        if (listing.SellerId == reporterId) throw new DomainException("CANNOT_REPORT_OWN", "Không thể báo cáo tin của chính mình");
        if (listing.Status is not (ListingStatus.Active or ListingStatus.TempHidden)) throw DomainException.Conflict("INVALID_STATE", "Tin không còn hiển thị");
        await moderation.EnsureCanReportAsync(reporterId, ct);
        var reporter = await users.RequireActiveAsync(reporterId, ct);
        try
        {
            await moderation.Reports.InsertOneAsync(new ListingReport
            {
                ListingId = id, SellerId = listing.SellerId, ReporterId = reporterId, Reason = reason, Note = note?.Trim(),
                Weight = ModerationService.ReportWeight(reporter, Now), CreatedAt = Now,
            }, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DomainException.Conflict("ALREADY_REPORTED", "Bạn đã báo cáo tin này");
        }

        var (weight, reporters, escrowScam) = await moderation.ReportStatsAsync(id, ct);
        var shouldHide = escrowScam || (weight >= 3 && reporters >= 2);
        if (shouldHide && listing.Status == ListingStatus.Active)
            await SetFieldsAsync(id, Builders<Listing>.Update.Set(l => l.Status, ListingStatus.TempHidden), ct);
        await moderation.OpenCaseAsync(id, listing.SellerId, CaseTrigger.Report, shouldHide ? CaseQueue.Priority : CaseQueue.Normal,
            listing.RiskScore, [$"reports:{reporters}", $"weight:{weight:0.#}"], ct);
    }

    /// <summary>BR-LIB-04: loài bị đổi sang cấm/hạn chế → các tin đang hiển thị vào hàng chờ.</summary>
    public async Task HandleSpeciesFlagAsync(string speciesId, LegalFlag flag, CancellationToken ct)
    {
        var affected = await Listings.Find(l => l.SpeciesId == speciesId && l.Status == ListingStatus.Active).ToListAsync(ct);
        foreach (var l in affected)
        {
            if (flag == LegalFlag.Banned)
                await SetFieldsAsync(l.Id, Builders<Listing>.Update.Set(x => x.Status, ListingStatus.TempHidden), ct);
            await moderation.OpenCaseAsync(l.Id, l.SellerId, CaseTrigger.Report, CaseQueue.Priority, 100, [$"species_flag_changed:{flag}"], ct);
        }
    }

    // ------------------------------------------------------------------ job nền

    /// <summary>Hết hạn tin (A-01) và tin Cần mua quá hạn cần hàng (BR-RFQ-04).</summary>
    public async Task<long> ExpireDueAsync(CancellationToken ct)
    {
        var now = Now;
        var f = Builders<Listing>.Filter;
        var visible = f.In(l => l.Status, [ListingStatus.Active, ListingStatus.Hidden, ListingStatus.SoldOut]);
        var due = visible & (f.Lt(l => l.ExpiresAt, now) | (f.Eq(l => l.Type, ListingType.Buy) & f.Lt(l => l.NeededBy, now)));
        var sellers = await Listings.Distinct(l => l.SellerId, due, cancellationToken: ct).ToListAsync(ct);
        var res = await Listings.UpdateManyAsync(due, Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Expired), cancellationToken: ct);
        foreach (var s in sellers) await RecomputeProSellerAsync(s, ct);
        return res.ModifiedCount;
    }

    public async Task<List<string>> DeadListingIdsAsync(CancellationToken ct)
    {
        var cutoff = Now.AddDays(-30);
        return await Listings.Find(l => (l.Status == ListingStatus.Deleted && l.DeletedAt < cutoff)
            || (l.Status == ListingStatus.Rejected && l.UpdatedAt < cutoff)).Project(l => l.Id).Limit(1000).ToListAsync(ct);
    }

    // ------------------------------------------------------------------ nội bộ

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    Task SetFieldsAsync(string id, UpdateDefinition<Listing> update, CancellationToken ct) =>
        Listings.UpdateOneAsync(l => l.Id == id, update.Inc(l => l.Version, 1), cancellationToken: ct);

    public async Task RecomputeProSellerAsync(string sellerId, CancellationToken ct)
    {
        var user = await users.GetAsync(sellerId, ct);
        var count = await Listings.CountDocumentsAsync(l => l.SellerId == sellerId && l.Status == ListingStatus.Active
            && (l.Type == ListingType.Sell || l.Type == ListingType.Rent), cancellationToken: ct);
        var isPro = ListingRules.IsProSeller(count, user.Flags.HasActivePlan, options.ProSellerThreshold);
        if (isPro != user.Flags.IsProSeller) await users.SetFlagAsync(sellerId, u => u.Flags.IsProSeller, isPro, ct);
    }

    async Task ApplyInputAsync(Listing l, ListingInput input, User user, CancellationToken ct)
    {
        var errors = new List<RuleError>();
        var category = await catalog.GetLeafForPostingAsync(input.CategoryId, ct);

        Species? species = null;
        if (!string.IsNullOrWhiteSpace(input.SpeciesId) && input.SpeciesId != "khac")
        {
            species = await catalog.GetSpeciesAsync(input.SpeciesId, ct);
            if (species.LegalFlag == LegalFlag.Banned) throw new DomainException("BANNED_SPECIES", $"Loài \"{species.CommonName}\" bị cấm mua bán");
        }
        else if (category.IsLivePlant && string.IsNullOrWhiteSpace(input.SpeciesId) && input.Type != ListingType.Buy)
            errors.Add(new("speciesId", "Hãy chọn loài cây (hoặc \"Khác / không rõ\")"));

        errors.AddRange(ListingRules.ValidateText(input.Title, input.Description));
        errors.AddRange(ListingRules.ValidatePrice(input.Type, category, new(input.Price, input.PriceMode, input.PriceNegotiable,
            input.PriceRefMin, input.PriceRefMax, input.BudgetMin, input.BudgetMax, input.Rent, input.Quantity)));

        var (attrs, attrErrors) = input.Type == ListingType.Buy
            ? (AttributeValidator.Validate(category, input.Attributes) is var r ? (r.Values, r.Errors.Where(e => !e.Message.EndsWith("là bắt buộc")).ToList()) : default)
            : AttributeValidator.Validate(category, input.Attributes);
        errors.AddRange(attrErrors.Select(e => new RuleError($"attributes.{e.Key}", e.Message)));

        var mediaIds = input.MediaIds ?? [];
        var (minMedia, maxMedia) = ListingRules.MediaBounds(category, input.Type);
        if (mediaIds.Count < minMedia || mediaIds.Count > maxMedia) errors.Add(new("mediaIds", $"Cần {minMedia}–{maxMedia} ảnh"));
        if (mediaIds.Distinct().Count() != mediaIds.Count) errors.Add(new("mediaIds", "Có ảnh bị lặp"));

        var pickup = input.PickupOptions ?? ["PICKUP"];
        if (pickup.Count == 0 || pickup.Any(p => !PickupValues.Contains(p))) errors.Add(new("pickupOptions", $"Hình thức nhận hợp lệ: {string.Join(", ", PickupValues)}"));

        // Giao dịch đảm bảo: chỉ Nhà vườn/Shop đã xác minh, chỉ tin Bán (BR-ESC-01/02, A-07).
        if (input.EscrowEnabled && (!user.Flags.HasVerifiedGarden || input.Type != ListingType.Sell))
            errors.Add(new("escrowEnabled", "Chỉ Nhà vườn/Shop đã xác minh mới bật được Giao dịch đảm bảo cho tin Bán"));

        if (input.Type == ListingType.Buy && input.NeededBy is { } nb && (nb < Now || nb > Now.AddDays(options.BuyRequestMaxDays)))
            errors.Add(new("neededBy", $"Hạn cần hàng phải trong vòng {options.BuyRequestMaxDays} ngày tới"));

        if ((input.Lat is null) != (input.Lng is null) || input.Lat is < -90 or > 90 || input.Lng is < -180 or > 180)
            errors.Add(new("location", "Tọa độ không hợp lệ"));

        var provinceId = input.ProvinceId ?? user.ProvinceId;
        if (string.IsNullOrWhiteSpace(provinceId)) errors.Add(new("provinceId", "Hãy chọn tỉnh/thành"));

        if (errors.Count > 0) throw new DomainException("VALIDATION_FAILED", "Tin đăng chưa hợp lệ", details: errors);

        if (input.Type == ListingType.Give && l.Status is ListingStatus.Draft or ListingStatus.Rejected && l.FirstPublishedAt is null)
        {
            var activeGive = await Listings.CountDocumentsAsync(x => x.SellerId == user.Id && x.Type == ListingType.Give
                && x.Status == ListingStatus.Active && x.Id != l.Id, cancellationToken: ct);
            if (activeGive >= options.MaxActiveGive) throw new DomainException("GIVE_LIMIT", $"Tối đa {options.MaxActiveGive} tin Tặng đang hiển thị");
        }

        var mediaItems = await media.GetOwnedAsync(mediaIds, user.Id, ct);
        if (mediaItems.Any(m => m.Kind != MediaKind.ListingPhoto)) throw new DomainException("INVALID_MEDIA", "Chỉ dùng ảnh tin đăng");

        var effectivePrice = input.PriceMode == PriceMode.Negotiable ? input.PriceRefMax : input.Price;
        string? verificationId = null;
        if (input.Type == ListingType.Sell && effectivePrice >= options.VerificationPhotoThresholdVnd)
        {
            // BR-LST-10 (kể cả tin "Giá thỏa thuận" — sửa L10)
            if (string.IsNullOrWhiteSpace(input.VerificationMediaId))
                throw new DomainException("VERIFICATION_PHOTO_REQUIRED",
                    "Tin từ 20 triệu cần 1 ảnh xác minh: cây kèm giấy ghi tên tài khoản và ngày, hoặc video quay quanh cây");
            verificationId = (await media.GetOwnedAsync([input.VerificationMediaId], user.Id, ct))[0].Id;
        }

        // Tin của người bán cá nhân chỉ lưu vị trí làm tròn ~1km (BR-MAP-02).
        GeoJsonPoint<GeoJson2DGeographicCoordinates>? location = null;
        if (input.Lat is { } lat && input.Lng is { } lng)
        {
            if (!user.Flags.HasActivePlan) { lat = Math.Round(lat, 2); lng = Math.Round(lng, 2); }
            location = GeoJson.Point(GeoJson.Geographic(lng, lat));
        }

        l.Type = input.Type;
        l.CategoryId = category.Id;
        l.CategoryRootId = category.ParentId!;
        l.SpeciesId = input.Type == ListingType.Buy && string.IsNullOrWhiteSpace(input.SpeciesId) ? null : input.SpeciesId;
        l.SpeciesFamily = species?.Family;
        l.CollectionIds = input.CollectionIds ?? [];
        l.Title = input.Title.Trim();
        l.Description = input.Description.Trim();
        l.SearchText = BuildSearchText(l.Title, l.Description, species);
        l.PriceMode = input.Type == ListingType.Sell ? input.PriceMode : PriceMode.Fixed;
        l.Price = input.Type switch { ListingType.Sell when input.PriceMode == PriceMode.Fixed => input.Price, ListingType.Give => 0, _ => null };
        l.PriceNegotiable = input.PriceNegotiable;
        l.PriceRefMin = l.PriceMode == PriceMode.Negotiable ? input.PriceRefMin : null;
        l.PriceRefMax = l.PriceMode == PriceMode.Negotiable ? input.PriceRefMax : null;
        l.BudgetMin = input.Type == ListingType.Buy ? input.BudgetMin : null;
        l.BudgetMax = input.Type == ListingType.Buy ? input.BudgetMax : null;
        l.NeededBy = input.Type == ListingType.Buy ? input.NeededBy ?? Now.AddDays(options.BuyRequestMaxDays) : null;
        l.Rent = input.Type == ListingType.Rent ? input.Rent : null;
        l.WantInExchange = input.Type == ListingType.Give ? input.WantInExchange?.Trim() : null;
        l.Quantity = input.Quantity;
        l.Unit = string.IsNullOrWhiteSpace(input.Unit) ? "cây" : input.Unit.Trim();
        l.Attributes = attrs;
        l.ProvinceId = provinceId!;
        l.WardId = input.WardId;
        l.Location = location;
        l.PickupOptions = pickup.Distinct().ToList();
        l.EscrowEnabled = input.EscrowEnabled;
        l.Media = mediaItems.Select(m => new ListingMediaRef { MediaId = m.Id, PHash = m.PHash, CapturedInApp = m.CapturedInApp, CapturedAt = m.CapturedAt }).ToList();
        l.VerificationMediaId = verificationId;
        l.SortPrice = SortPriceOf(l);
    }

    static long SortPriceOf(Listing l) => l.Type switch
    {
        ListingType.Sell => (l.PriceMode == PriceMode.Negotiable ? l.PriceRefMin : l.Price) ?? 0,
        ListingType.Rent => l.Rent?.PricePerUnit ?? 0,
        ListingType.Buy => l.BudgetMax ?? l.BudgetMin ?? 0,
        _ => 0,
    };

    static long SortPriceOf(Listing current, long? newPrice) =>
        current.Type == ListingType.Sell && current.PriceMode == PriceMode.Fixed ? newPrice ?? 0 : SortPriceOf(current);

    static string BuildSearchText(string title, string description, Species? species) =>
        VietnameseText.Normalize(string.Join(' ', new[] { title, description, species?.CommonName, species?.ScientificName }
            .Concat(species?.Aliases ?? []).Where(s => !string.IsNullOrWhiteSpace(s))));

    async Task<long?> SpeciesMedianAsync(string? speciesId, CancellationToken ct)
    {
        if (speciesId is null or "khac") return null;
        var s = await catalog.Species.Find(x => x.Id == speciesId).FirstOrDefaultAsync(ct);
        var refs = s?.PriceRefs.Where(r => r.SampleSize >= 5).ToList();
        return refs is { Count: > 0 } ? refs.Max(r => r.MedianVnd) : null;
    }

    /// <summary>BR-AUTH-07: tài khoản cá nhân mới (&lt; 7 ngày) tối đa 5 tin giá cao đang hiển thị.</summary>
    async Task EnsureNewAccountHighValueLimitAsync(User user, Listing listing, CancellationToken ct)
    {
        if (user.Flags.HasVerifiedGarden || user.CreatedAt <= Now.AddDays(-options.NewAccountDays)) return;
        if (listing.EffectivePrice is not { } price || price < options.HighValuePriceVnd) return;
        var active = await Listings.CountDocumentsAsync(x => x.SellerId == user.Id && x.Id != listing.Id
            && (x.Status == ListingStatus.Active || x.Status == ListingStatus.PendingReview) && x.SortPrice >= options.HighValuePriceVnd, cancellationToken: ct);
        if (active >= options.NewAccountMaxHighValue)
            throw new DomainException("NEW_ACCOUNT_LIMIT", $"Tài khoản mới chỉ được đăng tối đa {options.NewAccountMaxHighValue} tin từ {options.HighValuePriceVnd:N0}đ trong {options.NewAccountDays} ngày đầu");
    }

    /// <summary>BR-LST-06: ảnh trùng với tin khác của cùng người bán trong 30 ngày, kể cả tin đã ẩn/xóa (sửa L01).</summary>
    async Task EnsureNotDuplicateAsync(Listing listing, IReadOnlyCollection<ListingMediaRef> newMedia, CancellationToken ct)
    {
        if (newMedia.Count == 0) return;
        var since = Now.AddDays(-options.DuplicateLookbackDays);
        var own = await Listings.Find(x => x.SellerId == listing.SellerId && x.Id != listing.Id && x.UpdatedAt > since
                && x.Status != ListingStatus.Draft)
            .Project(x => new { x.Id, x.Media }).ToListAsync(ct);
        foreach (var other in own)
            if (other.Media.Any(o => newMedia.Any(n => ImageProcessor.HammingDistance((ulong)o.PHash, (ulong)n.PHash) <= options.DuplicateMaxHamming)))
                throw new DomainException("DUPLICATE_LISTING", "Ảnh trùng với một tin khác của bạn. Hãy gia hạn hoặc hiện lại tin cũ thay vì đăng lại",
                    details: new { existingListingId = other.Id });
    }

    async Task<RiskResult> EvaluateRiskAsync(Listing listing, string title, string description, Category category, string? speciesId,
        Dictionary<string, object> attributes, List<ListingMediaRef> mediaRefs, User user, CancellationToken ct)
    {
        var species = speciesId is null or "khac" ? null : await catalog.Species.Find(s => s.Id == speciesId).FirstOrDefaultAsync(ct);
        var cfg = await moderation.GetConfigAsync(ct);
        var ctx = new RiskContext(title, description, category, species, attributes,
            listing.EffectivePrice, await SpeciesMedianAsync(speciesId, ct),
            IsNewAccount: user.CreatedAt > Now.AddDays(-options.NewAccountDays),
            IsIndividual: !user.Flags.HasVerifiedGarden,
            IsTrustedGarden: user.Flags.HasActivePlan && user.ActiveViolationPoints == 0 && user.CreatedAt < Now.AddDays(-90),
            RecentRejections: await moderation.RecentRejectionsAsync(user.Id, ct),
            HasForeignPhoto: await HasForeignPhotoAsync(user.Id, mediaRefs, ct),
            options.HighValuePriceVnd, options.ManualReviewPriceVnd);
        return RiskScorer.Evaluate(ctx, cfg, options.AutoApproveBelow, options.PriorityQueueFrom);
    }

    /// <summary>BR-LST-07: ảnh trùng với tin của người bán khác → nghi ảnh mạng.</summary>
    async Task<bool> HasForeignPhotoAsync(string sellerId, List<ListingMediaRef> mediaRefs, CancellationToken ct)
    {
        foreach (var m in mediaRefs)
        {
            // pHash giống hệt (khoảng cách 0) tra bằng index; ảnh bị nén lại vẫn thường cho cùng dHash.
            if (await Listings.Find(x => x.SellerId != sellerId && x.Status != ListingStatus.Draft && x.Media.Any(o => o.PHash == m.PHash)).AnyAsync(ct))
                return true;
        }
        return false;
    }
}

public class ListingSpeciesFlagHandler(ListingService listings) : ISpeciesLegalFlagChanged
{
    public Task HandleAsync(string speciesId, LegalFlag newFlag, CancellationToken ct) => listings.HandleSpeciesFlagAsync(speciesId, newFlag, ct);
}
