namespace ChamXanh.Api.Modules.Catalog;

// Cây danh mục khởi tạo theo tài liệu 03 §1. Id trùng với nhóm giá trong PriceBookSeed.
public static class CatalogSeed
{
    static AttributeDefinition Num(string key, string label, string unit, bool required = false, decimal? min = 0, decimal? max = null, bool filter = true, int order = 0) =>
        new() { Key = key, Label = label, Type = AttributeType.Number, Unit = unit, Required = required, Min = min, Max = max, Filterable = filter, ShowOnCard = required, Order = order };

    static AttributeDefinition Select(string key, string label, bool required, params string[] options) =>
        new() { Key = key, Label = label, Type = AttributeType.SingleSelect, Options = [.. options], Required = required, Filterable = true };

    static readonly AttributeDefinition PlantCondition = Select("tinhTrang", "Tình trạng cây", true, "Trồng chậu", "Bầu đất", "Rễ trần", "Cành giâm/chiết", "Hạt");
    static AttributeDefinition Height(bool required = true) => Num("chieuCao", "Chiều cao", "cm", required, 0, 5000, order: 1);

    static Category Leaf(string id, string parent, string name, bool live, params AttributeDefinition[] extra) => new()
    {
        Id = id, ParentId = parent, Name = name, Level = 2, IsLivePlant = live,
        Attributes = live ? [PlantCondition, Height(), .. extra] : [.. extra],
    };

