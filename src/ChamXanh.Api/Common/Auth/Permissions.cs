namespace ChamXanh.Api.Common.Auth;

// Ma trận phân quyền — tài liệu 02 §4.1.
public static class Perm
{
    public const string ListingModerate = "listing.moderate";
    public const string UserSanction = "user.sanction";
    public const string UserBanPermanent = "user.ban_permanent";
    public const string KycView = "kyc.view";
    public const string GardenVerify = "garden.verify";
    public const string DisputeResolve = "dispute.resolve";
    public const string PayoutApprove = "payout.approve";
    public const string WalletAdjust = "wallet.adjust";
    public const string CatalogManage = "catalog.manage";
    public const string ContentManage = "content.manage";
    public const string ListingProxyPost = "listing.proxy_post";
    public const string PricingView = "pricing.view";
    public const string PricingEdit = "pricing.edit";
    public const string PricingApprove = "pricing.approve";
    public const string PromoManage = "promo.manage";
    public const string ModerationConfig = "moderation.config";
    public const string ReportsView = "reports.view";
    public const string AdminManage = "admin.manage";

    public static readonly string[] All =
    [
        ListingModerate, UserSanction, UserBanPermanent, KycView, GardenVerify, DisputeResolve, PayoutApprove,
        WalletAdjust, CatalogManage, ContentManage, ListingProxyPost, PricingView, PricingEdit, PricingApprove,
        PromoManage, ModerationConfig, ReportsView, AdminManage,
    ];
}

public static class AdminRoles
{
    public const string SuperAdmin = "SuperAdmin", Moderator = "Moderator", Verification = "Verification",
        Support = "Support", Accountant = "Accountant", Editor = "Editor", FieldSales = "FieldSales", Marketing = "Marketing";

    public static readonly IReadOnlyDictionary<string, string[]> Permissions = new Dictionary<string, string[]>
    {
        [SuperAdmin] = Perm.All,
        [Moderator] = [Perm.ListingModerate, Perm.UserSanction, Perm.ReportsView],
        [Verification] = [Perm.KycView, Perm.GardenVerify],
        [Support] = [Perm.DisputeResolve, Perm.PricingView, Perm.ReportsView],
        [Accountant] = [Perm.PayoutApprove, Perm.WalletAdjust, Perm.PricingView, Perm.ReportsView],
        [Editor] = [Perm.CatalogManage, Perm.ContentManage],
        [FieldSales] = [Perm.ListingProxyPost, Perm.PricingView],
        [Marketing] = [Perm.PricingView, Perm.PricingEdit, Perm.PromoManage, Perm.ReportsView],
    };

    public static string[] Resolve(IEnumerable<string> roles) =>
        roles.SelectMany(r => Permissions.TryGetValue(r, out var p) ? p : []).Distinct().ToArray();
}
