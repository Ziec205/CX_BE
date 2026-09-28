using ChamXanh.Api.Common;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Identity;

public record UpdateProfileRequest(string? DisplayName, string? FullName, string? ProvinceId, string? WardId, bool? HidePhone, string? AvatarMediaId);

public class UserService(IMongoDatabase db, TimeProvider clock)
{
    public IMongoCollection<User> Users { get; } = db.GetCollection<User>("users");

    public Task EnsureIndexesAsync() => Users.Indexes.CreateOneAsync(new CreateIndexModel<User>(
        Builders<User>.IndexKeys.Ascending(u => u.Phone), new CreateIndexOptions { Unique = true }));

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
