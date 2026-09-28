using ChamXanh.Api.Common;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Pricing;

public class PricingException(string code, string message, object? details = null)
    : DomainException(code, message, StatusFor(code), details)
{
    static int StatusFor(string code) => code switch
    {
        "NOT_FOUND" or "SOURCE_NOT_FOUND" or "NO_ACTIVE_PRICE_BOOK" => StatusCodes.Status404NotFound,
        "PRICE_CHANGED" => StatusCodes.Status409Conflict,
        "SELF_APPROVAL" => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status422UnprocessableEntity,
    };
}

public class PricingService(IMongoDatabase db, TimeProvider clock)
{
    readonly IMongoCollection<PriceBook> _books = db.GetCollection<PriceBook>("priceBooks");
    public static readonly TimeSpan GardenPriceIncreaseNotice = TimeSpan.FromDays(7);

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _books.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<PriceBook>(Builders<PriceBook>.IndexKeys.Ascending(b => b.Status).Descending(b => b.EffectiveFrom)),
            new CreateIndexModel<PriceBook>(Builders<PriceBook>.IndexKeys.Ascending(b => b.Version), new CreateIndexOptions { Unique = true }),
            // Luôn chỉ có 1 bản đang hiệu lực (08 §5)
            new CreateIndexModel<PriceBook>(Builders<PriceBook>.IndexKeys.Ascending(b => b.Status),
                new CreateIndexOptions<PriceBook>
                {
                    Unique = true, Name = "one_active",
                    PartialFilterExpression = Builders<PriceBook>.Filter.Eq(b => b.Status, PriceBookStatus.Active),
                }),
        ], ct);
    }

    public Task<PriceBook?> GetActiveAsync(CancellationToken ct = default) =>
        _books.Find(b => b.Status == PriceBookStatus.Active).FirstOrDefaultAsync(ct)!;

    public Task<PriceBook?> GetAsync(string id, CancellationToken ct = default) =>
        _books.Find(b => b.Id == id).FirstOrDefaultAsync(ct)!;

    public Task<List<PriceBook>> ListAsync(CancellationToken ct = default) =>
        _books.Find(FilterDefinition<PriceBook>.Empty).SortByDescending(b => b.Version).ToListAsync(ct);

    public async Task<PriceBook> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        var existing = await GetActiveAsync(ct);
        if (existing is not null) return existing;
        var seed = PriceBookSeed.Create(clock.GetUtcNow().UtcDateTime);
        await _books.InsertOneAsync(seed, cancellationToken: ct);
        return seed;
    }

    /// <summary>BR-PRC-01/10: tạo nháp bằng cách nhân bản một phiên bản có sẵn (mặc định bản đang hiệu lực).</summary>
    public async Task<PriceBook> CreateDraftAsync(string actor, int? sourceVersion, CancellationToken ct = default)
    {
        var source = sourceVersion is int v
            ? await _books.Find(b => b.Version == v).FirstOrDefaultAsync(ct)
            : await GetActiveAsync(ct);
        if (source is null) throw new PricingException("SOURCE_NOT_FOUND", "Không tìm thấy phiên bản nguồn");

        var maxVersion = await _books.Find(FilterDefinition<PriceBook>.Empty).SortByDescending(b => b.Version)
            .Project(b => b.Version).FirstOrDefaultAsync(ct);
        var draft = Clone(source);
        draft.Id = $"pb_{maxVersion + 1}";
        draft.Version = maxVersion + 1;
        draft.Status = PriceBookStatus.Draft;
        draft.EffectiveFrom = draft.EffectiveTo = null;
        draft.CreatedBy = actor;
        draft.CreatedAt = clock.GetUtcNow().UtcDateTime;
        draft.ApprovedBy = null; draft.ApprovedAt = null; draft.RejectReason = null;
        draft.ChangeNote = sourceVersion is null ? null : $"Khôi phục từ v{sourceVersion}";
        await _books.InsertOneAsync(draft, cancellationToken: ct);
        return draft;
    }

    public async Task<PriceBook> UpdateDraftAsync(string id, PriceBook incoming, CancellationToken ct = default)
    {
        var current = await Require(id, ct);
        if (current.Status is not (PriceBookStatus.Draft or PriceBookStatus.Rejected))
            throw new PricingException("NOT_EDITABLE", "Chỉ sửa được bản nháp hoặc bản bị từ chối");

        incoming.Id = current.Id;
        incoming.Version = current.Version;
        incoming.Status = PriceBookStatus.Draft;
        incoming.CreatedBy = current.CreatedBy;
        incoming.CreatedAt = current.CreatedAt;
        incoming.EffectiveFrom = incoming.EffectiveTo = null;
        incoming.ApprovedBy = null; incoming.ApprovedAt = null;
        await _books.ReplaceOneAsync(b => b.Id == id, incoming, cancellationToken: ct);
        return incoming;
    }

    public async Task<ValidationResult> ValidateAsync(string id, CancellationToken ct = default) =>
        PriceBookValidator.Validate(await Require(id, ct), await GetActiveAsync(ct));

    public async Task<PriceBook> SubmitAsync(string id, CancellationToken ct = default)
    {
        var book = await Require(id, ct);
        if (book.Status is not (PriceBookStatus.Draft or PriceBookStatus.Rejected))
            throw new PricingException("INVALID_STATE", "Chỉ gửi duyệt được bản nháp");
        var result = PriceBookValidator.Validate(book, await GetActiveAsync(ct));
        if (!result.IsValid) throw new PricingException("VALIDATION_FAILED", "Bảng giá còn lỗi", result);
        await SetStatus(id, PriceBookStatus.PendingApproval, ct);
        book.Status = PriceBookStatus.PendingApproval;
        return book;
    }

    /// <summary>BR-PRC-03 maker-checker, BR-PRC-04 lên lịch, BR-PRC-07 báo trước khi tăng giá gói.</summary>
    public async Task<PriceBook> ApproveAsync(string id, string approver, DateTime? effectiveFrom, bool warningsAcknowledged, CancellationToken ct = default)
    {
        var book = await Require(id, ct);
        if (book.Status != PriceBookStatus.PendingApproval)
            throw new PricingException("INVALID_STATE", "Bảng giá chưa được gửi duyệt");
        if (string.Equals(book.CreatedBy, approver, StringComparison.OrdinalIgnoreCase))
            throw new PricingException("SELF_APPROVAL", "Người soạn không được tự duyệt (maker-checker)");

        var active = await GetActiveAsync(ct);
        var result = PriceBookValidator.Validate(book, active);
        if (!result.IsValid) throw new PricingException("VALIDATION_FAILED", "Bảng giá còn lỗi", result);
        if (result.Warnings.Count > 0 && !warningsAcknowledged)
            throw new PricingException("WARNINGS_NOT_ACKNOWLEDGED", "Người duyệt phải xác nhận đã xem cảnh báo", result);

        var now = clock.GetUtcNow().UtcDateTime;
        var start = effectiveFrom ?? now;
        if (start < now) start = now;
        if (active is not null && PriceBookValidator.IncreasesGardenPlanPrice(book, active) && start < now + GardenPriceIncreaseNotice)
            throw new PricingException("NOTICE_REQUIRED", "Tăng giá gói Nhà vườn phải có hiệu lực sau ít nhất 7 ngày kể từ lúc duyệt");

        await _books.UpdateOneAsync(b => b.Id == id, Builders<PriceBook>.Update
            .Set(b => b.Status, PriceBookStatus.Scheduled)
            .Set(b => b.EffectiveFrom, start)
            .Set(b => b.ApprovedBy, approver)
            .Set(b => b.ApprovedAt, now), cancellationToken: ct);

        await ActivateDueAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<PriceBook> RejectAsync(string id, string approver, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new PricingException("REASON_REQUIRED", "Phải nhập lý do từ chối");
        var book = await Require(id, ct);
        if (book.Status != PriceBookStatus.PendingApproval) throw new PricingException("INVALID_STATE", "Bảng giá chưa được gửi duyệt");
        await _books.UpdateOneAsync(b => b.Id == id, Builders<PriceBook>.Update
            .Set(b => b.Status, PriceBookStatus.Rejected).Set(b => b.RejectReason, reason).Set(b => b.ApprovedBy, approver), cancellationToken: ct);
        return (await GetAsync(id, ct))!;
    }

    /// <summary>Chuyển bản đã lên lịch sang hiệu lực khi đến giờ. Chạy định kỳ mỗi phút.</summary>
    public async Task<bool> ActivateDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await _books.Find(b => b.Status == PriceBookStatus.Scheduled && b.EffectiveFrom <= now)
            .SortByDescending(b => b.EffectiveFrom).FirstOrDefaultAsync(ct);
        if (due is null) return false;

        // Hết hiệu lực bản cũ trước, rồi mới bật bản mới, để partial unique index "one_active" không bị vi phạm.
        await _books.UpdateManyAsync(b => b.Status == PriceBookStatus.Active, Builders<PriceBook>.Update
            .Set(b => b.Status, PriceBookStatus.Expired).Set(b => b.EffectiveTo, now), cancellationToken: ct);
        await SetStatus(due.Id, PriceBookStatus.Active, ct);
        return true;
    }

    public async Task<PriceQuote> QuoteAsync(string serviceCode, int? days, QuoteContext ctx, CancellationToken ct = default)
    {
        var active = await GetActiveAsync(ct) ?? throw new PricingException("NO_ACTIVE_PRICE_BOOK", "Chưa có bảng giá hiệu lực");
        return PriceCalculator.Quote(active, serviceCode, days, ctx)
            ?? throw new PricingException("SERVICE_UNAVAILABLE", "Dịch vụ không khả dụng cho danh mục này");
    }

    /// <summary>BR-PRC-06: nếu giá đã đổi so với lúc người dùng xem thì từ chối, trả giá mới.</summary>
    public async Task<PriceQuote> ConfirmQuoteAsync(string serviceCode, int? days, QuoteContext ctx, int expectedVersion, long expectedPrice, CancellationToken ct = default)
    {
        var quote = await QuoteAsync(serviceCode, days, ctx, ct);
        if (quote.PriceBookVersion != expectedVersion || quote.FinalPrice != expectedPrice)
            throw new PricingException("PRICE_CHANGED", "Giá đã thay đổi, vui lòng xác nhận lại", quote);
        return quote;
    }

    async Task<PriceBook> Require(string id, CancellationToken ct) =>
        await GetAsync(id, ct) ?? throw new PricingException("NOT_FOUND", "Không tìm thấy bảng giá");

    Task SetStatus(string id, PriceBookStatus status, CancellationToken ct) =>
        _books.UpdateOneAsync(b => b.Id == id, Builders<PriceBook>.Update.Set(b => b.Status, status), cancellationToken: ct);

    static PriceBook Clone(PriceBook source) =>
        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<PriceBook>(MongoDB.Bson.BsonExtensionMethods.ToBson(source));
}

public class PriceBookActivationWorker(IServiceScopeFactory scopes, ILogger<PriceBookActivationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<PricingService>().ActivateDueAsync(stoppingToken))
                    logger.LogInformation("Đã kích hoạt phiên bản bảng giá mới");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi khi kích hoạt bảng giá");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
