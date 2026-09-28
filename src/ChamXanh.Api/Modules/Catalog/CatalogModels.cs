using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Catalog;

public enum AttributeType { Text, Number, SingleSelect, MultiSelect, Boolean }

/// <summary>Định nghĩa một trường của form động (tài liệu 03 §1.3).</summary>
public class AttributeDefinition
{
    public string Key { get; set; } = default!;
    public string Label { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public AttributeType Type { get; set; }
    public string? Unit { get; set; }
    public List<string> Options { get; set; } = [];
    public bool Required { get; set; }
    public bool Filterable { get; set; }
    public bool ShowOnCard { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int Order { get; set; }
    /// <summary>Giá trị khớp thì tin bắt buộc duyệt tay (vd: nguồn gốc = "Phôi rừng").</summary>
    public List<string> ManualReviewValues { get; set; } = [];
}

public class Category
{
    [BsonId] public string Id { get; set; } = default!; // slug, vd "bonsai-mini"
    public string? ParentId { get; set; }
    public string Name { get; set; } = default!;
    public int Level { get; set; }
    public int Order { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>Danh mục cây sống: bắt buộc chọn loài, tình trạng cây, chiều cao, tối thiểu 3 ảnh.</summary>
    public bool IsLivePlant { get; set; }
    /// <summary>Hàng hạn chế: mọi tin phải duyệt tay (03 §4.3, vd phân bón).</summary>
    public bool RequiresManualReview { get; set; }
    /// <summary>Cho phép chọn "Giá thỏa thuận" (BR-LST-05).</summary>
    public bool AllowNegotiablePrice { get; set; }
    public List<AttributeDefinition> Attributes { get; set; } = [];
}

public enum LegalFlag { None, Restricted, InvasiveAlien, Banned }

public class PriceRef
{
    public string SizeBand { get; set; } = default!; // "<30", "30-80", ">80" (cm)
    public long MedianVnd { get; set; }
    public int SampleSize { get; set; }
}

public class Species
{
    [BsonId] public string Id { get; set; } = default!; // slug
    public string CommonName { get; set; } = default!;
    public List<string> Aliases { get; set; } = [];
    public List<string> SearchTerms { get; set; } = []; // tên + bí danh đã bỏ dấu
    public string? ScientificName { get; set; }
    public string? Family { get; set; }
    public List<string> CategoryIds { get; set; } = [];
    public string? Light { get; set; }
    public string? Water { get; set; }
    public int? Difficulty { get; set; }
    public string? PetToxicity { get; set; }
    public string? Description { get; set; }
    [BsonRepresentation(BsonType.String)] public LegalFlag LegalFlag { get; set; }
    public string? LegalBasis { get; set; }
    public List<PriceRef> PriceRefs { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}

public class PlantCollection
{
    [BsonId] public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public List<string> SpeciesIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public bool IsSeasonal { get; set; }
    public DateTime? ActiveFrom { get; set; }
    public DateTime? ActiveTo { get; set; }
}
