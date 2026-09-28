using System.Text.Json;

namespace ChamXanh.Api.Modules.Catalog;

public record AttributeError(string Key, string Message);

/// <summary>Kiểm tra giá trị thuộc tính của tin theo form động của danh mục, trả về giá trị đã chuẩn hóa.</summary>
public static class AttributeValidator
{
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
                if (def.Required) errors.Add(new(def.Key, $"{def.Label} là bắt buộc"));
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
                    if (el.ValueKind == JsonValueKind.String && def.Options.Contains(el.GetString()!)) values[def.Key] = el.GetString()!;
                    else errors.Add(new(def.Key, $"{def.Label}: giá trị không hợp lệ"));
                    break;
                case AttributeType.MultiSelect:
                    if (el.ValueKind != JsonValueKind.Array) { errors.Add(new(def.Key, $"{def.Label} phải là danh sách")); break; }
                    var picked = el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").ToList();
                    if (picked.Any(p => !def.Options.Contains(p))) errors.Add(new(def.Key, $"{def.Label}: có giá trị không hợp lệ"));
                    else values[def.Key] = picked.Distinct().ToList();
                    break;
                default:
                    var text = el.ValueKind == JsonValueKind.String ? el.GetString()!.Trim() : el.ToString();
                    if (text.Length > 200) errors.Add(new(def.Key, $"{def.Label} tối đa 200 ký tự"));
                    else values[def.Key] = text;
                    break;
            }
        }
        return (values, errors);
    }

    /// <summary>Có giá trị thuộc tính nào buộc duyệt tay không (vd: "Phôi rừng").</summary>
    public static bool NeedsManualReview(Category category, IReadOnlyDictionary<string, object> values) =>
        category.Attributes.Any(a => a.ManualReviewValues.Count > 0 && values.TryGetValue(a.Key, out var v) && v switch
        {
            string s => a.ManualReviewValues.Contains(s),
            IEnumerable<string> list => list.Any(a.ManualReviewValues.Contains),
            _ => false,
        });
}
