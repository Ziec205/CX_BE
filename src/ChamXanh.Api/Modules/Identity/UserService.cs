using System.Text.RegularExpressions;
using ChamXanh.Api.Common;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Identity;

public record UpdateProfileRequest(string? DisplayName, string? FullName, string? ProvinceId, string? WardId, bool? HidePhone, string? AvatarMediaId);

public class UserService(IMongoDatabase db, TimeProvider clock)
{
    public IMongoCollection<User> Users { get; } = db.GetCollection<User>("users");

    readonly PasswordHasher<User> _hasher = new();
    static readonly Regex UsernamePattern = new("^[a-z0-9._]{4,30}$", RegexOptions.Compiled);
    const int MinPasswordLength = 8;
    const int MaxFailedLogins = 5;

    public async Task EnsureIndexesAsync()
    {
        // Index unique cũ trên phone không có partial filter: tài khoản không SĐT sẽ trùng nhau → thay bằng index partial.
        using var cursor = await Users.Indexes.ListAsync();
        foreach (var ix in await cursor.ToListAsync())
            if (ix["key"].AsBsonDocument.Contains("phone") && !ix.Contains("partialFilterExpression"))
                await Users.Indexes.DropOneAsync(ix["name"].AsString);

        await Users.Indexes.CreateManyAsync(
        [
            UniqueWhenString(u => u.Phone, "phone", "phone_unique"),
            UniqueWhenString(u => u.Username, "username", "username_unique"),
        ]);
    }

    static CreateIndexModel<User> UniqueWhenString(System.Linq.Expressions.Expression<Func<User, object?>> field, string name, string indexName) =>
        new(Builders<User>.IndexKeys.Ascending(field), new CreateIndexOptions<User>
        {
            Unique = true, Name = indexName,
            PartialFilterExpression = new BsonDocument(name, new BsonDocument("$type", "string")),
        });

    const int MaxSignupsPerIpPerHour = 3;
    const int MaxSignupsPerIpPerDay = 10;

    public class SignupLog
    {
        [MongoDB.Bson.Serialization.Attributes.BsonId] public ObjectId Id { get; set; }
        public string Ip { get; set; } = default!;
        public DateTime At { get; set; }
    }

    IMongoCollection<SignupLog> SignupLogs => db.GetCollection<SignupLog>("signupLogs");