    public static List<Category> Create()
    {
        var bonsaiAttrs = new[]
        {
            Select("dangThe", "Dáng thế", false, "Trực", "Xiêu", "Hoành", "Huyền", "Văn nhân", "Bạt phong", "Song thụ", "Tam đa", "Phụ tử", "Bonsai lũa", "Khác"),
            Num("tuoi", "Tuổi ước tính", "năm"),
            Num("hoanhGoc", "Hoành gốc", "cm"),
            Num("ngangTan", "Chiều ngang tán", "cm"),
            new AttributeDefinition
            {
                Key = "nguonGoc", Label = "Nguồn gốc", Type = AttributeType.SingleSelect, Filterable = true,
                Options = ["Tự tạo từ phôi vườn", "Nhập khẩu", "Sưu tầm", "Phôi rừng"], ManualReviewValues = ["Phôi rừng"],
            },
        };

        var list = new List<Category>
        {
            new() { Id = "cay-canh", Name = "Cây cảnh & nội thất", Level = 1, Order = 1 },
            new() { Id = "gia-tri-cao", Name = "Bonsai & cây giá trị cao", Level = 1, Order = 2 },
            new() { Id = "giong-an-qua-root", Name = "Cây giống & cây ăn quả", Level = 1, Order = 3 },
            new() { Id = "vat-tu", Name = "Vật tư làm vườn", Level = 1, Order = 5 },
            new() { Id = "lan-hoa", Name = "Lan & hoa", Level = 1, Order = 4 },

            Leaf("cay-mini", "cay-canh", "Cây mini để bàn", true,
                new AttributeDefinition { Key = "kemChau", Label = "Kèm chậu", Type = AttributeType.Boolean, Filterable = true }),
            Leaf("cay-ngoai-troi", "cay-canh", "Cây ban công, sân vườn", true,
                Select("anhSang", "Ánh sáng", false, "Chịu bóng", "Bán râm", "Cần nắng")),
            Leaf("cay-hang-rao", "cay-canh", "Cây hàng rào, phủ nền", true),

            Leaf("lan", "lan-hoa", "Lan các loại", true,
                Select("loaiLan", "Loại lan", false, "Hồ điệp", "Dendrobium", "Cattleya", "Vanda", "Lan kiếm (Cymbidium)", "Phi điệp", "Lan rừng gây trồng", "Khác"),
                Select("trangThaiHoa", "Trạng thái hoa", false, "Đang nở", "Có nụ", "Chưa ra hoa"),
                Select("giaThe", "Giá thể", false, "Dớn", "Vỏ thông", "Than củi", "Gắn gỗ/lũa", "Khác")),
            Leaf("hoa-hong", "lan-hoa", "Hoa hồng", true,
                Select("dangHong", "Dạng cây", false, "Hồng bụi", "Hồng leo", "Hồng thân gỗ", "Hồng mini")),
            Leaf("hoa-giay", "lan-hoa", "Hoa giấy", true),
            Leaf("hoa-tet", "lan-hoa", "Hoa Tết, hoa theo mùa", true,
                Select("trangThaiHoa", "Trạng thái hoa", false, "Đang nở", "Có nụ", "Chưa ra hoa")),

            Leaf("tieu-canh", "gia-tri-cao", "Tiểu cảnh, non bộ", false,
                Num("dai", "Chiều dài", "cm"), Num("rong", "Chiều rộng", "cm")),

            Leaf("duoc-lieu", "giong-an-qua-root", "Cây dược liệu, thuốc nam", true),

            Leaf("noi-that", "cay-canh", "Cây nội thất / văn phòng", true,
                Select("anhSang", "Ánh sáng", false, "Chịu bóng", "Bán râm", "Cần nắng"),
                Num("dkChau", "Đường kính chậu", "cm"),
                new AttributeDefinition { Key = "kemChau", Label = "Kèm chậu", Type = AttributeType.Boolean, Filterable = true }),
            Leaf("sen-da", "cay-canh", "Sen đá & xương rồng", true,
                new AttributeDefinition { Key = "ghep", Label = "Cây ghép", Type = AttributeType.Boolean },
                new AttributeDefinition { Key = "dotBien", Label = "Đột biến (variegated)", Type = AttributeType.Boolean, Filterable = true }),
            Leaf("cay-leo", "cay-canh", "Cây leo, cây treo", true),
            Leaf("hoa-kieng", "cay-canh", "Hoa & cây kiểng", true),
            Leaf("thuy-sinh", "cay-canh", "Cây thủy sinh & terrarium", true),

            Leaf("bonsai-mini", "gia-tri-cao", "Bonsai mini (< 30cm)", true, bonsaiAttrs),
            Leaf("bonsai-trung-dai", "gia-tri-cao", "Bonsai trung/đại", true, bonsaiAttrs),
            Leaf("cay-the", "gia-tri-cao", "Cây thế, cây phôi", true, bonsaiAttrs),
            Leaf("cong-trinh", "gia-tri-cao", "Cây công trình, cây bóng mát", true,
                Num("dkThan", "Đường kính thân (đo ở 1,3m)", "cm"), Num("dkBau", "Đường kính bầu", "cm"),
                new AttributeDefinition { Key = "coVat", Label = "Có hóa đơn VAT", Type = AttributeType.Boolean, Filterable = true }),
            Leaf("mai-vang", "gia-tri-cao", "Mai vàng", true,
                Select("giongMai", "Giống mai", false, "Mai giảo", "Mai cánh", "Mai Yên Tử", "Mai ghép", "Khác"),
                Num("hoanhGoc", "Hoành gốc", "cm")),

            Leaf("giong-an-qua", "giong-an-qua-root", "Cây giống ăn quả", true,
                new AttributeDefinition { Key = "giong", Label = "Giống / cultivar", Type = AttributeType.Text, Filterable = false },
                Select("nhanGiong", "Phương pháp nhân giống", false, "Ghép", "Chiết", "Giâm", "Gieo hạt", "Nuôi cấy mô"),
                Num("tuoiThang", "Tuổi cây giống", "tháng")),
            Leaf("giong-lam-nghiep", "giong-an-qua-root", "Cây giống lâm nghiệp", true),
            new() { Id = "hat-giong", ParentId = "giong-an-qua-root", Name = "Hạt giống", Level = 2,
                Attributes = [
                    new() { Key = "giong", Label = "Giống", Type = AttributeType.Text, Required = true },
                    Num("tyLeNayMam", "Tỉ lệ nảy mầm công bố", "%", max: 100, filter: false),
                ] },
            Leaf("an-qua-truong-thanh", "giong-an-qua-root", "Cây ăn quả trưởng thành", true),
            Leaf("rau-gia-vi", "giong-an-qua-root", "Rau, gia vị giống", true),

            Leaf("chau", "vat-tu", "Chậu", false,
                Select("chatLieu", "Chất liệu", true, "Xi măng", "Đất nung", "Gốm/sứ", "Nhựa", "Composite", "Đá mài"),
                Num("dkMieng", "Đường kính miệng", "cm", true), Num("chieuCaoChau", "Chiều cao", "cm")),
            Leaf("dat-gia-the", "vat-tu", "Đất & giá thể", false),
            new() { Id = "phan-bon", ParentId = "vat-tu", Name = "Phân bón", Level = 2, RequiresManualReview = true,
                Attributes = [
                    Select("loaiPhan", "Loại", true, "Hữu cơ", "Vô cơ", "Vi sinh"),
                    new() { Key = "soGiayPhep", Label = "Số giấy chứng nhận / quyết định lưu hành", Type = AttributeType.Text, Required = true },
                ] },
            Leaf("dung-cu", "vat-tu", "Dụng cụ", false),
            Leaf("tuoi", "vat-tu", "Tưới tự động", false),
            Leaf("ke-gian", "vat-tu", "Kệ, giàn, lưới", false),
        };
        foreach (var id in new[] { "bonsai-mini", "bonsai-trung-dai", "cay-the", "mai-vang" })
            list.First(c => c.Id == id).AllowNegotiablePrice = true;
        return list;
    }

