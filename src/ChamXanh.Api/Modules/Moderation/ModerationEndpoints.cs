using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Listings;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Moderation;

public record DecideRequest(Decision Decision, string? ReasonCode, string? Note);

public static class ModerationEndpoints
{
    public static void MapModeration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/moderation").WithTags("Admin Moderation").RequireAuthorization(Policies.ForPerm(Perm.ListingModerate));

        g.MapGet("/reasons", () => ReasonCodes.All.Select(kv => new { code = kv.Key, kv.Value.Label, kv.Value.Points }));

        // Hàng chờ: ưu tiên trước, rồi theo hạn SLA (UC-ADM-02).
        g.MapGet("/cases", async (CaseQueue? queue, CaseTrigger? trigger, int? limit, ModerationService mod, ListingService listings, CancellationToken ct) =>
        {
            var f = Builders<ModerationCase>.Filter.Eq(c => c.Status, CaseStatus.Open);
            if (queue is { } q) f &= Builders<ModerationCase>.Filter.Eq(c => c.Queue, q);
            if (trigger is { } t) f &= Builders<ModerationCase>.Filter.Eq(c => c.Trigger, t);
            var cases = await mod.Cases.Find(f).ToListAsync(ct);
            var ordered = cases.OrderBy(c => c.Queue == CaseQueue.Priority ? 0 : c.Queue == CaseQueue.Normal ? 1 : 2).ThenBy(c => c.SlaDueAt)
                .Take(Math.Clamp(limit ?? 50, 1, 200)).ToList();
            var ids = ordered.Select(c => c.ListingId).ToList();
            var ls = await listings.Listings.Find(l => ids.Contains(l.Id)).Project(l => new { l.Id, l.Title, l.Status, l.CategoryId, l.SortPrice }).ToListAsync(ct);
            return ordered.Select(c => new { @case = c, listing = ls.FirstOrDefault(l => l.Id == c.ListingId) });
        });

        g.MapGet("/cases/{id}", async (string id, ModerationService mod, ListingService listings, CancellationToken ct) =>
        {
            var c = await mod.Cases.Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("hồ sơ");
            var listing = await listings.GetAsync(c.ListingId, ct);
            var history = await mod.Cases.Find(x => x.SellerId == c.SellerId && x.Status == CaseStatus.Resolved).SortByDescending(x => x.DecidedAt).Limit(20).ToListAsync(ct);
            var reports = await mod.Reports.Find(r => r.ListingId == c.ListingId).ToListAsync(ct);
            var points = await mod.ActivePointsAsync(c.SellerId, ct);
            return new { @case = c, listing, sellerHistory = history, reports, sellerActivePoints = points };
        });

        g.MapPost("/cases/{id}/decide", async (string id, DecideRequest req, ClaimsPrincipal p, ModerationService mod, ListingService listings,
            AuditService audit, CancellationToken ct) =>
        {
            var c = await mod.GetOpenCaseAsync(id, ct);
            if (req.ReasonCode is not null) ReasonCodes.EnsureValid(req.ReasonCode);
            if (req.Decision is Decision.Reject or Decision.Remove or Decision.RequestChanges && req.ReasonCode is null)
                throw new DomainException("REASON_REQUIRED", "Phải chọn lý do chuẩn khi từ chối/gỡ tin (BR-MOD-06)");
            // BR-MOD-05: người xét khiếu nại phải khác người xử lý lần đầu.
            if (c.Trigger == CaseTrigger.Appeal && c.OriginalDeciderId == p.UserId())
                throw DomainException.Forbidden("Người xét khiếu nại phải khác người đã xử lý lần đầu");

            var listing = await listings.ApplyDecisionAsync(c, req.Decision, req.ReasonCode, req.Note, p, ct);
            await mod.ResolveAsync(c, req.Decision, req.ReasonCode, req.Note, p.UserId(), p.ActorName(), ct);
            await audit.LogAsync(p, $"moderation.{req.Decision.ToString().ToLowerInvariant()}", "listing", c.ListingId,
                after: new { listing.Status, c.Trigger }, reason: req.ReasonCode, ct: ct);
            return new { listing.Id, listing.Status };
        });

        var cfg = app.MapGroup("/api/admin/moderation/config").WithTags("Admin Moderation").RequireAuthorization(Policies.ForPerm(Perm.ModerationConfig));
        cfg.MapGet("/", (ModerationService mod, CancellationToken ct) => mod.GetConfigAsync(ct));
        cfg.MapPut("/", async (ModerationConfig body, ClaimsPrincipal p, ModerationService mod, AuditService audit, CancellationToken ct) =>
        {
            var before = await mod.GetConfigAsync(ct);
            body.BannedTerms = body.BannedTerms.Select(VietnameseText.Normalize).Where(t => t.Length > 0).Distinct().ToList();
            body.NegationPrefixes = body.NegationPrefixes.Select(VietnameseText.Normalize).Where(t => t.Length > 0).Distinct().ToList();
            body.RiskyTerms = body.RiskyTerms.ToDictionary(kv => VietnameseText.Normalize(kv.Key), kv => Math.Clamp(kv.Value, 0, 100));
            await mod.SaveConfigAsync(body, ct);
            await audit.LogAsync(p, "moderation.update_config", "moderationConfig", "default", before, body, ct: ct);
            return body;
        });
    }
}
