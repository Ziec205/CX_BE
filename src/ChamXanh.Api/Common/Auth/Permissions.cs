namespace ChamXanh.Api.Common.Auth;

// Ma trận phân quyền trang quản trị. Người dùng web chỉ có một vai trò (thành viên); mọi quyền dưới đây là của quản trị viên.
public static class Perm
{
    public const string ListingModerate = "listing.moderate";
    public const string UserSanction = "user.sanction";
    public const string UserBanPermanent = "user.ban_permanent";
    /// <summary>Xử lý ticket hỗ trợ khách hàng.</summary>
    public const string DisputeResolve = "dispute.resolve";
    public const string WalletAdjust = "wallet.adjust";
    public const string CatalogManage = "catalog.manage";
    public const string ContentManage = "content.manage";
    public const string PricingView = "pricing.view";
    public const string PricingEdit = "pricing.edit";
    public const string PricingApprove = "pricing.approve";
    public const string PromoManage = "promo.manage";
    public const string ModerationConfig = "moderation.config";
    public const string ReportsView = "reports.view";
    public const string AdminManage = "admin.manage";

    public static readonly string[] All =
    [
        ListingModerate, UserSanction, UserBanPermanent, DisputeResolve,
        WalletAdjust, CatalogManage, ContentManage, PricingView, PricingEdit, PricingApprove,
        PromoManage, ModerationConfig, ReportsView, AdminManage,
    ];
}

public static class AdminRoles
{
    public const string SuperAdmin = "SuperAdmin", Moderator = "Moderator",
        Support = "Support", Accountant = "Accountant", Editor = "Editor", Marketing = "Marketing";

    public static readonly IReadOnlyDictionary<string, string[]> Permissions = new Dictionary<string, string[]>
    {
        [SuperAdmin] = Perm.All,
        [Moderator] = [Perm.ListingModerate, Perm.UserSanction, Perm.ReportsView],
        [Support] = [Perm.DisputeResolve, Perm.PricingView, Perm.ReportsView],
        [Accountant] = [Perm.WalletAdjust, Perm.PricingView, Perm.ReportsView],
        [Editor] = [Perm.CatalogManage, Perm.ContentManage],
        [Marketing] = [Perm.PricingView, Perm.PricingEdit, Perm.PromoManage, Perm.ReportsView],
    };

    public static string[] Resolve(IEnumerable<string> roles) =>
        roles.SelectMany(r => Permissions.TryGetValue(r, out var p) ? p : []).Distinct().ToArray();
}
