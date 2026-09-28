using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;

namespace ChamXanh.Api.Modules.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Catalog");

        g.MapGet("/categories", async (CatalogService svc, CancellationToken ct) =>
        {
            var all = await svc.ListCategoriesAsync(ct);
            return all.Where(c => c.Level == 1).Select(root => new
            {
                root.Id, root.Name, root.Order,
                children = all.Where(c => c.ParentId == root.Id).Select(c => new { c.Id, c.Name, c.IsLivePlant, c.AllowNegotiablePrice, c.Order }),
            });
        });
        g.MapGet("/categories/{id}", (string id, CatalogService svc, CancellationToken ct) => svc.GetCategoryAsync(id, ct));

        g.MapGet("/species", (string? q, string? categoryId, int? limit, CatalogService svc, CancellationToken ct) =>
            svc.SearchSpeciesAsync(q, categoryId, limit ?? 20, ct));
        g.MapGet("/species/{id}", (string id, CatalogService svc, CancellationToken ct) => svc.GetSpeciesAsync(id, ct));

        var admin = app.MapGroup("/api/admin/catalog").WithTags("Admin Catalog").RequireAuthorization(Policies.ForPerm(Perm.CatalogManage));
        admin.MapPut("/categories/{id}", async (string id, Category body, ClaimsPrincipal p, CatalogService svc, AuditService audit, CancellationToken ct) =>
        {
            body.Id = id;
            var saved = await svc.UpsertCategoryAsync(body, ct);
            await audit.LogAsync(p, "catalog.upsert_category", "category", id, after: saved, ct: ct);
            return saved;
        });
        admin.MapPut("/species/{id}", async (string id, Species body, ClaimsPrincipal p, CatalogService svc, AuditService audit, CancellationToken ct) =>
        {
            body.Id = id;
            var saved = await svc.UpsertSpeciesAsync(body, p, ct);
            await audit.LogAsync(p, "catalog.upsert_species", "species", id, after: new { saved.CommonName, saved.LegalFlag }, ct: ct);
            return saved;
        });
    }
}
