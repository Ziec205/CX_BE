using System.Text.RegularExpressions;
using ChamXanh.Api.Common;

namespace ChamXanh.Api.Modules.Identity;

public static partial class PhoneNumber
{
    [GeneratedRegex(@"^(?:\+?84|0)([35789]\d{8})$")]
    private static partial Regex VnMobile();

    /// <summary>Chuẩn hóa số di động VN về dạng +84xxxxxxxxx.</summary>
    public static string Normalize(string raw)
    {
        var digits = new string((raw ?? "").Where(c => char.IsDigit(c) || c == '+').ToArray());
        var m = VnMobile().Match(digits);
        if (!m.Success) throw new DomainException("INVALID_PHONE", "Số điện thoại di động Việt Nam không hợp lệ", StatusCodes.Status400BadRequest);
        return "+84" + m.Groups[1].Value;
    }

    public static string Mask(string phone) => phone.Length < 7 ? phone : phone[..6] + "***" + phone[^3..];
}
