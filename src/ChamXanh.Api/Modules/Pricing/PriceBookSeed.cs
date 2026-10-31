namespace ChamXanh.Api.Modules.Pricing;

// Gói nạp Xu khởi tạo. Sau lần chạy đầu, admin chỉnh trên trang Bảng giá.
public static class PriceBookSeed
{
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
