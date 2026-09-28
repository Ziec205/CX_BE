using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Community;
using ChamXanh.Api.Modules.Discovery;
using ChamXanh.Api.Modules.Escrow;
using ChamXanh.Api.Modules.Gardens;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Moderation;
using ChamXanh.Api.Modules.Notifications;
using ChamXanh.Api.Modules.PlantCare;
using ChamXanh.Api.Modules.Wallet;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Identity;

public record DeleteAccountRequest(string OtpCode, bool AcknowledgeLosses);
public record SanctionRequest(string Action, int? Days, int? Points, string Reason); // warn | restrict | lock | ban | unlock

/// <summary>Xóa tài khoản (BR-AUTH-05) và quản lý người dùng phía quản trị (UC-ADM-04).</summary>
public class AccountLifecycleService(
    IMongoDatabase db, TimeProvider clock, UserService users, OtpService otp, TokenService tokens, ListingService listings,
    EscrowService escrow, WalletService wallets, GardenService gardens, PlantCareService plants, NotificationService notifications,
    CommunityService community)
{
    DateTime Now => clock.GetUtcNow().UtcDateTime;

    static readonly OrderStatus[] Blocking =
    [
        OrderStatus.AwaitingSellerConfirm, OrderStatus.Paid, OrderStatus.Shipping, OrderStatus.Delivered, OrderStatus.Disputed,
        OrderStatus.AwaitingReturn, OrderStatus.Completed, OrderStatus.PartiallyRefunded,
    ];

    public async Task<object> CheckAsync(string userId, CancellationToken ct)
    {
        var blockers = new List<string>();
        var open = await escrow.Orders.CountDocumentsAsync(o => (o.BuyerId == userId || o.SellerId == userId) && Blocking.Contains(o.Status), cancellationToken: ct);
        if (open > 0) blockers.Add($"Còn {open} đơn đảm bảo đang mở, đang khiếu nại hoặc chưa quyết toán");
        var wallet = await wallets.GetAsync(userId, ct);
        var garden = await gardens.FindByOwnerAsync(userId, ct);
        var planDaysLeft = garden?.Plan is { } p && p.EndAt > Now ? (int)Math.Ceiling((p.EndAt - Now).TotalDays) : 0;
        return new
        {
            canDelete = blockers.Count == 0, blockers,
            losses = new { xu = wallet.Balance(Now), planDaysLeft },
        };
    }

    public async Task DeleteAsync(string userId, DeleteAccountRequest req, CancellationToken ct)
    {
        var user = await users.GetAsync(userId, ct);
        if (user.Status == UserStatus.Deleted) return;
        if (!req.AcknowledgeLosses) throw new DomainException("ACK_REQUIRED", "Bạn cần xác nhận đã hiểu Xu và thời gian gói còn lại sẽ bị hủy");
        var open = await escrow.Orders.CountDocumentsAsync(o => (o.BuyerId == userId || o.SellerId == userId) && Blocking.Contains(o.Status), cancellationToken: ct);
        if (open > 0) throw DomainException.Conflict("OPEN_ORDERS", "Còn đơn đảm bảo chưa hoàn tất, chưa thể xóa tài khoản");
        await otp.VerifyAsync(user.Phone, req.OtpCode, ct);

        // Đơn chưa thanh toán: hủy để trả lại số lượng.
        foreach (var o in await escrow.Orders.Find(o => (o.BuyerId == userId || o.SellerId == userId) && o.Status == OrderStatus.AwaitingPayment).ToListAsync(ct))
            await escrow.CancelAsync(userId, o.Id, new CancelRequest("Tài khoản đã xóa"), ct);

        await listings.Listings.UpdateManyAsync(l => l.SellerId == userId && l.Status != ListingStatus.Deleted,
            Builders<Listing>.Update.Set(l => l.Status, ListingStatus.Deleted).Set(l => l.DeletedAt, Now), cancellationToken: ct);
        await gardens.Gardens.UpdateOneAsync(g => g.OwnerId == userId && g.Plan != null && g.Plan.EndAt > Now, Builders<GardenProfile>.Update
            .Set(g => g.Plan!.EndAt, Now).Set(g => g.Plan!.GraceEndAt, Now).Set(g => g.Plan!.AutoRenew, false), cancellationToken: ct);
        await gardens.Gardens.UpdateOneAsync(g => g.OwnerId == userId, Builders<GardenProfile>.Update.Set(g => g.Bank, null), cancellationToken: ct);
        await community.Posts.UpdateManyAsync(p => p.AuthorId == userId, Builders<Post>.Update.Set(p => p.Status, PostStatus.Removed), cancellationToken: ct);
        await plants.Reminders.DeleteManyAsync(r => r.UserId == userId, ct);
        await plants.Plants.DeleteManyAsync(p => p.UserId == userId, ct);
        await notifications.Devices.DeleteManyAsync(d => d.UserId == userId, ct);
        await db.GetCollection<Favorite>("favorites").DeleteManyAsync(f => f.UserId == userId, ct);
        await db.GetCollection<Follow>("follows").DeleteManyAsync(f => f.FollowerId == userId || f.SellerId == userId, ct);
        await db.GetCollection<SavedSearch>("savedSearches").DeleteManyAsync(s => s.UserId == userId, ct);

        // Ẩn danh hóa hồ sơ; dữ liệu giao dịch/thuế giữ theo luật định (sổ cái Xu, đơn đảm bảo không xóa).
        await users.Users.UpdateOneAsync(u => u.Id == userId, Builders<User>.Update
            .Set(u => u.Status, UserStatus.Deleted).Set(u => u.Phone, $"deleted:{userId}").Set(u => u.DisplayName, "Người dùng đã xóa")
            .Set(u => u.FullName, null).Set(u => u.AvatarMediaId, null).Set(u => u.WardId, null).Set(u => u.HidePhone, true)
            .Set(u => u.Flags, new UserFlags()), cancellationToken: ct);
        await tokens.RevokeAllAsync(userId);
    }
}

