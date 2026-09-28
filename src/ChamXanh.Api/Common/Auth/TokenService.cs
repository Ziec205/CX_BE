using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Common.Auth;

public static class Claims
{
    public const string Kind = "kind", Perm = "perm", Mfa = "mfa", Scope = "scope", HubScope = "hub";
    public const string Member = "member", Admin = "admin";
}

public static class Policies
{
    public const string Member = "member";
    public const string Admin = "admin";
    public static string ForPerm(string perm) => $"perm:{perm}";
}

public class AuthOptions
{
    public string JwtKey { get; set; } = "";
    public string Issuer { get; set; } = "chamxanh";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 60;
    /// <summary>Bí mật dùng chung với BFF (Next.js) để tin header X-Client-IP khi giới hạn đăng ký theo IP.</summary>
    public string? BffSecret { get; set; }
}

public record TokenPair(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken);

public class RefreshToken
{
    [BsonId] public string Id { get; set; } = default!; // SHA-256 của token, không lưu token gốc
    public string SubjectId { get; set; } = default!;
    public string Kind { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
}

public class TokenService(AuthOptions options, IMongoDatabase db, TimeProvider clock)
{
    readonly IMongoCollection<RefreshToken> _refresh = db.GetCollection<RefreshToken>("refreshTokens");

    public static SymmetricSecurityKey SigningKey(AuthOptions o) => new(Encoding.UTF8.GetBytes(o.JwtKey));

    public Task EnsureIndexesAsync() => _refresh.Indexes.CreateOneAsync(new CreateIndexModel<RefreshToken>(
        Builders<RefreshToken>.IndexKeys.Ascending(t => t.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));

    public async Task<TokenPair> IssueAsync(string subjectId, string kind, string name, IEnumerable<Claim>? extra = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subjectId),
            new(JwtRegisteredClaimNames.Name, name),
            new(Claims.Kind, kind),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (extra is not null) claims.AddRange(extra);

        var token = new JwtSecurityToken(options.Issuer, options.Issuer, claims, now, expires,
            new SigningCredentials(SigningKey(options), SecurityAlgorithms.HmacSha256));
        var access = new JwtSecurityTokenHandler().WriteToken(token);

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await _refresh.InsertOneAsync(new RefreshToken
        {
            Id = Hash(refresh), SubjectId = subjectId, Kind = kind, ExpiresAt = now.AddDays(options.RefreshTokenDays),
        });
        return new(access, expires, refresh);
    }

    /// <summary>Token 2 phút chỉ để mở kết nối SignalR (claim scope=hub, bị chặn ở mọi API khác).
    /// Dùng khi token chính nằm trong cookie httpOnly mà trình duyệt không đọc được.</summary>
    public string IssueHubToken(string subjectId, string name)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(options.Issuer, options.Issuer,
        [
            new Claim(JwtRegisteredClaimNames.Sub, subjectId), new Claim(JwtRegisteredClaimNames.Name, name),
            new Claim(Claims.Kind, Claims.Member), new Claim(Claims.Scope, Claims.HubScope),
        ], now, now.AddMinutes(2), new SigningCredentials(SigningKey(options), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Đổi refresh token (xoay vòng: token cũ bị hủy ngay khi dùng).</summary>
    public async Task<RefreshToken> ConsumeRefreshAsync(string refreshToken)
    {
        var doc = await _refresh.FindOneAndDeleteAsync(t => t.Id == Hash(refreshToken));
        if (doc is null || doc.ExpiresAt < clock.GetUtcNow().UtcDateTime)
            throw new DomainException("INVALID_REFRESH_TOKEN", "Phiên đăng nhập đã hết hạn", StatusCodes.Status401Unauthorized);
        return doc;
    }

    public Task RevokeAsync(string refreshToken) => _refresh.DeleteOneAsync(t => t.Id == Hash(refreshToken));
    public Task RevokeAllAsync(string subjectId) => _refresh.DeleteManyAsync(t => t.SubjectId == subjectId);

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

public static class ClaimsPrincipalExtensions
{
    public static string UserId(this ClaimsPrincipal p) =>
        p.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? p.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new DomainException("UNAUTHENTICATED", "Chưa đăng nhập", StatusCodes.Status401Unauthorized);

    public static string ActorName(this ClaimsPrincipal p) =>
        p.FindFirstValue(JwtRegisteredClaimNames.Name) ?? p.FindFirstValue(ClaimTypes.Name) ?? p.UserId();

    public static bool IsAdmin(this ClaimsPrincipal p) => p.FindFirstValue(Claims.Kind) == Claims.Admin;
}
