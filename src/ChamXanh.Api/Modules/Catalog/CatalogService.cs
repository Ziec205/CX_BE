using System.Security.Claims;
using System.Text.RegularExpressions;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Catalog;

/// <summary>Module khác (Tin đăng) đăng ký để xử lý khi một loài bị đổi cờ pháp lý (BR-LIB-04).</summary>
public interface ISpeciesLegalFlagChanged
{
    Task HandleAsync(string speciesId, LegalFlag newFlag, CancellationToken ct);
}

// Handler được lấy lúc gọi (không inject qua constructor) để tránh vòng phụ thuộc Catalog ↔ Listings.
public class CatalogService(IMongoDatabase db, TimeProvider clock, IServiceProvider services)
{
    public IMongoCollection<Category> Categories { get; } = db.GetCollection<Category>("categories");
    public IMongoCollection<Species> Species { get; } = db.GetCollection<Species>("species");
    public IMongoCollection<PlantCollection> Collections { get; } = db.GetCollection<PlantCollection>("collections");

    public async Task EnsureIndexesAndSeedAsync(CancellationToken ct)
    {
        await Species.Indexes.CreateOneAsync(new CreateIndexModel<Species>(Builders<Species>.IndexKeys.Ascending(s => s.SearchTerms)), cancellationToken: ct);
        await Species.Indexes.CreateOneAsync(new CreateIndexModel<Species>(Builders<Species>.IndexKeys.Ascending(s => s.CategoryIds)), cancellationToken: ct);
        // Chỉ thêm mục seed còn thiếu (theo Id) — không ghi đè chỉnh sửa của quản trị, để DB cũ nhận được danh mục mới.
        var existingCats = (await Categories.Find(_ => true).Project(c => c.Id).ToListAsync(ct)).ToHashSet();
        var newCats = CatalogSeed.Create().Where(c => !existingCats.Contains(c.Id)).ToList();
        if (newCats.Count > 0) await Categories.InsertManyAsync(newCats, cancellationToken: ct);

        var existingSpecies = (await Species.Find(_ => true).Project(s => s.Id).ToListAsync(ct)).ToHashSet();
        var speciesSeed = CatalogSeed.Species();
        var newSpecies = speciesSeed.Where(s => !existingSpecies.Contains(s.Id)).ToList();
        foreach (var s in newSpecies) Prepare(s);
        if (newSpecies.Count > 0) await Species.InsertManyAsync(newSpecies, cancellationToken: ct);
        // Loài đã có: chỉ bổ sung danh mục mới vào CategoryIds.
        var links = speciesSeed.Where(s => existingSpecies.Contains(s.Id) && s.CategoryIds.Count > 0)
            .Select(s => new UpdateOneModel<Species>(Builders<Species>.Filter.Eq(x => x.Id, s.Id),
                Builders<Species>.Update.AddToSetEach(x => x.CategoryIds, s.CategoryIds)))
            .ToList();
        if (links.Count > 0) await Species.BulkWriteAsync(links, cancellationToken: ct);
    }

    public Task<List<Category>> ListCategoriesAsync(CancellationToken ct) =>
        Categories.Find(c => c.Active).SortBy(c => c.Level).ThenBy(c => c.Order).ToListAsync(ct);

    public async Task<Category> GetCategoryAsync(string id, CancellationToken ct = default) =>
        await Categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("danh mục");

    /// <summary>Danh mục để đăng tin: phải là cấp 2 và đang hoạt động.</summary>
    public async Task<Category> GetLeafForPostingAsync(string id, CancellationToken ct)
    {
        var c = await Categories.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (c is null || !c.Active || c.Level != 2) throw new DomainException("INVALID_CATEGORY", "Danh mục không hợp lệ, hãy chọn danh mục cấp 2");
        return c;
    }

