using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Catalog;
using MongoDB.Bson.Serialization.Attributes;

namespace ChamXanh.Api.Modules.Moderation;

/// <summary>Cấu hình kiểm duyệt, admin có quyền moderation.config chỉnh được (03 §4.2–4.3).</summary>
public class ModerationConfig
{
    [BsonId] public string Id { get; set; } = "default";
    /// <summary>Khớp chính xác thì từ chối tự động, KHÔNG cộng điểm (sửa L09).</summary>
    public List<string> BannedTerms { get; set; } =
        ["can sa", "canabis", "cannabis", "thuoc phien", "anh tuc", "cay coca", "thuoc bvtv", "thuoc tru sau", "thuoc diet co",
         "bung rung", "lan rung tu nhien", "go sua do"];
    /// <summary>Từ ngữ phủ định đứng ngay trước từ cấm thì chuyển duyệt tay thay vì từ chối (vd "không phải lan rừng").</summary>
    public List<string> NegationPrefixes { get; set; } = ["khong phai", "khong co", "khong ban", "khong", "ko phai", "k phai"];
    public Dictionary<string, int> RiskyTerms { get; set; } = new()
    {
        ["phoi rung"] = 30, ["lan rung"] = 25, ["cay rung"] = 20, ["coc truoc"] = 20, ["chuyen khoan truoc"] = 20,
        ["ship cod khong kiem"] = 15, ["hoa hong xanh"] = 25, ["hoa hong cau vong"] = 25, ["sen da 7 mau"] = 25, ["hat giong cau vong"] = 25,
    };
    public int WeightNewAccount { get; set; } = 15;
    public int WeightIndividualHighValue { get; set; } = 20;
    public int WeightRecentRejection { get; set; } = 10;
    public int WeightPriceDeviation { get; set; } = 20;
    public int WeightForeignPhoto { get; set; } = 30;
    public int WeightHighPrice { get; set; } = 20;
    public int TrustedGardenBonus { get; set; } = -20;
}

public record RiskContext(
    string Title, string Description, Category Category, Species? Species, IReadOnlyDictionary<string, object> Attributes,
    long? EffectivePrice, long? SpeciesMedianPrice, bool IsNewAccount, bool IsIndividual, bool IsTrustedGarden,
    int RecentRejections, bool HasForeignPhoto, long HighValueThreshold, long ManualReviewPrice);

public enum RiskRoute { AutoReject, ManualPriority, Manual, AutoApprove }

public record RiskResult(int Score, List<string> Signals, RiskRoute Route, string? RejectReason);

public static class RiskScorer
{
    public static RiskResult Evaluate(RiskContext c, ModerationConfig cfg, int autoApproveBelow, int priorityFrom)
    {
        var signals = new List<string>();
        var text = " " + VietnameseText.Normalize($"{c.Title} {c.Description}") + " ";

        // 1. Bộ lọc cứng
        if (c.Species?.LegalFlag == LegalFlag.Banned)
            return new(100, ["banned_species"], RiskRoute.AutoReject, $"Loài \"{c.Species.CommonName}\" bị cấm mua bán");
        var bannedHit = cfg.BannedTerms.FirstOrDefault(t => text.Contains($" {t} "));
        var ambiguous = false;
        if (bannedHit is not null)
        {
            ambiguous = cfg.NegationPrefixes.Any(n => text.Contains($" {n} {bannedHit} "));
            if (!ambiguous) return new(100, [$"banned_term:{bannedHit}"], RiskRoute.AutoReject, "Tin chứa hàng hóa bị cấm đăng");
            signals.Add($"ambiguous_banned_term:{bannedHit}");
        }

        // 2. Tuyến bắt buộc duyệt tay (sửa L08)
        var mandatory = ambiguous;
        if (c.Category.RequiresManualReview) { mandatory = true; signals.Add("restricted_category"); }
        if (c.Species?.LegalFlag is LegalFlag.Restricted or LegalFlag.InvasiveAlien) { mandatory = true; signals.Add($"species_{c.Species.LegalFlag}"); }
        if (AttributeValidator.NeedsManualReview(c.Category, c.Attributes)) { mandatory = true; signals.Add("attribute_manual_review"); }
        if (c.EffectivePrice >= c.ManualReviewPrice) { mandatory = true; signals.Add("price_over_manual_threshold"); }

        // 3. Chấm điểm
        var score = 0;
        void Add(int w, string s) { score += w; signals.Add(s); }
        if (c.IsNewAccount) Add(cfg.WeightNewAccount, "new_account");
        if (c.IsIndividual && c.EffectivePrice > c.HighValueThreshold) Add(cfg.WeightIndividualHighValue, "individual_high_value");
        if (c.RecentRejections > 0) Add(cfg.WeightRecentRejection * c.RecentRejections, $"recent_rejections:{c.RecentRejections}");
        if (c.EffectivePrice is > 0 && c.SpeciesMedianPrice is > 0
            && Math.Abs(c.EffectivePrice.Value - c.SpeciesMedianPrice.Value) * 2 > c.SpeciesMedianPrice.Value)
            Add(cfg.WeightPriceDeviation, "price_deviation");
        foreach (var (term, w) in cfg.RiskyTerms.Where(t => text.Contains($" {t.Key} ")))
            Add(w, $"risky_term:{term}");
        if (c.HasForeignPhoto) Add(cfg.WeightForeignPhoto, "photo_matches_other_seller");
        if (c.EffectivePrice >= c.ManualReviewPrice) Add(cfg.WeightHighPrice, "high_price");
        if (c.IsTrustedGarden) Add(cfg.TrustedGardenBonus, "trusted_garden");
        score = Math.Clamp(score, 0, 100);

        var route = score >= priorityFrom ? RiskRoute.ManualPriority
            : mandatory || score >= autoApproveBelow ? RiskRoute.Manual
            : RiskRoute.AutoApprove;
        return new(score, signals, route, null);
    }
}
