using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Ai;

public static class AssistantEndpoints
{
    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        // Phải đăng nhập: lượt AI tính theo tài khoản.
        var g = app.MapGroup("/api/ai").WithTags("AI Assistant").RequireAuthorization(Policies.Member);

        g.MapGet("/quota", async (ClaimsPrincipal p, AiQuotaService quota, IAiModel model, CancellationToken ct) =>
            new { quota = AssistantService.QuotaDto(await quota.GetAsync(p.UserId(), ct)), simulated = model.IsSimulated });

        g.MapGet("/conversations", async (ClaimsPrincipal p, AssistantService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var list = await svc.Conversations.Find(c => c.UserId == userId).SortByDescending(c => c.UpdatedAt).Limit(100).ToListAsync(ct);
            return list.Select(AssistantService.ConversationDto);
        });
        g.MapGet("/conversations/{id}", async (string id, ClaimsPrincipal p, AssistantService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var conv = (ObjectId.TryParse(id, out _) ? await svc.Conversations.Find(c => c.Id == id && c.UserId == userId).FirstOrDefaultAsync(ct) : null)
                ?? throw DomainException.NotFound("cuộc trò chuyện");
            var messages = await svc.Messages.Find(m => m.ConversationId == id).SortBy(m => m.CreatedAt).Limit(400).ToListAsync(ct);
            return new { conversation = AssistantService.ConversationDto(conv), messages = messages.Select(AssistantService.MessageDto) };
        });
        g.MapDelete("/conversations/{id}", async (string id, ClaimsPrincipal p, AssistantService svc, CancellationToken ct) =>
        {
            await svc.DeleteConversationAsync(p.UserId(), id, ct);
            return Results.NoContent();
        });

        g.MapPost("/chat", (ChatRequest req, ClaimsPrincipal p, AssistantService svc, CancellationToken ct) => svc.ChatAsync(p.UserId(), req, ct));
        g.MapPost("/compare", (CompareRequest req, ClaimsPrincipal p, AssistantService svc, CancellationToken ct) => svc.CompareAsync(p.UserId(), req, ct));
        g.MapPost("/listings/{id}/market-check", (string id, ClaimsPrincipal p, AssistantService svc, CancellationToken ct) =>
            svc.MarketCheckAsync(p.UserId(), id, ct));
        g.MapPost("/listing-draft", (ListingDraftRequest req, ClaimsPrincipal p, AssistantService svc, CancellationToken ct) =>
            svc.DraftListingAsync(p.UserId(), req, ct));
        g.MapPost("/price-suggest", (PriceSuggestRequest req, ClaimsPrincipal p, AssistantService svc, CancellationToken ct) =>
            svc.SuggestPriceAsync(p.UserId(), req, ct));
    }
}
