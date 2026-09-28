using System.Security.Claims;
using ChamXanh.Api.Common.Auth;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Identity;

public record OtpRequest(string Phone, string? DeviceId);
public record OtpVerifyRequest(string Phone, string Code);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(TokenPair Tokens, MeResponse User, bool IsNew);
public record MeResponse(string Id, string Phone, string DisplayName, string? FullName, string? ProvinceId, string? WardId,
    string? AvatarMediaId, bool HidePhone, UserFlags Flags, string Status, bool CanPost, DateTime CreatedAt)
{
    public static MeResponse From(User u) => new(u.Id, u.Phone, u.DisplayName, u.FullName, u.ProvinceId, u.WardId,
        u.AvatarMediaId, u.HidePhone, u.Flags, u.Status.ToString(), u.HasPostingProfile, u.CreatedAt);
}

public static class IdentityEndpoints
{
    public static void MapIdentity(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/otp/request", async (OtpRequest req, OtpService otp, CancellationToken ct) =>
        {
            var phone = PhoneNumber.Normalize(req.Phone);
            var devCode = await otp.RequestAsync(phone, req.DeviceId, ct);
            return Results.Ok(new { sent = true, devCode });
        });

        auth.MapPost("/otp/verify", async (OtpVerifyRequest req, OtpService otp, UserService users, TokenService tokens, CancellationToken ct) =>
        {
            var phone = PhoneNumber.Normalize(req.Phone);
            await otp.VerifyAsync(phone, req.Code, ct);
            var (user, isNew) = await users.GetOrCreateByPhoneAsync(phone, ct);
            var pair = await tokens.IssueAsync(user.Id, Claims.Member, user.DisplayName);
            return Results.Ok(new AuthResponse(pair, MeResponse.From(user), isNew));
        });

        auth.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, UserService users, Admin.AdminAuthService admins, CancellationToken ct) =>
        {
            var old = await tokens.ConsumeRefreshAsync(req.RefreshToken);
            if (old.Kind == Claims.Member)
            {
                var user = await users.RequireActiveAsync(old.SubjectId, ct);
                return Results.Ok(await tokens.IssueAsync(user.Id, Claims.Member, user.DisplayName));
            }
            return Results.Ok(await admins.ReissueAsync(old.SubjectId, ct));
        });

        auth.MapPost("/logout", async (RefreshRequest req, TokenService tokens) =>
        {
            await tokens.RevokeAsync(req.RefreshToken);
            return Results.NoContent();
        });

        var me = app.MapGroup("/api/me").WithTags("Me").RequireAuthorization(Policies.Member);
        me.MapGet("/", async (ClaimsPrincipal p, UserService users, CancellationToken ct) =>
            MeResponse.From(await users.GetAsync(p.UserId(), ct)));
        me.MapPut("/", async (UpdateProfileRequest req, ClaimsPrincipal p, UserService users, CancellationToken ct) =>
            MeResponse.From(await users.UpdateProfileAsync(p.UserId(), req, ct)));

        // Tra cứu người dùng cho vận hành (ví, kiểm duyệt). SĐT chỉ trả về cho admin.
        app.MapGet("/api/admin/users/lookup", async (string q, UserService users, CancellationToken ct) =>
        {
            User? u = MongoDB.Bson.ObjectId.TryParse(q, out _) ? await users.Users.Find(x => x.Id == q).FirstOrDefaultAsync(ct) : null;
            if (u is null)
            {
                try { var phone = PhoneNumber.Normalize(q); u = await users.Users.Find(x => x.Phone == phone).FirstOrDefaultAsync(ct); }
                catch (Common.DomainException) { }
            }
            return u is null ? Results.NotFound() : Results.Ok(new { u.Id, u.Phone, u.DisplayName, u.FullName, u.Flags, Status = u.Status.ToString(), u.ActiveViolationPoints, u.CreatedAt });
        }).WithTags("Admin Users").RequireAuthorization(p => p
            .RequireClaim(Claims.Kind, Claims.Admin).RequireClaim(Claims.Mfa, "true")
            .RequireClaim(Claims.Perm, Perm.WalletAdjust, Perm.UserSanction, Perm.ListingModerate, Perm.DisputeResolve));

        app.MapGet("/api/users/{id}", async (string id, UserService users, CancellationToken ct) =>
            PublicUser.From(await users.GetAsync(id, ct))).WithTags("Users");
    }
}
