using System.Security.Claims;
using ChamXanh.Api.Common.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Messaging;

/// <summary>Kênh realtime: client kết nối /hubs/chat?access_token=... và nhận sự kiện "message".</summary>
[Authorize(Policy = Policies.Member)]
public class ChatHub : Hub
{
    public static string UserGroup(string userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(Context.User!.UserId()));
        await base.OnConnectedAsync();
    }
}

public record StartConversationRequest(string ListingId);
public record BlockRequest(bool Block);

public static class ChatEndpoints
{
    public static void MapChat(this IEndpointRouteBuilder app)
    {
        app.MapHub<ChatHub>("/hubs/chat");

        app.MapPost("/api/auth/hub-token", (ClaimsPrincipal p, TokenService tokens) =>
            Results.Ok(new { token = tokens.IssueHubToken(p.UserId(), p.ActorName()), expiresInSeconds = 120 }))
            .WithTags("Chat").RequireAuthorization(Policies.Member);

        var g = app.MapGroup("/api/conversations").WithTags("Chat").RequireAuthorization(Policies.Member);
        g.MapPost("/", (StartConversationRequest req, ClaimsPrincipal p, ChatService svc, CancellationToken ct) => svc.StartAsync(p.UserId(), req.ListingId, ct));

        g.MapGet("/", async (string? role, bool? archived, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var f = Builders<Conversation>.Filter;
            var filter = role switch
            {
                "buying" => f.Eq(c => c.BuyerId, userId),
                "selling" => f.Eq(c => c.SellerId, userId),
                _ => f.Eq(c => c.BuyerId, userId) | f.Eq(c => c.SellerId, userId),
            };
            filter &= f.Ne(c => c.Last, null);
            filter &= archived == true ? f.AnyEq(c => c.ArchivedBy, userId) : f.Not(f.AnyEq(c => c.ArchivedBy, userId));
            var list = await svc.Conversations.Find(filter).SortByDescending(c => c.Last!.At).Limit(100).ToListAsync(ct);
            return list.Select(c => new
            {
                c.Id, c.ListingId, c.Listing, c.Last, role = c.BuyerId == userId ? "buyer" : "seller", otherUserId = c.Other(userId),
                unread = c.Unread.GetValueOrDefault(userId), blocked = c.BlockedBy.Count > 0,
            });
        });

        // Một hội thoại (kể cả hội thoại vừa tạo, chưa có tin nhắn nào nên chưa nằm trong danh sách).
        g.MapGet("/{id}", async (string id, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var c = await svc.GetForMemberAsync(id, userId, ct);
            return new
            {
                c.Id, c.ListingId, c.Listing, c.Last, role = c.BuyerId == userId ? "buyer" : "seller", otherUserId = c.Other(userId),
                unread = c.Unread.GetValueOrDefault(userId), blocked = c.BlockedBy.Count > 0,
            };
        });

        g.MapGet("/{id}/messages", async (string id, DateTime? before, int? limit, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var conv = await svc.GetForMemberAsync(id, userId, ct);
            var f = Builders<ChatMessage>.Filter.Eq(m => m.ConversationId, conv.Id);
            if (before is { } b) f &= Builders<ChatMessage>.Filter.Lt(m => m.CreatedAt, b);
            var msgs = await svc.Messages.Find(f).SortByDescending(m => m.CreatedAt).Limit(Math.Clamp(limit ?? 50, 1, 100)).ToListAsync(ct);
            // Cảnh báo chỉ dành cho người nhận
            return msgs.Select(m => m.SenderId == userId ? new ChatMessage
            {
                Id = m.Id, ConversationId = m.ConversationId, SenderId = m.SenderId, Type = m.Type, Text = m.Text, MediaIds = m.MediaIds,
                Lat = m.Lat, Lng = m.Lng, Offer = m.Offer, CreatedAt = m.CreatedAt,
            } : m);
        });

        g.MapPost("/{id}/messages", (string id, SendMessageRequest req, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
            svc.SendAsync(id, p.UserId(), req, ct));
        g.MapPost("/{id}/offers/{messageId}/respond", (string id, string messageId, RespondOfferRequest req, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
            svc.RespondOfferAsync(id, messageId, p.UserId(), req, ct));
        g.MapPost("/{id}/read", async (string id, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
        {
            await svc.MarkReadAsync(id, p.UserId(), ct);
            return Results.NoContent();
        });
        g.MapPost("/{id}/block", async (string id, BlockRequest req, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
        {
            await svc.BlockAsync(id, p.UserId(), req.Block, ct);
            return Results.NoContent();
        });
        g.MapPost("/{id}/archive", async (string id, ClaimsPrincipal p, ChatService svc, CancellationToken ct) =>
        {
            var userId = p.UserId();
            await svc.GetForMemberAsync(id, userId, ct);
            await svc.Conversations.UpdateOneAsync(c => c.Id == id, Builders<Conversation>.Update.AddToSet(c => c.ArchivedBy, userId), cancellationToken: ct);
            return Results.NoContent();
        });

        app.MapGet("/api/users/{id}/response-stats", (string id, ChatService svc, CancellationToken ct) => svc.ResponseStatsAsync(id, ct)).WithTags("Users");
    }
}
