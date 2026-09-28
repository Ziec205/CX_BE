using System.Security.Cryptography;
using System.Text;
using ChamXanh.Api.Common;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Identity;

public interface IOtpSender
{
    Task SendAsync(string phone, string code, CancellationToken ct);
}

/// <summary>Chỉ dùng khi phát triển: ghi mã OTP ra log. Production thay bằng SMS brandname / Zalo ZNS.</summary>
public class LogOtpSender(ILogger<LogOtpSender> logger) : IOtpSender
{
    public Task SendAsync(string phone, string code, CancellationToken ct)
    {
        logger.LogWarning("[DEV OTP] {Phone}: {Code}", PhoneNumber.Mask(phone), code);
        return Task.CompletedTask;
    }
}

public class OtpOptions
{
    public int CodeTtlMinutes { get; set; } = 5;
    public int MaxPerPhonePerHour { get; set; } = 5;
    public int MaxPerDevicePerDay { get; set; } = 10;
    public int MaxVerifyAttempts { get; set; } = 5;
    /// <summary>Chỉ bật ở môi trường dev/test: trả mã OTP trong response.</summary>
    public bool ExposeCodeInResponse { get; set; }
}

public class OtpCode
{
    [BsonId] public string Phone { get; set; } = default!;
    public string CodeHash { get; set; } = default!;
    public int Attempts { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class OtpRequestLog
{
    [BsonId] public MongoDB.Bson.ObjectId Id { get; set; }
    public string Phone { get; set; } = default!;
    public string? DeviceId { get; set; }
    public DateTime At { get; set; }
}

// BR-AUTH-03: OTP hết hạn 5 phút, tối đa 5 lần gửi / SĐT / giờ, 10 / thiết bị / ngày.
public class OtpService(IMongoDatabase db, IOtpSender sender, OtpOptions options, TimeProvider clock)
{
    readonly IMongoCollection<OtpCode> _codes = db.GetCollection<OtpCode>("otpCodes");
    readonly IMongoCollection<OtpRequestLog> _log = db.GetCollection<OtpRequestLog>("otpRequests");

    public async Task EnsureIndexesAsync()
    {
        await _codes.Indexes.CreateOneAsync(new CreateIndexModel<OtpCode>(
            Builders<OtpCode>.IndexKeys.Ascending(c => c.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
        await _log.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<OtpRequestLog>(Builders<OtpRequestLog>.IndexKeys.Ascending(l => l.At), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }),
            new CreateIndexModel<OtpRequestLog>(Builders<OtpRequestLog>.IndexKeys.Ascending(l => l.Phone).Ascending(l => l.At)),
            new CreateIndexModel<OtpRequestLog>(Builders<OtpRequestLog>.IndexKeys.Ascending(l => l.DeviceId).Ascending(l => l.At)),
        ]);
    }

    public async Task<string?> RequestAsync(string phone, string? deviceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var perPhone = await _log.CountDocumentsAsync(l => l.Phone == phone && l.At > now.AddHours(-1), cancellationToken: ct);
        if (perPhone >= options.MaxPerPhonePerHour) throw DomainException.TooMany("Bạn đã yêu cầu quá nhiều mã OTP, vui lòng thử lại sau 1 giờ");
        if (!string.IsNullOrEmpty(deviceId))
        {
            var perDevice = await _log.CountDocumentsAsync(l => l.DeviceId == deviceId && l.At > now.AddDays(-1), cancellationToken: ct);
            if (perDevice >= options.MaxPerDevicePerDay) throw DomainException.TooMany("Thiết bị đã yêu cầu quá nhiều mã OTP hôm nay");
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        await _codes.ReplaceOneAsync(c => c.Phone == phone,
            new OtpCode { Phone = phone, CodeHash = Hash(phone, code), ExpiresAt = now.AddMinutes(options.CodeTtlMinutes) },
            new ReplaceOptions { IsUpsert = true }, ct);
        await _log.InsertOneAsync(new OtpRequestLog { Phone = phone, DeviceId = deviceId, At = now }, cancellationToken: ct);
        await sender.SendAsync(phone, code, ct);
        return options.ExposeCodeInResponse ? code : null;
    }

    public async Task VerifyAsync(string phone, string code, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var doc = await _codes.FindOneAndUpdateAsync<OtpCode>(c => c.Phone == phone,
            Builders<OtpCode>.Update.Inc(c => c.Attempts, 1), new() { ReturnDocument = ReturnDocument.After }, ct);
        if (doc is null || doc.ExpiresAt < now) throw new DomainException("OTP_EXPIRED", "Mã OTP đã hết hạn, vui lòng yêu cầu mã mới");
        if (doc.Attempts > options.MaxVerifyAttempts)
        {
            await _codes.DeleteOneAsync(c => c.Phone == phone, ct);
            throw DomainException.TooMany("Nhập sai quá nhiều lần, vui lòng yêu cầu mã mới");
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(doc.CodeHash), Encoding.UTF8.GetBytes(Hash(phone, code ?? ""))))
            throw new DomainException("OTP_INVALID", "Mã OTP không đúng");
        await _codes.DeleteOneAsync(c => c.Phone == phone, ct);
    }

    static string Hash(string phone, string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{phone}:{code}")));
}
