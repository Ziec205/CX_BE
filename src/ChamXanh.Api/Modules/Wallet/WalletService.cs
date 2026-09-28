using ChamXanh.Api.Common;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Wallet;

public class WalletService(IMongoDatabase db, TimeProvider clock)
{
    public IMongoCollection<XuWallet> Wallets { get; } = db.GetCollection<XuWallet>("wallets");
    public IMongoCollection<LedgerEntry> Ledger { get; } = db.GetCollection<LedgerEntry>("walletLedger");

    public async Task EnsureIndexesAsync()
    {
        await Ledger.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<LedgerEntry>(Builders<LedgerEntry>.IndexKeys.Ascending(e => e.IdempotencyKey), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<LedgerEntry>(Builders<LedgerEntry>.IndexKeys.Ascending(e => e.UserId).Descending(e => e.CreatedAt)),
        ]);
    }

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<XuWallet> GetAsync(string userId, CancellationToken ct, IClientSessionHandle? s = null)
    {
        var find = s is null ? Wallets.Find(w => w.UserId == userId) : Wallets.Find(s, w => w.UserId == userId);
        return await find.FirstOrDefaultAsync(ct) ?? new XuWallet { UserId = userId };
    }

    /// <summary>Phân bổ số Xu cần trừ vào các lô: Xu thưởng trước, lô hết hạn sớm trước (thuần, dễ kiểm thử).</summary>
    public static List<LotAllocation>? Allocate(IEnumerable<XuLot> lots, long amount, DateTime now)
    {
        var ordered = lots.Where(l => l.Remaining > 0 && (l.ExpiresAt is null || l.ExpiresAt > now))
            .OrderBy(l => l.Kind == LotKind.Bonus ? 0 : 1).ThenBy(l => l.ExpiresAt ?? DateTime.MaxValue).ThenBy(l => l.CreatedAt);
        var result = new List<LotAllocation>();
        var left = amount;
        foreach (var lot in ordered)
        {
            if (left == 0) break;
            var take = Math.Min(lot.Remaining, left);
            result.Add(new LotAllocation { LotId = lot.Id, Kind = lot.Kind, Amount = take });
            left -= take;
        }
        return left == 0 ? result : null;
    }

    /// <summary>Trừ Xu trong transaction đang mở. Trùng khóa idempotency thì trả về bút toán cũ, không trừ lần 2 (BR-XU-02).</summary>
    public async Task<(LedgerEntry Entry, bool Duplicate)> DebitAsync(IClientSessionHandle s, string userId, long amount, string idempotencyKey,
        string refType, string refId, string note, CancellationToken ct)
    {
        if (amount <= 0) throw new DomainException("INVALID_AMOUNT", "Số Xu không hợp lệ");
        var existing = await Ledger.Find(s, e => e.IdempotencyKey == idempotencyKey).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (existing.UserId != userId) throw DomainException.Conflict("IDEMPOTENCY_KEY_REUSED", "Khóa idempotency đã được dùng");
            return (existing, true);
        }

        var wallet = await GetAsync(userId, ct, s);
        if (wallet.Frozen) throw DomainException.Forbidden("Ví Xu đang bị đóng băng");
        var alloc = Allocate(wallet.Lots, amount, Now)
            ?? throw new DomainException("INSUFFICIENT_XU", $"Không đủ Xu (cần {amount}, còn {wallet.Balance(Now)})", StatusCodes.Status402PaymentRequired);
        foreach (var a in alloc) wallet.Lots.First(l => l.Id == a.LotId).Remaining -= a.Amount;

        await SaveWalletAsync(s, wallet, ct);
        var entry = new LedgerEntry
        {
            UserId = userId, Amount = -amount, Type = LedgerType.Spend, RefType = refType, RefId = refId,
            IdempotencyKey = idempotencyKey, Allocations = alloc, Note = note, CreatedAt = Now,
        };
        await Ledger.InsertOneAsync(s, entry, cancellationToken: ct);
        return (entry, false);
    }

    public async Task<LedgerEntry> CreditAsync(IClientSessionHandle s, string userId, long amount, LotKind kind, DateTime? expiresAt,
        LedgerType type, string idempotencyKey, string? refType, string? refId, string? note, CancellationToken ct)
    {
        if (amount <= 0) throw new DomainException("INVALID_AMOUNT", "Số Xu không hợp lệ");
        var existing = await Ledger.Find(s, e => e.IdempotencyKey == idempotencyKey).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;
        var wallet = await GetAsync(userId, ct, s);
        var lot = new XuLot { Kind = kind, Amount = amount, Remaining = amount, ExpiresAt = expiresAt, CreatedAt = Now };
        wallet.Lots.Add(lot);
        await SaveWalletAsync(s, wallet, ct);
        var entry = new LedgerEntry
        {
            UserId = userId, Amount = amount, Type = type, RefType = refType, RefId = refId, IdempotencyKey = idempotencyKey,
            Allocations = [new() { LotId = lot.Id, Kind = kind, Amount = amount }], Note = note, CreatedAt = Now,
        };
        await Ledger.InsertOneAsync(s, entry, cancellationToken: ct);
        return entry;
    }

    /// <summary>Hoàn lại đúng các lô đã trừ (BR-XU-03), lô đã hết hạn thì hoàn thành Xu thưởng hạn 30 ngày.</summary>
    public async Task<LedgerEntry> RefundAsync(IClientSessionHandle s, LedgerEntry spend, string reason, CancellationToken ct)
    {
        var key = $"refund:{spend.Id}";
        var existing = await Ledger.Find(s, e => e.IdempotencyKey == key).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;
        var wallet = await GetAsync(spend.UserId, ct, s);
        foreach (var a in spend.Allocations)
        {
            var lot = wallet.Lots.FirstOrDefault(l => l.Id == a.LotId);
            if (lot is not null && (lot.ExpiresAt is null || lot.ExpiresAt > Now)) lot.Remaining += a.Amount;
            else wallet.Lots.Add(new XuLot { Kind = LotKind.Bonus, Amount = a.Amount, Remaining = a.Amount, ExpiresAt = Now.AddDays(30), CreatedAt = Now });
        }
        await SaveWalletAsync(s, wallet, ct);
        var entry = new LedgerEntry
        {
            UserId = spend.UserId, Amount = -spend.Amount, Type = LedgerType.Refund, RefType = spend.RefType, RefId = spend.RefId,
            IdempotencyKey = key, Allocations = spend.Allocations, Note = reason, CreatedAt = Now,
        };
        await Ledger.InsertOneAsync(s, entry, cancellationToken: ct);
        return entry;
    }

    /// <summary>Optimistic concurrency theo Version: hai giao dịch đồng thời trên cùng ví, giao dịch sau sẽ lỗi và transaction bị hủy.</summary>
    async Task SaveWalletAsync(IClientSessionHandle s, XuWallet wallet, CancellationToken ct)
    {
        var expected = wallet.Version;
        wallet.Version++;
        wallet.Lots.RemoveAll(l => l.Remaining == 0 && l.CreatedAt < Now.AddDays(-365));
        if (expected == 0)
        {
            await Wallets.ReplaceOneAsync(s, w => w.UserId == wallet.UserId && w.Version == 0, wallet, new ReplaceOptions { IsUpsert = true }, ct);
            return;
        }
        var r = await Wallets.ReplaceOneAsync(s, w => w.UserId == wallet.UserId && w.Version == expected, wallet, cancellationToken: ct);
        if (r.MatchedCount == 0) throw DomainException.Conflict("CONCURRENT_UPDATE", "Ví vừa thay đổi, vui lòng thử lại");
    }

    /// <summary>Xu thưởng hết hạn: ghi bút toán Expire để số dư khớp sổ cái.</summary>
    public async Task<int> ExpireLotsAsync(IMongoClient client, CancellationToken ct)
    {
        var now = Now;
        var wallets = await Wallets.Find(w => w.Lots.Any(l => l.Remaining > 0 && l.ExpiresAt < now)).Limit(500).ToListAsync(ct);
        foreach (var w in wallets)
        {
            using var s = await client.StartSessionAsync(cancellationToken: ct);
            await s.WithTransactionAsync(async (session, token) =>
            {
                var wallet = await GetAsync(w.UserId, token, session);
                var expired = wallet.Lots.Where(l => l.Remaining > 0 && l.ExpiresAt < now).ToList();
                if (expired.Count == 0) return true;
                var alloc = expired.Select(l => new LotAllocation { LotId = l.Id, Kind = l.Kind, Amount = l.Remaining }).ToList();
                foreach (var l in expired) l.Remaining = 0;
                await SaveWalletAsync(session, wallet, token);
                await Ledger.InsertOneAsync(session, new LedgerEntry
                {
                    UserId = w.UserId, Amount = -alloc.Sum(a => a.Amount), Type = LedgerType.Expire, IdempotencyKey = $"expire:{w.UserId}:{now:O}",
                    Allocations = alloc, Note = "Xu thưởng hết hạn", CreatedAt = now,
                }, cancellationToken: token);
                return true;
            }, cancellationToken: ct);
        }
        return wallets.Count;
    }
}
