using System.Security.Claims;
using System.Text.Json;
using ChamXanh.Api.Common.Auth;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Common;

// BR-ADM-01: mọi thao tác quản trị đều ghi audit log, chỉ ghi thêm, không sửa/xóa.
public class AuditLog
{
    [BsonId] public ObjectId Id { get; set; }
    public string ActorId { get; set; } = default!;
    public string ActorName { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string TargetType { get; set; } = default!;
    public string TargetId { get; set; } = default!;
    public string? Reason { get; set; }
    public BsonDocument? Before { get; set; }
    public BsonDocument? After { get; set; }
    public string? Ip { get; set; }
    public DateTime At { get; set; }
}

public class AuditService(IMongoDatabase db, TimeProvider clock, IHttpContextAccessor http)
{
    public IMongoCollection<AuditLog> Logs { get; } = db.GetCollection<AuditLog>("auditLogs");

    public Task EnsureIndexesAsync() => Logs.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<AuditLog>(Builders<AuditLog>.IndexKeys.Ascending(l => l.TargetType).Ascending(l => l.TargetId).Descending(l => l.At)),
        new CreateIndexModel<AuditLog>(Builders<AuditLog>.IndexKeys.Ascending(l => l.ActorId).Descending(l => l.At)),
    ]);

    public Task LogAsync(ClaimsPrincipal actor, string action, string targetType, string targetId,
        object? before = null, object? after = null, string? reason = null, CancellationToken ct = default) =>
        Logs.InsertOneAsync(new AuditLog
        {
            ActorId = actor.UserId(), ActorName = actor.ActorName(), Action = action,
            TargetType = targetType, TargetId = targetId, Reason = reason,
            Before = ToBson(before), After = ToBson(after),
            Ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            At = clock.GetUtcNow().UtcDateTime,
        }, cancellationToken: ct);

    static BsonDocument? ToBson(object? value) =>
        value is null ? null : BsonDocument.Parse(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));
}