    public async Task<Category> UpsertCategoryAsync(Category c, CancellationToken ct)
    {
        if (c.Level == 2 && (c.ParentId is null || await Categories.Find(x => x.Id == c.ParentId && x.Level == 1).AnyAsync(ct) == false))
            throw new DomainException("INVALID_PARENT", "Danh mục cấp 2 phải có danh mục cha cấp 1 hợp lệ");
        var dupKeys = c.Attributes.GroupBy(a => a.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupKeys.Count > 0) throw new DomainException("DUPLICATE_ATTRIBUTE", $"Trùng khóa thuộc tính: {string.Join(", ", dupKeys)}");
        foreach (var a in c.Attributes.Where(a => a.Type is AttributeType.SingleSelect or AttributeType.MultiSelect && a.Options.Count == 0))
            throw new DomainException("MISSING_OPTIONS", $"Thuộc tính '{a.Key}' dạng chọn phải có danh sách lựa chọn");
        await Categories.ReplaceOneAsync(x => x.Id == c.Id, c, new ReplaceOptions { IsUpsert = true }, ct);
        return c;
    }

    public async Task<Species> GetSpeciesAsync(string id, CancellationToken ct = default) =>
        await Species.Find(s => s.Id == id).FirstOrDefaultAsync(ct) ?? throw DomainException.NotFound("loài cây");

    public async Task<List<Species>> SearchSpeciesAsync(string? q, string? categoryId, int limit, CancellationToken ct)
    {
        var f = Builders<Species>.Filter;
        var filter = f.Ne(s => s.LegalFlag, LegalFlag.Banned);
        if (!string.IsNullOrWhiteSpace(categoryId)) filter &= f.AnyEq(s => s.CategoryIds, categoryId);
        var norm = VietnameseText.Normalize(q);
        if (norm.Length > 0) filter &= f.Regex("searchTerms", new MongoDB.Bson.BsonRegularExpression(Regex.Escape(norm)));
        return await Species.Find(filter).SortBy(s => s.CommonName).Limit(Math.Clamp(limit, 1, 50)).ToListAsync(ct);
    }

    /// <summary>Tìm loài khớp theo tên (cả tên khác), dùng cho mở rộng từ đồng nghĩa khi tìm tin.</summary>
    public async Task<List<string>> MatchSpeciesIdsAsync(string normalizedQuery, CancellationToken ct)
    {
        if (normalizedQuery.Length < 2) return [];
        return await Species.Find(Builders<Species>.Filter.Regex("searchTerms", new MongoDB.Bson.BsonRegularExpression(Regex.Escape(normalizedQuery))))
            .Project(s => s.Id).Limit(20).ToListAsync(ct);
    }

    public async Task<Species> UpsertSpeciesAsync(Species s, ClaimsPrincipal actor, CancellationToken ct)
    {
        var existing = await Species.Find(x => x.Id == s.Id).FirstOrDefaultAsync(ct);
        var flagChanged = (existing?.LegalFlag ?? LegalFlag.None) != s.LegalFlag;
        // BR-LIB-04: đổi cờ pháp lý chỉ Super Admin (quyền admin.manage).
        if (flagChanged && !actor.HasClaim(Claims.Perm, Perm.AdminManage))
            throw DomainException.Forbidden("Chỉ Super Admin được đổi cờ pháp lý của loài");
        if (existing is not null) s.PriceRefs = existing.PriceRefs; // giá tham khảo do hệ thống tính
        Prepare(s);
        await Species.ReplaceOneAsync(x => x.Id == s.Id, s, new ReplaceOptions { IsUpsert = true }, ct);
        if (flagChanged && s.LegalFlag is LegalFlag.Banned or LegalFlag.Restricted)
            foreach (var h in services.GetServices<ISpeciesLegalFlagChanged>()) await h.HandleAsync(s.Id, s.LegalFlag, ct);
        return s;
    }

    void Prepare(Species s)
    {
        s.SearchTerms = new[] { s.CommonName, s.ScientificName }.Concat(s.Aliases)
            .Select(VietnameseText.Normalize).Where(t => t.Length > 0).Distinct().ToList();
        s.UpdatedAt = clock.GetUtcNow().UtcDateTime;
    }
}
