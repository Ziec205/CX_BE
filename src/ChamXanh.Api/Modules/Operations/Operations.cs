using System.Globalization;
using System.Security.Claims;
using System.Text;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Community;
using ChamXanh.Api.Modules.Escrow;
using ChamXanh.Api.Modules.Gardens;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Moderation;
using ChamXanh.Api.Modules.Notifications;
using ChamXanh.Api.Modules.Wallet;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Operations;

// Vận hành: Dashboard (UC-ADM-01), Báo cáo (UC-ADM-14), Hỗ trợ khách hàng (05 §6), Chuyên trang mùa vụ & banner (05 §4, UC-ADM-12).

public enum TicketStatus { Open, WaitingCustomer, Resolved, Closed }
public enum TicketTopic { Account, Listing, Order, Payment, Report, Other }

public class TicketMessage
{
    public string AuthorId { get; set; } = default!;
    public bool FromStaff { get; set; }
    public string Text { get; set; } = default!;
    public List<string> MediaIds { get; set; } = [];
    public DateTime At { get; set; }
}

public class SupportTicket
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public TicketTopic Topic { get; set; }
    public string Subject { get; set; } = default!;
    public string? ListingId { get; set; }
    public string? OrderId { get; set; }
    public string? ConversationId { get; set; }
    [BsonRepresentation(BsonType.String)] public TicketStatus Status { get; set; }
    /// <summary>SLA: ticket thường 24h, ticket gắn đơn đảm bảo 4h.</summary>
    public DateTime DueAt { get; set; }
    public string? AssigneeId { get; set; }
    public List<TicketMessage> Messages { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Campaign
{
    [BsonId] public string Id { get; set; } = default!; // slug
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string? BannerMediaId { get; set; }
    public string? BannerLink { get; set; }
    public List<string> CategoryIds { get; set; } = [];
    public List<string> SpeciesIds { get; set; } = [];
    public List<string> CollectionIds { get; set; } = [];
    /// <summary>Vị trí trên trang chủ: nhỏ hơn hiển thị trước.</summary>
    public int HomeOrder { get; set; }
    public bool Enabled { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public record TicketRequest(TicketTopic Topic, string Subject, string Message, string? ListingId, string? OrderId, string? ConversationId, List<string>? MediaIds);
public record TicketReply(string Message, List<string>? MediaIds);
public record TicketUpdate(TicketStatus? Status, string? AssigneeId);

public static class OperationsEndpoints
{
    public static async Task EnsureIndexesAsync(IMongoDatabase db)
    {
        await db.GetCollection<SupportTicket>("supportTickets").Indexes.CreateManyAsync(
        [
            new CreateIndexModel<SupportTicket>(Builders<SupportTicket>.IndexKeys.Ascending(t => t.UserId).Descending(t => t.CreatedAt)),
            new CreateIndexModel<SupportTicket>(Builders<SupportTicket>.IndexKeys.Ascending(t => t.Status).Ascending(t => t.DueAt)),
        ]);
    }

    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        MapSupport(app);
        MapCampaigns(app);
        MapDashboard(app);
    }

    // ---------- Hỗ trợ khách hàng ----------

    static void MapSupport(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/support/tickets").WithTags("Support").RequireAuthorization(Policies.Member);
        g.MapPost("/", async (TicketRequest req, ClaimsPrincipal p, IMongoDatabase db, MediaService media, UserService users, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await users.GetAsync(userId, ct);
            var subject = req.Subject?.Trim() ?? "";
            var text = req.Message?.Trim() ?? "";
            if (subject.Length is < 5 or > 150 || text.Length is < 10 or > 5000) throw new DomainException("INVALID_TICKET", "Tiêu đề 5–150 ký tự, nội dung 10–5000 ký tự");
            var tickets = db.GetCollection<SupportTicket>("supportTickets");
            var now = clock.GetUtcNow().UtcDateTime;
            if (await tickets.CountDocumentsAsync(t => t.UserId == userId && t.CreatedAt > now.AddDays(-1), cancellationToken: ct) >= 10)
                throw DomainException.TooMany("Bạn gửi quá nhiều yêu cầu hôm nay");
            var mediaIds = (req.MediaIds ?? []).Take(6).ToList();
            await media.GetOwnedAsync(mediaIds, userId, ct);
            var t = new SupportTicket
            {
                UserId = userId, Topic = req.Topic, Subject = subject, ListingId = req.ListingId, OrderId = req.OrderId, ConversationId = req.ConversationId,
                Status = TicketStatus.Open, DueAt = now.AddHours(req.OrderId is null ? 24 : 4), CreatedAt = now, UpdatedAt = now,
                Messages = [new TicketMessage { AuthorId = userId, Text = text, MediaIds = mediaIds, At = now }],
            };
            await tickets.InsertOneAsync(t, cancellationToken: ct);
            await media.AttachAsync(mediaIds, $"ticket:{t.Id}", ct);
            return t;
        });
        g.MapGet("/", async (ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await db.GetCollection<SupportTicket>("supportTickets").Find(t => t.UserId == userId).SortByDescending(t => t.UpdatedAt).Limit(50).ToListAsync(ct);
        });
        g.MapGet("/{id}", async (string id, ClaimsPrincipal p, IMongoDatabase db, CancellationToken ct) =>
        {
            var userId = p.UserId();
            return await db.GetCollection<SupportTicket>("supportTickets").Find(t => t.Id == id && t.UserId == userId).FirstOrDefaultAsync(ct)
                   ?? throw DomainException.NotFound("yêu cầu hỗ trợ");
        });
        g.MapPost("/{id}/reply", async (string id, TicketReply req, ClaimsPrincipal p, IMongoDatabase db, MediaService media, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var text = req.Message?.Trim() ?? "";
            if (text.Length is < 1 or > 5000) throw new DomainException("INVALID_MESSAGE", "Nội dung 1–5000 ký tự");
            var mediaIds = (req.MediaIds ?? []).Take(6).ToList();
            await media.GetOwnedAsync(mediaIds, userId, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var res = await db.GetCollection<SupportTicket>("supportTickets").UpdateOneAsync(t => t.Id == id && t.UserId == userId && t.Status != TicketStatus.Closed,
                Builders<SupportTicket>.Update.Push(t => t.Messages, new TicketMessage { AuthorId = userId, Text = text, MediaIds = mediaIds, At = now })
                    .Set(t => t.Status, TicketStatus.Open).Set(t => t.UpdatedAt, now), cancellationToken: ct);
            if (res.ModifiedCount == 0) throw DomainException.NotFound("yêu cầu hỗ trợ");
            await media.AttachAsync(mediaIds, $"ticket:{id}", ct);
            return Results.NoContent();
        });

        var admin = app.MapGroup("/api/admin/support/tickets").WithTags("Admin Support").RequireAuthorization(Policies.ForPerm(Perm.DisputeResolve));
        admin.MapGet("/", async (TicketStatus? status, IMongoDatabase db, CancellationToken ct) =>
        {
            var f = status is { } s ? Builders<SupportTicket>.Filter.Eq(t => t.Status, s)
                : Builders<SupportTicket>.Filter.In(t => t.Status, [TicketStatus.Open, TicketStatus.WaitingCustomer]);
            return await db.GetCollection<SupportTicket>("supportTickets").Find(f).SortBy(t => t.DueAt).Limit(200).ToListAsync(ct);
        });
        admin.MapGet("/{id}", async (string id, IMongoDatabase db, CancellationToken ct) =>
            await db.GetCollection<SupportTicket>("supportTickets").Find(t => t.Id == id).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("yêu cầu hỗ trợ"));
        admin.MapPost("/{id}/reply", async (string id, TicketReply req, ClaimsPrincipal p, IMongoDatabase db, NotificationService notifications, TimeProvider clock, CancellationToken ct) =>
        {
            var text = req.Message?.Trim() ?? "";
            if (text.Length is < 1 or > 5000) throw new DomainException("INVALID_MESSAGE", "Nội dung 1–5000 ký tự");
            var now = clock.GetUtcNow().UtcDateTime;
            var t = await db.GetCollection<SupportTicket>("supportTickets").FindOneAndUpdateAsync<SupportTicket>(x => x.Id == id,
                Builders<SupportTicket>.Update.Push(x => x.Messages, new TicketMessage { AuthorId = p.UserId(), FromStaff = true, Text = text, At = now })
                    .Set(x => x.Status, TicketStatus.WaitingCustomer).Set(x => x.UpdatedAt, now).Set(x => x.AssigneeId, p.UserId()),
                cancellationToken: ct) ?? throw DomainException.NotFound("yêu cầu hỗ trợ");
            await notifications.SendAsync(t.UserId, "account.support", "CSKH đã trả lời yêu cầu của bạn", t.Subject, $"/ho-tro/{t.Id}", ct);
            return Results.NoContent();
        });
        admin.MapPost("/{id}", async (string id, TicketUpdate req, ClaimsPrincipal p, IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
        {
            var u = Builders<SupportTicket>.Update.Set(x => x.UpdatedAt, clock.GetUtcNow().UtcDateTime);
            if (req.Status is { } s) u = u.Set(x => x.Status, s);
            if (req.AssigneeId is not null) u = u.Set(x => x.AssigneeId, req.AssigneeId);
            await db.GetCollection<SupportTicket>("supportTickets").UpdateOneAsync(x => x.Id == id, u, cancellationToken: ct);
            return Results.NoContent();
        });
    }

    // ---------- Chuyên trang mùa vụ & banner ----------

    static void MapCampaigns(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/campaigns/active", async (IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var list = await db.GetCollection<Campaign>("campaigns").Find(c => c.Enabled && c.StartAt <= now && c.EndAt > now).SortBy(c => c.HomeOrder).ToListAsync(ct);
            return list.Select(c => new { c.Id, c.Name, c.Description, c.EndAt, c.BannerLink, bannerUrl = c.BannerMediaId is null ? null : $"/media/{c.BannerMediaId}/full.webp", c.CategoryIds, c.SpeciesIds, c.CollectionIds });
        }).WithTags("Campaigns");
        app.MapGet("/api/campaigns/{id}", async (string id, IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return await db.GetCollection<Campaign>("campaigns").Find(c => c.Id == id && c.Enabled && c.StartAt <= now && c.EndAt > now).FirstOrDefaultAsync(ct)
                   ?? throw DomainException.NotFound("chuyên trang");
        }).WithTags("Campaigns");

        var admin = app.MapGroup("/api/admin/campaigns").WithTags("Admin Campaigns").RequireAuthorization(Policies.ForPerm(Perm.ContentManage));
        admin.MapGet("/", async (IMongoDatabase db, CancellationToken ct) =>
            await db.GetCollection<Campaign>("campaigns").Find(_ => true).SortByDescending(c => c.StartAt).ToListAsync(ct));
        admin.MapPut("/{id}", async (string id, Campaign body, ClaimsPrincipal p, IMongoDatabase db, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            body.Id = VietnameseText.Slugify(id);
            if (string.IsNullOrWhiteSpace(body.Name) || body.EndAt <= body.StartAt) throw new DomainException("INVALID_CAMPAIGN", "Cần tên và thời gian kết thúc sau thời gian bắt đầu");
            body.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.GetCollection<Campaign>("campaigns").ReplaceOneAsync(c => c.Id == body.Id, body, new ReplaceOptions { IsUpsert = true }, ct);
            if (body.BannerMediaId is not null)
                await db.GetCollection<MediaItem>("media").UpdateOneAsync(m => m.Id == body.BannerMediaId, Builders<MediaItem>.Update.Set(m => m.AttachedTo, $"campaign:{body.Id}"), cancellationToken: ct);
            await audit.LogAsync(p, "campaign.upsert", "campaign", body.Id, after: new { body.Name, body.StartAt, body.EndAt, body.Enabled }, ct: ct);
            return body;
        });
        admin.MapDelete("/{id}", async (string id, ClaimsPrincipal p, IMongoDatabase db, AuditService audit, CancellationToken ct) =>
        {
            await db.GetCollection<Campaign>("campaigns").DeleteOneAsync(c => c.Id == id, ct);
            await audit.LogAsync(p, "campaign.delete", "campaign", id, ct: ct);
            return Results.NoContent();
        });
    }

    // ---------- Dashboard & báo cáo ----------

    static void MapDashboard(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/reports").WithTags("Admin Reports").RequireAuthorization(Policies.ForPerm(Perm.ReportsView));

        admin.MapGet("/dashboard", async (IMongoDatabase db, ListingService listings, UserService users, ModerationService mod, EscrowService escrow,
            CommunityService community, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var day = now.AddDays(-1);
            var week = now.AddDays(-7);
            var ledger = db.GetCollection<BsonDocument>("xuLedger");
            var topUps = db.GetCollection<BsonDocument>("topUps");
            var paidTopUps = await topUps.Aggregate().Match(new BsonDocument { { "status", "Paid" }, { "paidAt", new BsonDocument("$gte", week) } })
                .Group(new BsonDocument { { "_id", BsonNull.Value }, { "vnd", new BsonDocument("$sum", "$snapshot.priceVnd") }, { "count", new BsonDocument("$sum", 1) } })
                .FirstOrDefaultAsync(ct);
            var gmv = await escrow.Orders.Aggregate().Match(o => o.PaidAt >= week && o.Status != OrderStatus.Cancelled)
                .Group(o => 1, g => new { total = g.Sum(o => o.Total), count = g.Count() }).FirstOrDefaultAsync(ct);
            var casesOpen = await db.GetCollection<BsonDocument>("moderationCases").CountDocumentsAsync(new BsonDocument("status", "Open"), cancellationToken: ct);
            return new
            {
                listings = new
                {
                    active = await listings.Listings.CountDocumentsAsync(l => l.Status == ListingStatus.Active, cancellationToken: ct),
                    newToday = await listings.Listings.CountDocumentsAsync(l => l.CreatedAt >= day, cancellationToken: ct),
                    pendingReview = await listings.Listings.CountDocumentsAsync(l => l.Status == ListingStatus.PendingReview, cancellationToken: ct),
                },
                moderationCasesOpen = casesOpen,
                users = new
                {
                    total = await users.Users.CountDocumentsAsync(u => u.Status != UserStatus.Deleted, cancellationToken: ct),
                    newToday = await users.Users.CountDocumentsAsync(u => u.CreatedAt >= day, cancellationToken: ct),
                    newWeek = await users.Users.CountDocumentsAsync(u => u.CreatedAt >= week, cancellationToken: ct),
                    verifiedGardens = await users.Users.CountDocumentsAsync(u => u.Flags.HasVerifiedGarden, cancellationToken: ct),
                },
                revenue7d = new { topUpVnd = paidTopUps?["vnd"].ToInt64() ?? 0, topUpCount = paidTopUps?["count"].ToInt32() ?? 0 },
                escrow7d = new { gmv = gmv?.total ?? 0, orders = gmv?.count ?? 0 },
                disputesOpen = await escrow.Orders.CountDocumentsAsync(o => o.Status == OrderStatus.Disputed, cancellationToken: ct),
                payoutsPending = await escrow.Orders.CountDocumentsAsync(o => (o.Status == OrderStatus.Completed || o.Status == OrderStatus.PartiallyRefunded) && o.PayoutEligibleAt <= now, cancellationToken: ct),
                ticketsOverdue = await db.GetCollection<SupportTicket>("supportTickets").CountDocumentsAsync(t => t.Status == TicketStatus.Open && t.DueAt < now, cancellationToken: ct),
                communityPostsWeek = await community.Posts.CountDocumentsAsync(p => p.CreatedAt >= week, cancellationToken: ct),
            };
        });

        // Thanh khoản theo danh mục × tỉnh: tỉ lệ tin có ít nhất 1 kết nối (chat) trong 30 ngày.
        admin.MapGet("/liquidity", async (IMongoDatabase db, ListingService listings, TimeProvider clock, CancellationToken ct) =>
        {
            var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
            var posted = await listings.Listings.Aggregate().Match(l => l.FirstPublishedAt >= since)
                .Group(l => new { l.CategoryRootId, l.ProvinceId }, g => new { g.Key, count = g.Count(), ids = g.Select(x => x.Id) }).ToListAsync(ct);
            var contacted = (await db.GetCollection<Messaging.Conversation>("conversations").Find(c => c.CreatedAt >= since && c.FirstReplyAt != null)
                .Project(c => c.ListingId).ToListAsync(ct)).ToHashSet();
            return posted.Select(p => new
            {
                category = p.Key.CategoryRootId, province = p.Key.ProvinceId, listings = p.count,
                liquidity = p.count == 0 ? 0 : Math.Round(p.ids.Count(contacted.Contains) / (double)p.count, 3),
            }).OrderByDescending(x => x.listings);
        });

        // Xuất CSV (UC-ADM-14): đơn đảm bảo, nạp Xu, người bán — phục vụ báo cáo Bộ Công Thương và đối soát.
        admin.MapGet("/export/{kind}", async (string kind, DateTime? from, DateTime? to, IMongoDatabase db, EscrowService escrow, ClaimsPrincipal p,
            AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var end = to ?? clock.GetUtcNow().UtcDateTime;
            var start = from ?? end.AddDays(-30);
            var sb = new StringBuilder();
            static string Csv(object? v) => v is null ? "" : "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)!.Replace("\"", "\"\"") + "\"";
            switch (kind)
            {
                case "escrow-orders":
                    sb.AppendLine("code,status,buyerId,sellerId,itemAmount,shippingFee,fee,total,refund,payout,createdAt,paidAt,completedAt,settledAt");
                    foreach (var o in await escrow.Orders.Find(o => o.CreatedAt >= start && o.CreatedAt < end).SortBy(o => o.CreatedAt).ToListAsync(ct))
                        sb.AppendLine(string.Join(',', new object?[] { o.Code, o.Status, o.BuyerId, o.SellerId, o.ItemAmount, o.ShippingFee, o.Fee, o.Total, o.RefundAmount, o.PayoutAmount, o.CreatedAt.ToString("o"), o.PaidAt?.ToString("o"), o.CompletedAt?.ToString("o"), o.SettledAt?.ToString("o") }.Select(Csv)));
                    break;
                case "topups":
                    sb.AppendLine("id,userId,status,priceVnd,xu,gatewayRef,createdAt,paidAt");
                    foreach (var t in await db.GetCollection<BsonDocument>("topUps").Find(new BsonDocument("createdAt", new BsonDocument { { "$gte", start }, { "$lt", end } })).ToListAsync(ct))
                        sb.AppendLine(string.Join(',', new object?[] { t["_id"], t.GetValue("userId", ""), t.GetValue("status", ""), t["snapshot"].AsBsonDocument.GetValue("priceVnd", 0), t["snapshot"].AsBsonDocument.GetValue("xu", 0), t.GetValue("gatewayRef", BsonNull.Value), t.GetValue("createdAt", BsonNull.Value), t.GetValue("paidAt", BsonNull.Value) }.Select(v => Csv(v is BsonNull ? null : v))));
                    break;
                case "sellers":
                    // Doanh thu người bán qua Giao dịch đảm bảo (phục vụ khấu trừ thuế, L6).
                    sb.AppendLine("sellerId,orders,gross,fees,payout");
                    var settled = await escrow.Orders.Find(o => o.SettledAt >= start && o.SettledAt < end).ToListAsync(ct);
                    foreach (var g in settled.GroupBy(o => o.SellerId))
                        sb.AppendLine(string.Join(',', new object?[] { g.Key, g.Count(), g.Sum(o => o.ItemAmount + o.ShippingFee - o.RefundAmount), g.Sum(o => o.ItemAmount + o.ShippingFee - o.RefundAmount - (o.PayoutAmount ?? 0)), g.Sum(o => o.PayoutAmount ?? 0) }.Select(Csv)));
                    break;
                default: throw DomainException.NotFound("loại báo cáo");
            }
            await audit.LogAsync(p, "report.export", "report", kind, after: new { start, end }, ct: ct);
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"{kind}-{start:yyyyMMdd}-{end:yyyyMMdd}.csv");
        });
    }
}
