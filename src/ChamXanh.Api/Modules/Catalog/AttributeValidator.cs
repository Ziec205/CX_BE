using System.Text.Json;

namespace ChamXanh.Api.Modules.Catalog;

public record AttributeError(string Key, string Message);

/// <summary>Kiểm tra giá trị thuộc tính của tin theo form động của danh mục, trả về giá trị đã chuẩn hóa.
/// Ô dạng chọn chỉ là gợi ý: người bán gõ giá trị riêng được, không bị gò theo danh sách có sẵn.</summary>
public static class AttributeValidator
{
    /// <summary>Danh mục cây sống chỉ bắt buộc hai thông tin cốt lõi; mọi ô đặc thù khác (loại lan, giống mai…) là tùy chọn.
    /// Vật tư, hạt giống giữ nguyên ô bắt buộc (vd: số giấy phép lưu hành phân bón).</summary>
    public static readonly IReadOnlySet<string> CoreRequiredKeys = new HashSet<string> { "tinhTrang", "chieuCao" };
    const int MaxTextLength = 200, MaxChoiceLength = 100, MaxChoices = 15;

    public static bool IsRequired(Category category, AttributeDefinition def) => def.Required && (!category.IsLivePlant || CoreRequiredKeys.Contains(def.Key));

    public static (Dictionary<string, object> Values, List<AttributeError> Errors) Validate(
        Category category, IReadOnlyDictionary<string, JsonElement>? input)
    {
        var values = new Dictionary<string, object>();
        var errors = new List<AttributeError>();
        input ??= new Dictionary<string, JsonElement>();

        foreach (var key in input.Keys.Where(k => category.Attributes.All(a => a.Key != k)))
            errors.Add(new(key, "Thuộc tính không thuộc danh mục này"));

        foreach (var def in category.Attributes)
        {
            if (!input.TryGetValue(def.Key, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (el.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(el.GetString()))
                || (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() == 0))
            {
                if (IsRequired(category, def)) errors.Add(new(def.Key, $"{def.Label} là bắt buộc"));
                continue;
            }

            switch (def.Type)
            {
                case AttributeType.Number:
                    if (el.ValueKind != JsonValueKind.Number || !el.TryGetDecimal(out var n)) { errors.Add(new(def.Key, $"{def.Label} phải là số")); break; }
                    if (def.Min is { } min && n < min) errors.Add(new(def.Key, $"{def.Label} tối thiểu {min}"));
                    else if (def.Max is { } max && n > max) errors.Add(new(def.Key, $"{def.Label} tối đa {max}"));
                    else values[def.Key] = (double)n; // lưu dạng số BSON (double) để lọc khoảng được
                    break;
                case AttributeType.Boolean:
                    if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) values[def.Key] = el.GetBoolean();
                    else errors.Add(new(def.Key, $"{def.Label} phải là có/không"));
                    break;
                case AttributeType.SingleSelect:
                    if (el.ValueKind != JsonValueKind.String) { errors.Add(new(def.Key, $"{def.Label} phải là chữ")); break; }
                    var choice = Canonical(def, el.GetString()!);
                    if (choice.Length > MaxChoiceLength) errors.Add(new(def.Key, $"{def.Label} tối đa {MaxChoiceLength} ký tự"));
                    else values[def.Key] = choice;
                    break;
                case AttributeType.MultiSelect:
                    if (el.ValueKind != JsonValueKind.Array) { errors.Add(new(def.Key, $"{def.Label} phải là danh sách")); break; }
                    if (el.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) { errors.Add(new(def.Key, $"{def.Label}: mỗi mục phải là chữ")); break; }
                    var picked = el.EnumerateArray().Select(x => Canonical(def, x.GetString()!)).Where(x => x.Length > 0).Distinct().ToList();
                    if (picked.Count > MaxChoices) errors.Add(new(def.Key, $"{def.Label} tối đa {MaxChoices} mục"));
                    else if (picked.Any(x => x.Length > MaxChoiceLength)) errors.Add(new(def.Key, $"{def.Label}: mỗi mục tối đa {MaxChoiceLength} ký tự"));
                    else values[def.Key] = picked;
                    break;
                default:
                    var text = el.ValueKind == JsonValueKind.String ? el.GetString()!.Trim() : el.ToString();
                    if (text.Length > MaxTextLength) errors.Add(new(def.Key, $"{def.Label} tối đa {MaxTextLength} ký tự"));
                    else values[def.Key] = text;
                    break;
            }
        }
        return (values, errors);
    }

    /// <summary>Gõ trùng một gợi ý (khác hoa/thường, thừa dấu cách) thì lưu đúng chữ của gợi ý, để bộ lọc Chợ cây vẫn khớp.</summary>
    static string Canonical(AttributeDefinition def, string raw)
    {
        var text = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return def.Options.FirstOrDefault(o => string.Equals(o, text, StringComparison.CurrentCultureIgnoreCase)) ?? text;
    }

    /// <summary>Có giá trị thuộc tính nào buộc duyệt tay không (vd: "Phôi rừng"). So không dấu vì người bán gõ tự do.</summary>
    public static bool NeedsManualReview(Category category, IReadOnlyDictionary<string, object> values) =>
        category.Attributes.Any(a => a.ManualReviewValues.Count > 0 && values.TryGetValue(a.Key, out var v) && v switch
        {
            string s => MatchesAny(s, a.ManualReviewValues),
            IEnumerable<string> list => list.Any(x => MatchesAny(x, a.ManualReviewValues)),
            _ => false,
        });

    static bool MatchesAny(string value, List<string> flagged)
    {
        var norm = Common.VietnameseText.Normalize(value);
        return flagged.Any(f => norm.Contains(Common.VietnameseText.Normalize(f)));
    }
}
