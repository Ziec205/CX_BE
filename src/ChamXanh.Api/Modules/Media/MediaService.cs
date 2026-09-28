using ChamXanh.Api.Common;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace ChamXanh.Api.Modules.Media;

public enum MediaKind { ListingPhoto, Avatar, CommunityPhoto, KycDocument, DisputeEvidence, PlantPhoto, ArticlePhoto }

public class MediaItem
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string OwnerId { get; set; } = default!;
    [BsonRepresentation(BsonType.String)] public MediaKind Kind { get; set; }
    public bool Secure { get; set; }
    public Dictionary<string, ObjectId> Variants { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>pHash lưu dạng long để truy vấn được; giữ lại cả khi file đã bị dọn (BR-MED-03).</summary>
    public long PHash { get; set; }
    public bool CapturedInApp { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string? AttachedTo { get; set; }
    public bool FilesDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record MediaDto(string Id, string Kind, int Width, int Height, bool CapturedInApp, DateTime? CapturedAt, Dictionary<string, string> Urls);

public class MediaService(IMongoDatabase db, TimeProvider clock, ILogger<MediaService> logger)
{
    public IMongoCollection<MediaItem> Items { get; } = db.GetCollection<MediaItem>("media");
    readonly GridFSBucket _public = new(db, new GridFSBucketOptions { BucketName = "media" });
    readonly GridFSBucket _secure = new(db, new GridFSBucketOptions { BucketName = "secure" });

    public Task EnsureIndexesAsync() => Items.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<MediaItem>(Builders<MediaItem>.IndexKeys.Ascending(m => m.OwnerId).Ascending(m => m.CreatedAt)),
        new CreateIndexModel<MediaItem>(Builders<MediaItem>.IndexKeys.Ascending(m => m.AttachedTo).Ascending(m => m.CreatedAt)),
    ]);

    public static bool IsSecureKind(MediaKind k) => k is MediaKind.KycDocument or MediaKind.DisputeEvidence;

    public async Task<MediaItem> UploadImageAsync(string ownerId, MediaKind kind, byte[] data, string? watermark,
        bool capturedInApp, DateTime? capturedAt, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (capturedAt > now.AddMinutes(5)) capturedAt = null; // thời gian chụp ở tương lai: không tin
        var secure = IsSecureKind(kind);
        var processed = ImageProcessor.Process(data, secure ? null : watermark);
        var bucket = secure ? _secure : _public;
        var item = new MediaItem
        {
            OwnerId = ownerId, Kind = kind, Secure = secure, Width = processed.Width, Height = processed.Height,
            PHash = unchecked((long)processed.PHash), CapturedInApp = capturedInApp, CapturedAt = capturedInApp ? capturedAt ?? now : null,
            CreatedAt = now,
        };
        foreach (var v in processed.Variants)
            item.Variants[v.Name] = await bucket.UploadFromBytesAsync($"{item.Id}/{v.Name}.webp", v.Data,
                new GridFSUploadOptions { Metadata = new BsonDocument { { "mediaId", item.Id }, { "contentType", "image/webp" } } }, ct);
        await Items.InsertOneAsync(item, cancellationToken: ct);
        return item;
    }

    public async Task<MediaItem?> FindAsync(string id, CancellationToken ct) =>
        ObjectId.TryParse(id, out _) ? await Items.Find(m => m.Id == id).FirstOrDefaultAsync(ct) : null;

    public async Task<List<MediaItem>> GetOwnedAsync(IEnumerable<string> ids, string ownerId, CancellationToken ct)
    {
        var list = ids.ToList();
        if (list.Any(i => !ObjectId.TryParse(i, out _))) throw new DomainException("INVALID_MEDIA", "Mã ảnh không hợp lệ");
        var items = await Items.Find(m => list.Contains(m.Id)).ToListAsync(ct);
        if (items.Count != list.Distinct().Count() || items.Any(m => m.OwnerId != ownerId || m.FilesDeleted))
            throw new DomainException("INVALID_MEDIA", "Có ảnh không tồn tại hoặc không thuộc về bạn");
        return list.Select(id => items.First(m => m.Id == id)).ToList();
    }

    public Task AttachAsync(IEnumerable<string> ids, string target, CancellationToken ct) =>
        Items.UpdateManyAsync(m => ids.Contains(m.Id), Builders<MediaItem>.Update.Set(m => m.AttachedTo, target), cancellationToken: ct);

    public async Task<(Stream Stream, string ContentType)?> OpenAsync(MediaItem item, string variant, CancellationToken ct)
    {
        if (item.FilesDeleted || !item.Variants.TryGetValue(variant, out var fileId)) return null;
        var bucket = item.Secure ? _secure : _public;
        return (await bucket.OpenDownloadStreamAsync(fileId, cancellationToken: ct), "image/webp");
    }

    /// <summary>BR-MED-03: xóa file ảnh chưa gắn vào đâu sau 24h, và ảnh của tin đã xóa/bị từ chối quá 30 ngày (giữ pHash).</summary>
    public async Task<int> CleanupAsync(IEnumerable<string> deadTargets, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var dead = deadTargets.ToList();
        var f = Builders<MediaItem>.Filter;
        var filter = f.Eq(m => m.FilesDeleted, false) & f.Eq(m => m.Secure, false) &
            (f.Eq(m => m.AttachedTo, null) & f.Lt(m => m.CreatedAt, now.AddHours(-24)) | f.In(m => m.AttachedTo, dead));
        var items = await Items.Find(filter).Limit(500).ToListAsync(ct);
        foreach (var item in items)
        {
            foreach (var fileId in item.Variants.Values)
            {
                try { await _public.DeleteAsync(fileId, ct); }
                catch (GridFSFileNotFoundException) { }
            }
            await Items.UpdateOneAsync(m => m.Id == item.Id, Builders<MediaItem>.Update.Set(m => m.FilesDeleted, true), cancellationToken: ct);
        }
        if (items.Count > 0) logger.LogInformation("Đã dọn {Count} ảnh", items.Count);
        return items.Count;
    }

    public static MediaDto ToDto(MediaItem m, string baseUrl = "") => new(m.Id, m.Kind.ToString(), m.Width, m.Height, m.CapturedInApp, m.CapturedAt,
        m.Secure ? [] : m.Variants.Keys.ToDictionary(k => k, k => $"{baseUrl}/media/{m.Id}/{k}.webp"));
}