    public static List<Species> Species() =>
    [
        Sp("kim-tien", "Kim tiền", "Zamioculcas zamiifolia", "Araceae", ["noi-that"], ["Kim phát tài", "ZZ plant"], light: "Chịu bóng", difficulty: 1, toxic: "Độc nhẹ với chó mèo"),
        Sp("luoi-ho", "Lưỡi hổ", "Dracaena trifasciata", "Asparagaceae", ["noi-that"], ["Hổ vĩ", "Sansevieria"], light: "Chịu bóng", difficulty: 1, toxic: "Độc nhẹ với chó mèo"),
        Sp("trau-ba", "Trầu bà", "Epipremnum aureum", "Araceae", ["noi-that", "cay-leo"], ["Pothos"], light: "Chịu bóng", difficulty: 1, toxic: "Độc với chó mèo"),
        Sp("monstera", "Trầu bà lá xẻ", "Monstera deliciosa", "Araceae", ["noi-that"], ["Monstera"], light: "Bán râm", difficulty: 2, toxic: "Độc với chó mèo"),
        Sp("sen-da-kim-tuyen", "Sen đá kim tuyến", "Echeveria sp.", "Crassulaceae", ["sen-da"], [], light: "Cần nắng", difficulty: 2),
        Sp("xuong-rong-tai-tho", "Xương rồng tai thỏ", "Opuntia microdasys", "Cactaceae", ["sen-da"], [], light: "Cần nắng", difficulty: 1),
        Sp("mai-vang", "Mai vàng", "Ochna integerrima", "Ochnaceae", ["mai-vang", "bonsai-mini", "bonsai-trung-dai", "cay-the"], ["Mai"], light: "Cần nắng", difficulty: 3),
        Sp("sanh", "Sanh", "Ficus benjamina", "Moraceae", ["bonsai-mini", "bonsai-trung-dai", "cay-the"], ["Si"], light: "Cần nắng", difficulty: 2),
        Sp("nguyet-que", "Nguyệt quế", "Murraya paniculata", "Rutaceae", ["bonsai-mini", "bonsai-trung-dai", "cay-the", "hoa-kieng"], ["Nguyệt quý"], light: "Cần nắng", difficulty: 2),
        Sp("quat", "Quất cảnh", "Citrus japonica", "Rutaceae", ["hoa-kieng", "an-qua-truong-thanh"], ["Tắc", "Kim quất"], light: "Cần nắng", difficulty: 2),
        Sp("dao", "Đào", "Prunus persica", "Rosaceae", ["hoa-kieng"], ["Đào Tết"], light: "Cần nắng", difficulty: 3),
        Sp("sau-rieng", "Sầu riêng", "Durio zibethinus", "Malvaceae", ["giong-an-qua", "an-qua-truong-thanh"], ["Durian"], light: "Cần nắng", difficulty: 3),
        Sp("sao-den", "Sao đen", "Hopea odorata", "Dipterocarpaceae", ["cong-trinh", "giong-lam-nghiep"], [], light: "Cần nắng", difficulty: 2),
        Sp("lan-ho-diep", "Lan hồ điệp", "Phalaenopsis sp.", "Orchidaceae", ["hoa-kieng", "lan", "hoa-tet"], ["Phalaenopsis"], light: "Bán râm", difficulty: 3),
        Sp("lan-dendro", "Lan Dendro", "Dendrobium spp.", "Orchidaceae", ["lan"], ["Lan dendrobium", "Hoàng thảo"], light: "Bán râm", difficulty: 2),
        Sp("hoa-hong", "Hoa hồng", "Rosa spp.", "Rosaceae", ["hoa-hong", "hoa-kieng"], ["Hồng", "Hồng ngoại"], light: "Cần nắng", difficulty: 2),
        Sp("hoa-giay", "Hoa giấy", "Bougainvillea spp.", "Nyctaginaceae", ["hoa-giay", "cay-ngoai-troi", "bonsai-mini", "bonsai-trung-dai"], ["Bông giấy"], light: "Cần nắng", difficulty: 1),
        Sp("cuc-mam-xoi", "Cúc mâm xôi", "Chrysanthemum morifolium", "Asteraceae", ["hoa-tet"], ["Cúc Tết"], light: "Cần nắng", difficulty: 2),
        Sp("van-nien-thanh", "Vạn niên thanh", "Aglaonema spp.", "Araceae", ["noi-that", "cay-mini"], ["Aglaonema"], light: "Chịu bóng", difficulty: 1, toxic: "Độc với chó mèo"),
        Sp("cay-ngoc-ngan", "Ngọc ngân", "Aglaonema commutatum", "Araceae", ["cay-mini", "noi-that"], [], light: "Chịu bóng", difficulty: 1, toxic: "Độc với chó mèo"),
        Sp("chuoi-ngoc", "Chuỗi ngọc", "Duranta erecta", "Verbenaceae", ["cay-hang-rao"], ["Thanh quan"], light: "Cần nắng", difficulty: 1),
        Sp("dinh-lang", "Đinh lăng", "Polyscias fruticosa", "Araliaceae", ["duoc-lieu", "bonsai-mini", "cay-ngoai-troi"], ["Cây gỏi cá"], light: "Bán râm", difficulty: 1),
        Sp("xa-den", "Xạ đen", "Ehretia asperula", "Boraginaceae", ["duoc-lieu"], [], light: "Bán râm", difficulty: 1),
        Sp("lan-hai", "Lan hài", "Paphiopedilum spp.", "Orchidaceae", ["hoa-kieng", "lan"], ["Lan hài rừng"], legal: LegalFlag.Restricted,
            legalBasis: "Nhiều loài thuộc danh mục thực vật nguy cấp (NĐ 06/2019, NĐ 84/2021) và CITES — chỉ duyệt khi có giấy tờ gây trồng hợp pháp"),
        Sp("mai-duong", "Mai dương (trinh nữ thân gỗ)", "Mimosa pigra", "Fabaceae", ["hoa-kieng"], ["Trinh nữ đầm lầy"], legal: LegalFlag.InvasiveAlien,
            legalBasis: "Loài ngoại lai xâm hại (TT 35/2018/TT-BTNMT)"),
        Sp("can-sa", "Cần sa", "Cannabis sativa", "Cannabaceae", [], ["Cỏ mỹ", "Weed"], legal: LegalFlag.Banned,
            legalBasis: "Cây chứa chất ma túy — cấm tuyệt đối (Luật Phòng, chống ma túy)"),
        Sp("thuoc-phien", "Cây thuốc phiện", "Papaver somniferum", "Papaveraceae", [], ["Anh túc", "Cây anh túc"], legal: LegalFlag.Banned,
            legalBasis: "Cây chứa chất ma túy — cấm tuyệt đối (Luật Phòng, chống ma túy)"),
    ];

    static Species Sp(string id, string name, string sci, string family, string[] cats, string[] aliases,
        string? light = null, int? difficulty = null, string? toxic = null, LegalFlag legal = LegalFlag.None, string? legalBasis = null) => new()
    {
        Id = id, CommonName = name, ScientificName = sci, Family = family, CategoryIds = [.. cats], Aliases = [.. aliases],
        Light = light, Difficulty = difficulty, PetToxicity = toxic, LegalFlag = legal, LegalBasis = legalBasis,
    };
}
