using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;

namespace ChamXanh.Api.Modules.Pricing;

public record ApproveRequest(DateTime? EffectiveFrom, bool WarningsAcknowledged);
public record RejectRequest(string Reason);
public record CreateDraftRequest(int? SourceVersion);

public static class PricingEndpoints
{
    public static void MapPricing(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/pricing").WithTags("Pricing");

        pub.MapGet("/current", async (PricingService svc, CancellationToken ct) =>
            await svc.GetActiveAsync(ct) is { } book
                ? Results.Ok(new
                {
                    book.Version, book.EffectiveFrom, book.ListingServices, book.GardenPlans,
                    TopUpPackages = book.TopUpPackages.Where(p => p.Enabled),
                    EscrowFee = new { book.EscrowFee.Pct, book.EscrowFee.MinVnd, book.EscrowFee.MaxVnd },
                })
                : Results.NotFound());

        pub.MapGet("/quote", async (string service, int? days, string categoryId, string? collectionIds, PricingService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var ctx = new QuoteContext(categoryId, SplitIds(collectionIds), clock.GetUtcNow().UtcDateTime);
            return Results.Ok(await svc.QuoteAsync(service, days, ctx, ct));
        });

        var admin = app.MapGroup("/api/admin/price-books").WithTags("Admin Pricing");
        var view = Policies.ForPerm(Perm.PricingView);
        var edit = Policies.ForPerm(Perm.PricingEdit);
        var approve = Policies.ForPerm(Perm.PricingApprove);

        admin.MapGet("/", (PricingService svc, CancellationToken ct) => svc.ListAsync(ct)).RequireAuthorization(view);
        admin.MapGet("/{id}", async (string id, PricingService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound()).RequireAuthorization(view);

        admin.MapPost("/", async (CreateDraftRequest req, ClaimsPrincipal p, PricingService svc, AuditService audit, CancellationToken ct) =>
        {
            var draft = await svc.CreateDraftAsync(p.ActorName(), req.SourceVersion, ct);
            await audit.LogAsync(p, "pricing.create_draft", "priceBook", draft.Id, reason: draft.ChangeNote, ct: ct);
            return Results.Ok(draft);
        }).RequireAuthorization(edit);

        admin.MapPut("/{id}", async (string id, PriceBook body, ClaimsPrincipal p, PricingService svc, AuditService audit, CancellationToken ct) =>
        {
            var before = await svc.GetAsync(id, ct);
            var after = await svc.UpdateDraftAsync(id, body, ct);
            await audit.LogAsync(p, "pricing.update_draft", "priceBook", id, before, after, ct: ct);
            return Results.Ok(after);
        }).RequireAuthorization(edit);

        admin.MapPost("/{id}/validate", async (string id, PricingService svc, CancellationToken ct) =>
            Results.Ok(await svc.ValidateAsync(id, ct))).RequireAuthorization(view);

        admin.MapPost("/{id}/submit", async (string id, ClaimsPrincipal p, PricingService svc, AuditService audit, CancellationToken ct) =>
        {
            var book = await svc.SubmitAsync(id, ct);
            await audit.LogAsync(p, "pricing.submit", "priceBook", id, ct: ct);
            return Results.Ok(book);
        }).RequireAuthorization(edit);

        admin.MapPost("/{id}/approve", async (string id, ApproveRequest req, ClaimsPrincipal p, PricingService svc, AuditService audit, CancellationToken ct) =>
        {
            var book = await svc.ApproveAsync(id, p.ActorName(), req.EffectiveFrom, req.WarningsAcknowledged, ct);
            await audit.LogAsync(p, "pricing.approve", "priceBook", id, after: new { book.Status, book.EffectiveFrom }, ct: ct);
            return Results.Ok(book);
        }).RequireAuthorization(approve);

        admin.MapPost("/{id}/reject", async (string id, RejectRequest req, ClaimsPrincipal p, PricingService svc, AuditService audit, CancellationToken ct) =>
        {
            var book = await svc.RejectAsync(id, p.ActorName(), req.Reason, ct);
            await audit.LogAsync(p, "pricing.reject", "priceBook", id, reason: req.Reason, ct: ct);
            return Results.Ok(book);
        }).RequireAuthorization(approve);

        admin.MapGet("/preview", async (string categoryId, string? collectionIds, DateTime? at, PricingService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var book = await svc.GetActiveAsync(ct);
            if (book is null) return Results.NotFound();
            var ctx = new QuoteContext(categoryId, SplitIds(collectionIds), at ?? clock.GetUtcNow().UtcDateTime);
            var quotes = book.ListingServices.Where(s => s.Enabled)
                .Select(s => PriceCalculator.Quote(book, s.Code, s.Days, ctx)).Where(q => q is not null);
            return Results.Ok(quotes);
        }).RequireAuthorization(view);
    }

    static string[] SplitIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
