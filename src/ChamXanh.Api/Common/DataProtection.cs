using System.Security.Cryptography;
using System.Text;

namespace ChamXanh.Api.Common;

public class SecurityOptions
{
    /// <summary>Khóa AES-256 (base64, 32 byte) mã hóa CCCD, số tài khoản… ở tầng ứng dụng (BR-AUTH-04).</summary>
    public string DataKey { get; set; } = "";
    /// <summary>Khóa HMAC (base64) để kiểm tra trùng CCCD mà không lưu số thật (sửa L11).</summary>
    public string HmacKey { get; set; } = "";
}

public class DataProtector
{
    readonly byte[] _key;
    readonly byte[] _hmacKey;

    public DataProtector(SecurityOptions o)
    {
        _key = Decode(o.DataKey, nameof(o.DataKey));
        _hmacKey = Decode(o.HmacKey, nameof(o.HmacKey));
        if (_key.Length != 32) throw new InvalidOperationException("Security:DataKey phải là 32 byte (base64)");
    }

    static byte[] Decode(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Thiếu cấu hình Security:{name}");
        return Convert.FromBase64String(value);
    }

    /// <summary>AES-GCM: nonce(12) | tag(16) | ciphertext, mã hóa base64.</summary>
    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Decrypt(string encoded)
    {
        var all = Convert.FromBase64String(encoded);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    public string Hmac(string value) => Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(value)));

    public static string MaskTail(string value, int visible = 3) =>
        value.Length <= visible ? value : new string('•', value.Length - visible) + value[^visible..];
}
