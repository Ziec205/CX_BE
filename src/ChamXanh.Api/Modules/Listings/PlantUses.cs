namespace ChamXanh.Api.Modules.Listings;

/// <summary>Danh sách công dụng cố định để lọc Chợ cây. FE giữ bản sao trong lib/plantUses.ts — sửa cả hai nơi.</summary>
public static class PlantUses
{
    public const int MaxPerListing = 5;

    public static readonly HashSet<string> All =
    [
        "Trang trí nội thất", "Để bàn làm việc", "Lọc không khí", "Phong thủy", "Làm quà tặng", "Chơi Tết",
        "Trồng ban công", "Sân vườn, cảnh quan", "Bóng mát", "Hàng rào", "Cho quả", "Rau, gia vị",
        "Dược liệu", "Hoa thơm", "Thủy sinh", "Sưu tầm", "An toàn thú cưng", "Dễ chăm cho người mới",
    ];
}
