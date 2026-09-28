using System.Text.RegularExpressions;
using ChamXanh.Api.Modules.Catalog;

namespace ChamXanh.Api.Modules.Listings;

public record RuleError(string Field, string Message);

/// <summary>Các quy tắc thuần của tin đăng (tài liệu 03 §1–3), không truy cập DB để dễ kiểm thử.</summary>
public static partial class ListingRules
{
    [GeneratedRegex(@"(?:\+?84|0)[\s.\-]?[35789](?:[\s.\-]?\d){8}")]
    private static partial Regex PhoneLike();

    [GeneratedRegex(@"(https?://|www\.|\b[a-z0-9-]+\.(com|vn|net|org|me|io|xyz)\b|zalo\.me|m\.me/|fb\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkLike();

    public static bool ContainsContactInfo(string text) => PhoneLike().IsMatch(text) || LinkLike().IsMatch(text);

    public static List<RuleError> ValidateText(string? title, string? description)
    {
        var errors = new List<RuleError>();
        title = title?.Trim() ?? "";
        description = description?.Trim() ?? "";
        if (title.Length is < 10 or > 70) errors.Add(new("title", "Tiêu đề dài 10–70 ký tự"));
        var letters = title.Where(char.IsLetter).ToList();
        if (letters.Count >= 6 && letters.All(char.IsUpper)) errors.Add(new("title", "Tiêu đề không được viết hoa toàn bộ"));
        if (description.Length is < 20 or > 3000) errors.Add(new("description", "Mô tả dài 20–3.000 ký tự"));
        // BR-LST-11: SĐT hiển thị qua nút "Hiện số", không để trong nội dung.
        if (ContainsContactInfo(title)) errors.Add(new("title", "Tiêu đề không được chứa số điện thoại hoặc đường link"));
        if (ContainsContactInfo(description)) errors.Add(new("description", "Mô tả không được chứa số điện thoại hoặc đường link"));
        return errors;
    }

    public static (int Min, int Max) MediaBounds(Category category, ListingType type) =>
        type == ListingType.Buy ? (0, 12) : category.IsLivePlant ? (3, 12) : (1, 12);

    public record PriceInput(long? Price, PriceMode Mode, bool Negotiable, long? RefMin, long? RefMax,
        long? BudgetMin, long? BudgetMax, RentTerms? Rent, int Quantity);

    public static List<RuleError> ValidatePrice(ListingType type, Category category, PriceInput p)
    {
        var errors = new List<RuleError>();
        if (p.Quantity is < 1 or > 1_000_000) errors.Add(new("quantity", "Số lượng từ 1 đến 1.000.000"));
        switch (type)
        {
            case ListingType.Sell:
                if (p.Mode == PriceMode.Negotiable)
                {
                    // BR-LST-05: chỉ danh mục bonsai/cây thế/mai vàng; bắt buộc khoảng giá tham khảo.
                    if (!category.AllowNegotiablePrice) errors.Add(new("priceMode", "Danh mục này không cho phép \"Giá thỏa thuận\""));
                    if (p.RefMin is not > 0 || p.RefMax is not > 0 || p.RefMin > p.RefMax)
                        errors.Add(new("priceRef", "\"Giá thỏa thuận\" phải kèm khoảng giá tham khảo hợp lệ"));
                }
                else if (p.Price is not > 0) errors.Add(new("price", "Tin bán phải có giá > 0"));
                break;
            case ListingType.Buy:
                if (p.BudgetMin < 0 || p.BudgetMax < 0 || (p.BudgetMin is { } a && p.BudgetMax is { } b && a > b))
                    errors.Add(new("budget", "Ngân sách không hợp lệ"));
                break;
            case ListingType.Rent:
                if (p.Rent is null || p.Rent.PricePerUnit <= 0) errors.Add(new("rent", "Tin cho thuê phải có giá thuê > 0"));
                else if (p.Rent.Deposit < 0 || p.Rent.MinUnits < 1) errors.Add(new("rent", "Tiền cọc/thời gian thuê tối thiểu không hợp lệ"));
                break;
            case ListingType.Give:
                if (p.Price is > 0) errors.Add(new("price", "Tin tặng/trao đổi phải có giá 0đ")); // BR-GIV-01
                break;
        }
        return errors;
    }

    /// <summary>BR-LST-09: mọi ảnh chụp trong app và ảnh cũ nhất ≤ N ngày tính đến hiện tại (tính khi đọc).</summary>
    public static bool HasRealPhotoBadge(IReadOnlyCollection<ListingMediaRef> media, DateTime now, int maxAgeDays) =>
        media.Count > 0 && media.All(m => m.CapturedInApp && m.CapturedAt is { } at && at >= now.AddDays(-maxAgeDays));

    public static DateTime? OldestPhotoAt(IReadOnlyCollection<ListingMediaRef> media) =>
        media.Count == 0 || media.Any(m => m.CapturedAt is null) ? null : media.Min(m => m.CapturedAt);

    /// <summary>BR-LST-12: nhãn "Bán chuyên" chỉ tính tin Bán + Cho thuê đang hiển thị, không áp khi đã có gói.</summary>
    public static bool IsProSeller(long activeSellOrRent, bool hasActivePlan, int threshold) => !hasActivePlan && activeSellOrRent > threshold;

    public static DateTime ComputeExpiry(DateTime now, ListingOptions o) =>
        (o.LaunchDate is { } launch && now < launch ? launch : now).AddDays(o.ExpiryDays);

    public static bool IsPubliclyVisible(ListingStatus s) => s is ListingStatus.Active or ListingStatus.SoldOut;
}
