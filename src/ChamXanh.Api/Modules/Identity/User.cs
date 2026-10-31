using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Identity;

public enum UserStatus { Active, Restricted, Locked, Banned, Deleted }

public class User
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    /// <summary>SĐT đã xác minh bằng OTP (không bắt buộc), dùng cho nút "Hiện số điện thoại".</summary>
    [BsonIgnoreIfNull] public string? Phone { get; set; }
    /// <summary>Tên đăng nhập (chữ thường), cho tài khoản đăng ký bằng mật khẩu.</summary>
    [BsonIgnoreIfNull] public string? Username { get; set; }
    [BsonIgnoreIfNull] public string? PasswordHash { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LoginLockedUntil { get; set; }
    public string DisplayName { get; set; } = default!;
    /// <summary>Họ tên tự khai, bắt buộc trước khi đăng tin đầu tiên (02 §3, T0).</summary>
    public string? FullName { get; set; }
    public string? ProvinceId { get; set; }
    public string? WardId { get; set; }
    public string? AvatarMediaId { get; set; }
    /// <summary>Email người dùng tự khai, dùng để gửi nhắc lịch chăm cây. Chưa có email thì không đặt được lời nhắc.</summary>
    [BsonIgnoreIfNull] public string? Email { get; set; }
    public bool HidePhone { get; set; }
    [BsonRepresentation(BsonType.String)] public UserStatus Status { get; set; } = UserStatus.Active;
    /// <summary>Hạn chế đăng tin đến thời điểm này (BR-MOD-04).</summary>
    public DateTime? PostingRestrictedUntil { get; set; }
    public DateTime? LockedUntil { get; set; }
    public int ActiveViolationPoints { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }

    public bool HasPostingProfile => !string.IsNullOrWhiteSpace(FullName) && !string.IsNullOrWhiteSpace(ProvinceId);
}

public record PublicUser(string Id, string DisplayName, string? AvatarMediaId, string? ProvinceId, DateTime CreatedAt)
{
    public static PublicUser From(User u) => new(u.Id, u.DisplayName, u.AvatarMediaId, u.ProvinceId, u.CreatedAt);
}