public static class AccountLifecycleEndpoints
{
    public static void MapAccountLifecycle(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me/account").WithTags("Account").RequireAuthorization(Policies.Member);
        me.MapGet("/deletion-check", (ClaimsPrincipal p, AccountLifecycleService svc, CancellationToken ct) => svc.CheckAsync(p.UserId(), ct));
        // Gửi OTP về SĐT tài khoản trước khi xóa (dùng lại luồng OTP đăng nhập).
        me.MapPost("/deletion-otp", async (ClaimsPrincipal p, UserService users, OtpService otp, CancellationToken ct) =>
        {
            var u = await users.GetAsync(p.UserId(), ct);
            return new { devCode = await otp.RequestAsync(u.Phone, null, ct) };
        });
        me.MapPost("/delete", async (DeleteAccountRequest req, ClaimsPrincipal p, AccountLifecycleService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(p.UserId(), req, ct);
            return Results.NoContent();
        });

        // ---- UC-ADM-04: quản lý người dùng ----
        var admin = app.MapGroup("/api/admin/members").WithTags("Admin Members").RequireAuthorization(Policies.ForPerm(Perm.UserSanction));
        admin.MapGet("/", async (string? q, UserStatus? status, int? page, UserService users, CancellationToken ct) =>
        {
            var f = Builders<User>.Filter.Empty;
            if (status is { } s) f &= Builders<User>.Filter.Eq(u => u.Status, s);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                string? phone = null;
                try { phone = PhoneNumber.Normalize(term); } catch (DomainException) { }
                f &= phone is not null ? Builders<User>.Filter.Eq(u => u.Phone, phone)
                    : MongoDB.Bson.ObjectId.TryParse(term, out _) ? Builders<User>.Filter.Eq(u => u.Id, term)
                    : Builders<User>.Filter.Regex(u => u.DisplayName, new MongoDB.Bson.BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(term), "i"));
            }
            var pageNo = Math.Max(1, page ?? 1);
            var list = await users.Users.Find(f).SortByDescending(u => u.CreatedAt).Skip((pageNo - 1) * 50).Limit(50).ToListAsync(ct);
            return list.Select(u => new
            {
                u.Id, phone = DataProtector.MaskTail(u.Phone, 3), u.DisplayName, u.FullName, u.ProvinceId, u.Flags, u.Status,
                u.ActiveViolationPoints, u.PostingRestrictedUntil, u.LockedUntil, u.CreatedAt, u.LastSeenAt,
            });
        });
        admin.MapGet("/{id}", async (string id, UserService users, ModerationService mod, ListingService listings, EscrowService escrow, CancellationToken ct) =>
        {
            var u = await users.GetAsync(id, ct);
            var violations = await mod.Violations.Find(v => v.UserId == id).SortByDescending(v => v.CreatedAt).Limit(100).ToListAsync(ct);
            var listingCount = await listings.Listings.CountDocumentsAsync(l => l.SellerId == id && l.Status != ListingStatus.Deleted, cancellationToken: ct);
            var orders = await escrow.Orders.Find(o => o.BuyerId == id || o.SellerId == id).SortByDescending(o => o.CreatedAt).Limit(20).ToListAsync(ct);
            return new
            {
                user = new { u.Id, phone = DataProtector.MaskTail(u.Phone, 3), u.DisplayName, u.FullName, u.ProvinceId, u.Flags, u.Status, u.ActiveViolationPoints, u.PostingRestrictedUntil, u.LockedUntil, u.CreatedAt, u.LastSeenAt },
                violations, listingCount,
                orders = orders.Select(o => new { o.Id, o.Code, o.Status, o.Total, role = o.BuyerId == id ? "buyer" : "seller", o.CreatedAt }),
            };
        });
        admin.MapPost("/{id}/sanction", async (string id, SanctionRequest req, ClaimsPrincipal p, UserService users, ModerationService mod,
            TokenService tokens, NotificationService notifications, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new DomainException("REASON_REQUIRED", "Nhập lý do");
            var u = await users.GetAsync(id, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var days = Math.Clamp(req.Days ?? 7, 1, 365);
            var upd = Builders<User>.Update;
            string message;
            switch (req.Action)
            {
                case "warn":
                    if (req.Points is > 0) await mod.AddViolationAsync(id, "ADMIN_WARN", req.Points.Value, null, p.UserId(), ct);
                    message = "Tài khoản của bạn bị cảnh cáo";
                    break;
                case "restrict":
                    await users.Users.UpdateOneAsync(x => x.Id == id, upd.Set(x => x.Status, UserStatus.Restricted).Set(x => x.PostingRestrictedUntil, now.AddDays(days)), cancellationToken: ct);
                    message = $"Tài khoản bị hạn chế đăng tin {days} ngày";
                    break;
                case "lock":
                    await users.Users.UpdateOneAsync(x => x.Id == id, upd.Set(x => x.Status, UserStatus.Locked).Set(x => x.LockedUntil, now.AddDays(days)), cancellationToken: ct);
                    await tokens.RevokeAllAsync(id);
                    message = $"Tài khoản bị khóa {days} ngày";
                    break;
                case "ban":
                    if (!p.HasClaim(Claims.Perm, Perm.UserBanPermanent)) throw DomainException.Forbidden("Cần quyền khóa vĩnh viễn");
                    await users.Users.UpdateOneAsync(x => x.Id == id, upd.Set(x => x.Status, UserStatus.Banned), cancellationToken: ct);
                    await tokens.RevokeAllAsync(id);
                    message = "Tài khoản bị khóa vĩnh viễn";
                    break;
                case "unlock":
                    await users.Users.UpdateOneAsync(x => x.Id == id, upd.Set(x => x.Status, UserStatus.Active).Set(x => x.LockedUntil, null).Set(x => x.PostingRestrictedUntil, null), cancellationToken: ct);
                    message = "Tài khoản đã được mở lại";
                    break;
                default: throw new DomainException("INVALID_ACTION", "Hành động không hợp lệ");
            }
            await notifications.SendAsync(id, "sanction", message, req.Reason, "/tai-khoan", ct, u.Status == UserStatus.Deleted ? null : u.Phone);
            await audit.LogAsync(p, $"user.{req.Action}", "user", id, before: new { u.Status }, after: new { req.Action, days, req.Points }, reason: req.Reason, ct: ct);
            return Results.NoContent();
        });
    }
}
