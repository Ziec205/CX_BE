using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Identity;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Media;

public static class MediaEndpoints
{
    const int MaxUploadsPerDay = 300;

    public static void MapMedia(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/media", async ([FromForm] IFormFile file, [FromForm] string? kind, [FromForm] bool? capturedInApp,
            [FromForm] DateTime? capturedAt, ClaimsPrincipal p, MediaService media, UserService users, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = p.UserId();
            var user = await users.RequireActiveAsync(userId, ct);
            var mediaKind = Enum.TryParse<MediaKind>(kind, true, out var k) ? k : MediaKind.ListingPhoto;
            if (mediaKind is MediaKind.KycDocument or MediaKind.DisputeEvidence && file.Length == 0)
                throw new DomainException("EMPTY_FILE", "File rỗng");
            var since = clock.GetUtcNow().UtcDateTime.AddDays(-1);
            if (await media.Items.CountDocumentsAsync(m => m.OwnerId == userId && m.CreatedAt > since, cancellationToken: ct) >= MaxUploadsPerDay)
                throw DomainException.TooMany("Bạn đã tải lên quá nhiều ảnh hôm nay");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var item = await media.UploadImageAsync(userId, mediaKind, ms.ToArray(), user.DisplayName, capturedInApp ?? false, capturedAt, ct);
            return Results.Ok(MediaService.ToDto(item));
        }).WithTags("Media").RequireAuthorization(Policies.Member).DisableAntiforgery();

        // Ảnh công khai: URL bất biến theo nội dung nên cache được 1 năm ở CDN (BR-MED-01).
        app.MapGet("/media/{id}/{variant}.webp", async (string id, string variant, MediaService media, HttpContext http, CancellationToken ct) =>
        {
            var item = await media.FindAsync(id, ct);
            if (item is null || item.Secure) return Results.NotFound();
            var file = await media.OpenAsync(item, variant, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Stream(file.Value.Stream, file.Value.ContentType);
        }).WithTags("Media");

        // Ảnh riêng tư (CCCD, bằng chứng tranh chấp): chỉ chủ sở hữu hoặc admin có quyền, không cache, ghi audit (BR-MED-02).
        app.MapGet("/api/secure-media/{id}/{variant}", async (string id, string variant, ClaimsPrincipal p, MediaService media,
            AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var item = await media.FindAsync(id, ct);
            if (item is null || !item.Secure) return Results.NotFound();
            var isOwner = !p.IsAdmin() && item.OwnerId == p.UserId();
            var needed = item.Kind == MediaKind.KycDocument ? Perm.KycView : Perm.DisputeResolve;
            var isAllowedAdmin = p.IsAdmin() && p.HasClaim(Claims.Mfa, "true") && p.HasClaim(Claims.Perm, needed);
            if (!isOwner && !isAllowedAdmin) return Results.Forbid();
            if (isAllowedAdmin) await audit.LogAsync(p, "media.view_secure", "media", id, ct: ct);
            var file = await media.OpenAsync(item, variant, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Stream(file.Value.Stream, file.Value.ContentType);
        }).WithTags("Media").RequireAuthorization();
    }
}
