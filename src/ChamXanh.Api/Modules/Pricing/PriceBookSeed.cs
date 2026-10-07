namespace ChamXanh.Api.Modules.Pricing;

// Giá khởi tạo theo tài liệu 04 §3–4. Sau lần chạy đầu, admin chỉnh trên trang Bảng giá.
public static class PriceBookSeed
{
    public const string Standard = "STANDARD", Seedling = "SEEDLING", Premium = "PREMIUM";

    static Dictionary<string, long> P(long standard, long seedling, long premium) =>
        new() { [Standard] = standard, [Seedling] = seedling, [Premium] = premium };

    public static PriceBook Create(DateTime now) => new()
    {
        Id = "pb_1",
        Version = 1,
        Status = PriceBookStatus.Active,
        EffectiveFrom = now,
        CreatedBy = "system",
        ApprovedBy = "system",
        CreatedAt = now,
        ApprovedAt = now,
        ChangeNote = "Bảng giá khởi tạo",
        PriceGroups =
        [
            new() { Code = Standard, Name = "Phổ thông", CategoryIds = ["noi-that", "sen-da", "cay-leo", "hoa-kieng", "thuy-sinh", "chau", "dat-gia-the", "phan-bon", "dung-cu", "tuoi", "ke-gian",
                "cay-mini", "cay-ngoai-troi", "cay-hang-rao", "cay-canh-khac", "lan", "hoa-hong", "hoa-giay", "hoa-tet", "lan-hoa-khac", "vat-tu-khac"] },
            new() { Code = Seedling, Name = "Cây giống", CategoryIds = ["giong-an-qua", "giong-lam-nghiep", "hat-giong", "an-qua-truong-thanh", "rau-gia-vi", "duoc-lieu", "giong-khac"] },
            new() { Code = Premium, Name = "Giá trị cao", CategoryIds = ["bonsai-mini", "bonsai-trung-dai", "cay-the", "cong-trinh", "mai-vang", "tieu-canh", "gia-tri-cao-khac"] },
        ],
        ListingServices =
        [
            new() { Code = ListingServiceCodes.Bump, Prices = P(10, 12, 25) },
            new() { Code = ListingServiceCodes.AutoBump, Days = 7, PerDay = 3, Prices = P(120, 150, 300) },
            new() { Code = ListingServiceCodes.Priority, Days = 3, Prices = P(70, 90, 180) },
            new() { Code = ListingServiceCodes.Priority, Days = 7, Prices = P(140, 180, 350) },
            new() { Code = ListingServiceCodes.Priority, Days = 14, Prices = P(240, 300, 600) },
            new() { Code = ListingServiceCodes.Label, Days = 7, LabelName = "Hàng mới về", Prices = P(15, 15, 20) },
        ],
        GardenPlans = [new() { Months = 1, PriceVnd = 199_000 }, new() { Months = 3, PriceVnd = 499_000 }, new() { Months = 12, PriceVnd = 1_690_000 }],
        TopUpPackages =
        [
            new() { Code = "MAM", Name = "Mầm", PriceVnd = 50_000, Xu = 50, BonusXu = 0 },
            new() { Code = "CHOI", Name = "Chồi", PriceVnd = 100_000, Xu = 100, BonusXu = 5 },
            new() { Code = "CAY", Name = "Cây", PriceVnd = 200_000, Xu = 200, BonusXu = 15, Popular = true },
            new() { Code = "VUON", Name = "Vườn", PriceVnd = 500_000, Xu = 500, BonusXu = 50 },
            new() { Code = "RUNG", Name = "Rừng", PriceVnd = 1_000_000, Xu = 1_000, BonusXu = 120 },
        ],
    };
}
