using System.Globalization;
using System.Text;

namespace ChamXanh.Api.Common;

public static class VietnameseText
{
    /// <summary>Bỏ dấu, chữ thường, gộp khoảng trắng: "Sen Đá  Kim Tuyến" → "sen da kim tuyen".</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = true;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }

    public static string Slugify(string s) => Normalize(s).Replace(' ', '-');
}
