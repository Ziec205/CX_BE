using System.Security.Claims;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using OtpNet;

namespace ChamXanh.Api.Modules.Admin;

public class AdminUser
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string Username { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public List<string> Roles { get; set; } = [];
    public string? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
    public bool Active { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdminOptions
{
    /// <summary>BR-ADM-03: bắt buộc 2FA. Chỉ tắt ở môi trường dev/test.</summary>
    /// <summary>Tắt mặc định theo yêu cầu (08/10/2026): admin đăng nhập bằng tên + mật khẩu. Bật lại bằng biến Admin__Require2fa=true.</summary>
    public bool Require2fa { get; set; }
    public string? BootstrapUsername { get; set; }
    public string? BootstrapPassword { get; set; }
    /// <summary>Chỉ bật ở Development: cho tài khoản bootstrap dùng mật khẩu ngắn (vd admin/123) để thử nhanh. Production luôn tắt.</summary>
    public bool AllowWeakBootstrapPassword { get; set; }
}

public record AdminLoginRequest(string Username, string Password, string? TotpCode);
public record CreateAdminRequest(string Username, string DisplayName, string Password, List<string> Roles);
public record TotpCodeRequest(string Code);
public record AdminMe(string Id, string Username, string DisplayName, List<string> Roles, string[] Permissions, bool TotpEnabled);

public class AdminAuthService(IMongoDatabase db, TokenService tokens, AdminOptions options, TimeProvider clock, ILogger<AdminAuthService> logger)
{
    readonly IMongoCollection<AdminUser> _admins = db.GetCollection<AdminUser>("adminUsers");
    readonly PasswordHasher<AdminUser> _hasher = new();

    public async Task EnsureIndexesAndBootstrapAsync()
    {
        await _admins.Indexes.CreateOneAsync(new CreateIndexModel<AdminUser>(
            Builders<AdminUser>.IndexKeys.Ascending(a => a.Username), new CreateIndexOptions { Unique = true }));
        if (await _admins.EstimatedDocumentCountAsync() > 0) return;
        if (string.IsNullOrWhiteSpace(options.BootstrapUsername) || string.IsNullOrWhiteSpace(options.BootstrapPassword))
        {
            logger.LogWarning("Chưa có tài khoản admin nào. Đặt Admin:BootstrapUsername/BootstrapPassword để tạo Super Admin đầu tiên.");
            return;
        }
        await CreateAsync(new CreateAdminRequest(options.BootstrapUsername, "Super Admin", options.BootstrapPassword, [AdminRoles.SuperAdmin]),
            skipPasswordPolicy: options.AllowWeakBootstrapPassword);
        logger.LogWarning("Đã tạo Super Admin '{User}'. Hãy bật 2FA và đổi mật khẩu ngay.", options.BootstrapUsername);
    }

    public async Task<AdminUser> CreateAsync(CreateAdminRequest req, CancellationToken ct = default, bool skipPasswordPolicy = false)
    {
        var unknown = req.Roles.Where(r => !AdminRoles.Permissions.ContainsKey(r)).ToList();
        if (unknown.Count > 0) throw new DomainException("UNKNOWN_ROLE", $"Vai trò không tồn tại: {string.Join(", ", unknown)}");
        if (!skipPasswordPolicy && req.Password.Length < 12) throw new DomainException("WEAK_PASSWORD", "Mật khẩu admin tối thiểu 12 ký tự");
        var admin = new AdminUser
        {
            Username = req.Username.Trim().ToLowerInvariant(), DisplayName = req.DisplayName,
            Roles = req.Roles, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        admin.PasswordHash = _hasher.HashPassword(admin, req.Password);
        try { await _admins.InsertOneAsync(admin, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DomainException.Conflict("USERNAME_TAKEN", "Tên đăng nhập đã tồn tại");
        }
        return admin;
    }

    public async Task<TokenPair> LoginAsync(AdminLoginRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var username = req.Username.Trim().ToLowerInvariant();
        var admin = await _admins.Find(a => a.Username == username).FirstOrDefaultAsync(ct);
        var invalid = new DomainException("INVALID_CREDENTIALS", "Sai tên đăng nhập, mật khẩu hoặc mã 2FA", StatusCodes.Status401Unauthorized);
        if (admin is null || !admin.Active) throw invalid;
        if (admin.LockedUntil > now) throw DomainException.TooMany("Tài khoản admin tạm khóa do đăng nhập sai nhiều lần");

        var ok = _hasher.VerifyHashedPassword(admin, admin.PasswordHash, req.Password) != PasswordVerificationResult.Failed;
        if (ok && admin.TotpEnabled && options.Require2fa) ok = VerifyTotp(admin.TotpSecret!, req.TotpCode);
        if (!ok)
        {
            var fails = admin.FailedLogins + 1;
            await _admins.UpdateOneAsync(a => a.Id == admin.Id, Builders<AdminUser>.Update
                .Set(a => a.FailedLogins, fails).Set(a => a.LockedUntil, fails >= 5 ? now.AddMinutes(15) : null), cancellationToken: ct);
            throw invalid;
        }
        await _admins.UpdateOneAsync(a => a.Id == admin.Id, Builders<AdminUser>.Update.Set(a => a.FailedLogins, 0), cancellationToken: ct);
        return await IssueAsync(admin);
    }

    public async Task<TokenPair> ReissueAsync(string adminId, CancellationToken ct)
    {
        var admin = await GetAsync(adminId, ct);
        if (!admin.Active) throw DomainException.Forbidden("Tài khoản admin đã bị vô hiệu hóa");
        return await IssueAsync(admin);
    }

    Task<TokenPair> IssueAsync(AdminUser admin)
    {
        // Chưa bật 2FA thì token không mang quyền nào (trừ khi cấu hình dev tắt yêu cầu 2FA): chỉ dùng được để bật 2FA.
        var mfaSatisfied = admin.TotpEnabled || !options.Require2fa;
        var claims = new List<Claim> { new(Claims.Mfa, mfaSatisfied ? "true" : "false") };
        if (mfaSatisfied) claims.AddRange(AdminRoles.Resolve(admin.Roles).Select(p => new Claim(Claims.Perm, p)));
        return tokens.IssueAsync(admin.Id, Claims.Admin, admin.Username, claims);
    }

    public async Task<object> BeginTotpSetupAsync(string adminId, CancellationToken ct)
    {
        var admin = await GetAsync(adminId, ct);
        if (admin.TotpEnabled) throw DomainException.Conflict("TOTP_ALREADY_ENABLED", "2FA đã được bật");
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        await _admins.UpdateOneAsync(a => a.Id == adminId, Builders<AdminUser>.Update.Set(a => a.TotpSecret, secret), cancellationToken: ct);
        return new { secret, otpauthUri = $"otpauth://totp/ChamXanh:{admin.Username}?secret={secret}&issuer=ChamXanh" };
    }

    public async Task EnableTotpAsync(string adminId, string code, CancellationToken ct)
    {
        var admin = await GetAsync(adminId, ct);
        if (admin.TotpSecret is null) throw new DomainException("TOTP_NOT_SETUP", "Hãy gọi bước thiết lập 2FA trước");
        if (!VerifyTotp(admin.TotpSecret, code)) throw new DomainException("TOTP_INVALID", "Mã 2FA không đúng");
        await _admins.UpdateOneAsync(a => a.Id == adminId, Builders<AdminUser>.Update.Set(a => a.TotpEnabled, true), cancellationToken: ct);
        await tokens.RevokeAllAsync(adminId);
    }

    public async Task<AdminMe> MeAsync(string adminId, CancellationToken ct)
    {
        var a = await GetAsync(adminId, ct);
        return new(a.Id, a.Username, a.DisplayName, a.Roles, AdminRoles.Resolve(a.Roles), a.TotpEnabled);
    }

    public Task<List<AdminMe>> ListAsync(CancellationToken ct) =>
        _admins.Find(_ => true).Project(a => new AdminMe(a.Id, a.Username, a.DisplayName, a.Roles, Array.Empty<string>(), a.TotpEnabled)).ToListAsync(ct);

    public async Task<AdminUser> GetAsync(string id, CancellationToken ct) =>
        (ObjectId.TryParse(id, out _) ? await _admins.Find(a => a.Id == id).FirstOrDefaultAsync(ct) : null)
        ?? throw DomainException.NotFound("tài khoản admin");

    public static bool VerifyTotp(string secret, string? code) =>
        !string.IsNullOrWhiteSpace(code) && new Totp(Base32Encoding.ToBytes(secret)).VerifyTotp(code.Trim(), out _, new VerificationWindow(1, 1));
}

public static class AdminAuthEndpoints
{
    public static void MapAdminAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/auth").WithTags("Admin Auth");
        g.MapPost("/login", async (AdminLoginRequest req, AdminAuthService svc, CancellationToken ct) => Results.Ok(await svc.LoginAsync(req, ct)));

        var signedIn = g.MapGroup("").RequireAuthorization(Policies.Admin);
        signedIn.MapGet("/me", (ClaimsPrincipal p, AdminAuthService svc, CancellationToken ct) => svc.MeAsync(p.UserId(), ct));
        signedIn.MapPost("/2fa/setup", (ClaimsPrincipal p, AdminAuthService svc, CancellationToken ct) => svc.BeginTotpSetupAsync(p.UserId(), ct));
        signedIn.MapPost("/2fa/enable", async (TotpCodeRequest req, ClaimsPrincipal p, AdminAuthService svc, CancellationToken ct) =>
        {
            await svc.EnableTotpAsync(p.UserId(), req.Code, ct);
            return Results.Ok(new { enabled = true, message = "Đã bật 2FA, vui lòng đăng nhập lại" });
        });

        app.MapGet("/api/admin/audit", async (string? targetType, string? targetId, string? actorId, int? limit, AuditService audit, CancellationToken ct) =>
        {
            var f = Builders<AuditLog>.Filter;
            var filter = f.Empty;
            if (targetType is not null) filter &= f.Eq(l => l.TargetType, targetType);
            if (targetId is not null) filter &= f.Eq(l => l.TargetId, targetId);
            if (actorId is not null) filter &= f.Eq(l => l.ActorId, actorId);
            var logs = await audit.Logs.Find(filter).SortByDescending(l => l.At).Limit(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct);
            return logs.Select(l => new
            {
                id = l.Id.ToString(), l.ActorId, l.ActorName, l.Action, l.TargetType, l.TargetId, l.Reason, l.Ip, l.At,
                before = l.Before?.ToJson(), after = l.After?.ToJson(),
            });
        }).WithTags("Admin Audit").RequireAuthorization(Policies.ForPerm(Perm.ReportsView));

        var users = app.MapGroup("/api/admin/users").WithTags("Admin Users").RequireAuthorization(Policies.ForPerm(Perm.AdminManage));
        users.MapGet("/", (AdminAuthService svc, CancellationToken ct) => svc.ListAsync(ct));
        users.MapPost("/", async (CreateAdminRequest req, AdminAuthService svc, CancellationToken ct) =>
        {
            var a = await svc.CreateAsync(req, ct);
            return Results.Ok(new { a.Id, a.Username, a.Roles });
        });
    }
}