    public Task EnsureSignupIndexesAsync() => SignupLogs.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<SignupLog>(Builders<SignupLog>.IndexKeys.Ascending(l => l.At), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }),
        new CreateIndexModel<SignupLog>(Builders<SignupLog>.IndexKeys.Ascending(l => l.Ip).Ascending(l => l.At)),
    ]);

    /// <summary>Chống tạo tài khoản hàng loạt: giới hạn số lần đăng ký theo IP.</summary>
    async Task GuardSignupRateAsync(string? ip, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ip)) return;
        var lastDay = await SignupLogs.Find(l => l.Ip == ip && l.At > now.AddDays(-1)).Project(l => l.At).ToListAsync(ct);
        if (lastDay.Count(a => a > now.AddHours(-1)) >= MaxSignupsPerIpPerHour || lastDay.Count >= MaxSignupsPerIpPerDay)
            throw DomainException.TooMany("Bạn đã tạo quá nhiều tài khoản từ mạng này, vui lòng thử lại sau");
    }

    /// <summary>Đăng ký thành viên bằng tên đăng nhập + mật khẩu (không cần SĐT/CCCD).</summary>
    public async Task<User> RegisterAsync(string username, string password, string confirmPassword, string? ip, CancellationToken ct)
    {
        var name = (username ?? "").Trim().ToLowerInvariant();
        if (!UsernamePattern.IsMatch(name))
            throw new DomainException("INVALID_USERNAME", "Tên đăng nhập dài 4–30 ký tự, chỉ gồm chữ không dấu, số, dấu chấm hoặc gạch dưới");
        if ((password ?? "").Length < MinPasswordLength)
            throw new DomainException("WEAK_PASSWORD", $"Mật khẩu tối thiểu {MinPasswordLength} ký tự");
        if (password != confirmPassword)
            throw new DomainException("PASSWORD_MISMATCH", "Hai lần nhập mật khẩu không khớp");

        var now = clock.GetUtcNow().UtcDateTime;
        await GuardSignupRateAsync(ip, now, ct);
        var user = new User { Username = name, DisplayName = name, CreatedAt = now, LastSeenAt = now };
        user.PasswordHash = _hasher.HashPassword(user, password!);
        try
        {
            await Users.InsertOneAsync(user, cancellationToken: ct);
            if (!string.IsNullOrEmpty(ip)) await SignupLogs.InsertOneAsync(new SignupLog { Ip = ip, At = now }, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DomainException.Conflict("USERNAME_TAKEN", "Tên đăng nhập đã có người dùng");
        }
        return user;
    }

    public async Task<User> LoginWithPasswordAsync(string username, string password, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var name = (username ?? "").Trim().ToLowerInvariant();
        var invalid = new DomainException("INVALID_CREDENTIALS", "Sai tên đăng nhập hoặc mật khẩu", StatusCodes.Status401Unauthorized);
        var user = await Users.Find(u => u.Username == name).FirstOrDefaultAsync(ct);
        if (user?.PasswordHash is null) throw invalid;
        if (user.LoginLockedUntil > now) throw DomainException.TooMany("Đăng nhập sai nhiều lần, hãy thử lại sau 15 phút");

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "") == PasswordVerificationResult.Failed)
        {
            var fails = user.FailedLogins + 1;
            await Users.UpdateOneAsync(u => u.Id == user.Id, Builders<User>.Update
                .Set(u => u.FailedLogins, fails >= MaxFailedLogins ? 0 : fails)
                .Set(u => u.LoginLockedUntil, fails >= MaxFailedLogins ? now.AddMinutes(15) : null), cancellationToken: ct);
            throw invalid;
        }
        EnsureCanSignIn(user);
        await Users.UpdateOneAsync(u => u.Id == user.Id, Builders<User>.Update
            .Set(u => u.FailedLogins, 0).Set(u => u.LoginLockedUntil, null).Set(u => u.LastSeenAt, now), cancellationToken: ct);
        return user;
    }

    /// <summary>Gắn SĐT đã xác thực OTP vào tài khoản đang đăng nhập.</summary>
    public async Task<User> AttachPhoneAsync(string userId, string phone, CancellationToken ct)
    {
        var user = await RequireActiveAsync(userId, ct);
        if (user.Phone == phone) return user;
        if (user.Phone is not null) throw DomainException.Conflict("PHONE_ALREADY_SET", "Tài khoản đã có số điện thoại");
        try
        {
            await Users.UpdateOneAsync(u => u.Id == userId, Builders<User>.Update.Set(u => u.Phone, phone), cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DomainException.Conflict("PHONE_TAKEN", "Số điện thoại đã thuộc về tài khoản khác");
        }
        return await GetAsync(userId, ct);
    }

    /// <summary>Xác nhận mật khẩu cho thao tác nhạy cảm (vd xóa tài khoản không có SĐT).</summary>
    public void EnsurePassword(User user, string? password)
    {
        if (user.PasswordHash is null || _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "") == PasswordVerificationResult.Failed)
            throw new DomainException("INVALID_PASSWORD", "Mật khẩu không đúng");
    }

    public async Task<(User User, bool IsNew)> GetOrCreateByPhoneAsync(string phone, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await Users.Find(u => u.Phone == phone).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            EnsureCanSignIn(existing);
            await Users.UpdateOneAsync(u => u.Id == existing.Id, Builders<User>.Update.Set(u => u.LastSeenAt, now), cancellationToken: ct);
            return (existing, false);
        }
        var user = new User { Phone = phone, DisplayName = $"Người dùng {phone[^4..]}", CreatedAt = now, LastSeenAt = now };
        try
        {
            await Users.InsertOneAsync(user, cancellationToken: ct);
            return (user, true);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return (await Users.Find(u => u.Phone == phone).FirstAsync(ct), false); // hai request đồng thời
        }
    }

    public async Task<User> GetAsync(string id, CancellationToken ct = default) =>
        (MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Users.Find(u => u.Id == id).FirstOrDefaultAsync(ct) : null)
        ?? throw DomainException.NotFound("người dùng");

    public async Task<User> RequireActiveAsync(string id, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        EnsureCanSignIn(user);
        return user;
    }

    public void EnsureCanSignIn(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (user.Status is UserStatus.Banned or UserStatus.Deleted)
            throw DomainException.Forbidden("Tài khoản đã bị khóa vĩnh viễn hoặc đã xóa");
        if (user.Status == UserStatus.Locked && (user.LockedUntil is null || user.LockedUntil > now))
            throw DomainException.Forbidden($"Tài khoản bị khóa tạm đến {user.LockedUntil:dd/MM/yyyy}");
    }

    /// <summary>Tài khoản đủ điều kiện đăng tin: đã khai họ tên + tỉnh (T0) và không bị hạn chế.</summary>
    public void EnsureCanPost(User user)
    {
        EnsureCanSignIn(user);
        if (!user.HasPostingProfile)
            throw new DomainException("PROFILE_REQUIRED", "Vui lòng khai họ tên và tỉnh/thành trước khi đăng tin đầu tiên");
        if (user.PostingRestrictedUntil is { } until && until > clock.GetUtcNow().UtcDateTime)
            throw DomainException.Forbidden($"Tài khoản bị hạn chế đăng tin đến {until:dd/MM/yyyy HH:mm}");
    }

    public async Task<User> UpdateProfileAsync(string id, UpdateProfileRequest req, CancellationToken ct)
    {
        var u = Builders<User>.Update;
        var updates = new List<UpdateDefinition<User>>();
        if (req.DisplayName is { } dn)
        {
            dn = dn.Trim();
            if (dn.Length is < 2 or > 50) throw new DomainException("INVALID_DISPLAY_NAME", "Tên hiển thị dài 2–50 ký tự");
            updates.Add(u.Set(x => x.DisplayName, dn));
        }
        if (req.FullName is { } fn)
        {
            fn = fn.Trim();
            if (fn.Length is < 2 or > 100) throw new DomainException("INVALID_FULL_NAME", "Họ tên dài 2–100 ký tự");
            updates.Add(u.Set(x => x.FullName, fn));
        }
        if (req.ProvinceId is not null) updates.Add(u.Set(x => x.ProvinceId, req.ProvinceId));
        if (req.WardId is not null) updates.Add(u.Set(x => x.WardId, req.WardId));
        if (req.HidePhone is { } hp) updates.Add(u.Set(x => x.HidePhone, hp));
        if (req.AvatarMediaId is not null) updates.Add(u.Set(x => x.AvatarMediaId, req.AvatarMediaId));
        if (updates.Count > 0) await Users.UpdateOneAsync(x => x.Id == id, u.Combine(updates), cancellationToken: ct);
        return await GetAsync(id, ct);
    }

    public Task SetFlagAsync(string id, System.Linq.Expressions.Expression<Func<User, bool>> flag, bool value, CancellationToken ct = default) =>
        Users.UpdateOneAsync(x => x.Id == id, Builders<User>.Update.Set(flag, value), cancellationToken: ct);
}
