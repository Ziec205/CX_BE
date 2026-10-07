using System.Security.Claims;
using ChamXanh.Api.Common.Auth;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Identity;

public record OtpRequest(string Phone, string? DeviceId);
public record OtpVerifyRequest(string Phone, string Code);
public record RefreshRequest(string RefreshToken);
public record RegisterRequest(string Username, string Password, string ConfirmPassword);
public record PasswordLoginRequest(string Username, string Password);
public record AuthResponse(TokenPair Tokens, MeResponse User, bool IsNew);
public record MeResponse(string Id, string? Phone, string? Username, string DisplayName, string? FullName, string? ProvinceId, string? WardId,
    string? AvatarMediaId, bool HidePhone, UserFlags Flags, string Status, bool CanPost, DateTime CreatedAt, string? Email)
{
    public static MeResponse From(User u) => new(u.Id, u.Phone, u.Username, u.DisplayName, u.FullName, u.ProvinceId, u.WardId,
        u.AvatarMediaId, u.HidePhone, u.Flags, u.Status.ToString(), u.HasPostingProfile, u.CreatedAt, u.Email);
}

public static class IdentityEndpoints
{
    /// <summary>
    /// IP người dùng để giới hạn đăng ký. Yêu cầu qua BFF: BFF gửi X-Client-IP kèm X-Bff-Secret (chỉ tin khi khớp cấu hình).
    /// Gọi thẳng API: lấy mục cuối của X-Forwarded-For (do proxy Render thêm vào, client không giả được).
    /// </summary>
    static string? ClientIp(HttpContext http, AuthOptions auth)
    {
        var h = http.Request.Headers;
        if (!string.IsNullOrEmpty(auth.BffSecret) && h["X-Bff-Secret"].ToString() is { Length: > 0 } s
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(s), System.Text.Encoding.UTF8.GetBytes(auth.BffSecret))
            && h["X-Client-IP"].ToString().Trim() is { Length: > 0 } clientIp)
            return clientIp;
        var xff = h["X-Forwarded-For"].ToString();
        if (xff.Length > 0) return xff.Split(',')[^1].Trim();
        return http.Connection.RemoteIpAddress?.ToString();
    }

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

        // Thành viên thường: tên đăng nhập + mật khẩu. SĐT/CCCD chỉ cần khi mở Nhà vườn/Shop.
        auth.MapPost("/register", async (RegisterRequest req, HttpContext http, AuthOptions auth, UserService users, TokenService tokens, CancellationToken ct) =>
        {
            var user = await users.RegisterAsync(req.Username, req.Password, req.ConfirmPassword, ClientIp(http, auth), ct);
            var pair = await tokens.IssueAsync(user.Id, Claims.Member, user.DisplayName);
            return Results.Ok(new AuthResponse(pair, MeResponse.From(user), true));
        });

        auth.MapPost("/login", async (PasswordLoginRequest req, UserService users, TokenService tokens, CancellationToken ct) =>
        {
            var user = await users.LoginWithPasswordAsync(req.Username, req.Password, ct);
            var pair = await tokens.IssueAsync(user.Id, Claims.Member, user.DisplayName);
            return Results.Ok(new AuthResponse(pair, MeResponse.From(user), false));
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

        // Gắn SĐT cho tài khoản tên đăng nhập (bắt buộc trước khi mở Nhà vườn/Shop).
        me.MapPost("/phone/request", async (OtpRequest req, OtpService otp, CancellationToken ct) =>
        {
            var phone = PhoneNumber.Normalize(req.Phone);
            return Results.Ok(new { sent = true, devCode = await otp.RequestAsync(phone, req.DeviceId, ct) });
        });
        me.MapPost("/phone/verify", async (OtpVerifyRequest req, ClaimsPrincipal p, OtpService otp, UserService users, CancellationToken ct) =>
        {
            var phone = PhoneNumber.Normalize(req.Phone);
            await otp.VerifyAsync(phone, req.Code, ct);
            return MeResponse.From(await users.AttachPhoneAsync(p.UserId(), phone, ct));
        });

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
