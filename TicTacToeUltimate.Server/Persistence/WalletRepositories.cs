using Dapper;
using Npgsql;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Persistence;

/// <summary>Thread-safe in-memory wallet: ledger + derived balance.</summary>
public sealed class InMemoryWalletRepository : IWalletRepository
{
    private readonly Lock _gate = new();
    private readonly List<LedgerEntry> _ledger = [];
    private readonly HashSet<(Guid, string, string)> _refs = [];
    private readonly Dictionary<Guid, int> _balance = [];
    private readonly List<InventoryItem> _inventory = [];

    public Task<WalletRecord> GetAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(new WalletRecord(userId, _balance.GetValueOrDefault(userId)));
    }

    public Task<bool> AppendAsync(LedgerEntry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_refs.Contains((entry.UserId, entry.Reason, entry.Ref))) return Task.FromResult(false);
            var balance = _balance.GetValueOrDefault(entry.UserId) + entry.Delta;
            if (balance < 0) return Task.FromResult(false);
            _refs.Add((entry.UserId, entry.Reason, entry.Ref));
            _ledger.Add(entry);
            _balance[entry.UserId] = balance;
            return Task.FromResult(true);
        }
    }

    public Task<int> CountSinceAsync(Guid userId, string reason, DateTimeOffset since, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_ledger.Count(e => e.UserId == userId && e.Reason == reason && e.At >= since));
    }

    public Task<int> SumSinceAsync(Guid userId, DateTimeOffset since, bool positiveOnly, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_ledger.Where(e => e.UserId == userId && e.At >= since && (!positiveOnly || e.Delta > 0)).Sum(e => e.Delta));
    }

    public Task<IReadOnlyList<InventoryItem>> ListInventoryAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<InventoryItem>>(_inventory.Where(i => i.UserId == userId).ToList());
    }

    public Task AddInventoryAsync(InventoryItem item, CancellationToken ct)
    {
        lock (_gate) _inventory.Add(item);
        return Task.CompletedTask;
    }

    /// <summary>Guest merge: the account absorbs the guest's balance (one ledger line) and passes; the guest is emptied.</summary>
    internal void Reassign(Guid from, Guid to)
    {
        lock (_gate)
        {
            var moved = _balance.GetValueOrDefault(from);
            if (moved > 0)
            {
                _ledger.Add(new LedgerEntry(to, moved, "merge", $"guest:{from:N}", DateTimeOffset.UtcNow));
                _balance[to] = _balance.GetValueOrDefault(to) + moved;
            }
            _balance.Remove(from);
            _ledger.RemoveAll(e => e.UserId == from);
            for (var i = 0; i < _inventory.Count; i++) if (_inventory[i].UserId == from) _inventory[i] = _inventory[i] with { UserId = to };
        }
    }

    /// <summary>Account deletion.</summary>
    internal void RemoveUser(Guid id)
    {
        lock (_gate)
        {
            _balance.Remove(id);
            _ledger.RemoveAll(e => e.UserId == id);
            _refs.RemoveWhere(r => r.Item1 == id);
            _inventory.RemoveAll(i => i.UserId == id);
        }
    }
}

/// <summary>Postgres wallet: the ledger insert and the balance update share one transaction; the unique (user, reason, ref) gives idempotency.</summary>
public sealed class PgWalletRepository(PgDb db) : IWalletRepository
{
    private sealed class InvRow { public Guid user_id; public string item_id = ""; public DateTime acquired_at; public DateTime? expires_at; }

    public async Task<WalletRecord> GetAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var b = await c.ExecuteScalarAsync<int?>("select balance from wallets where user_id=@userId", new { userId });
        return new WalletRecord(userId, b ?? 0);
    }

    public async Task<bool> AppendAsync(LedgerEntry e, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        try
        {
            var inserted = await c.ExecuteAsync("insert into diamond_ledger (user_id, delta, reason, ref, created_at) values (@UserId, @Delta, @Reason, @Ref, @at) on conflict do nothing",
                new { e.UserId, e.Delta, e.Reason, e.Ref, at = e.At.UtcDateTime }, tx);
            if (inserted == 0) { await tx.RollbackAsync(ct); return false; }
            // The check constraint (balance >= 0) refuses an overdraft; that rolls the ledger line back too.
            await c.ExecuteAsync("""
                insert into wallets (user_id, balance, updated_at) values (@UserId, @Delta, @at)
                on conflict (user_id) do update set balance = wallets.balance + excluded.balance, updated_at = excluded.updated_at
                """, new { e.UserId, e.Delta, at = e.At.UtcDateTime }, tx);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
    }

    public async Task<int> CountSinceAsync(Guid userId, string reason, DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("select count(*) from diamond_ledger where user_id=@userId and reason=@reason and created_at >= @since",
            new { userId, reason, since = since.UtcDateTime });
    }

    public async Task<int> SumSinceAsync(Guid userId, DateTimeOffset since, bool positiveOnly, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("select coalesce(sum(delta), 0) from diamond_ledger where user_id=@userId and created_at >= @since and (@pos = false or delta > 0)",
            new { userId, since = since.UtcDateTime, pos = positiveOnly });
    }

    public async Task<IReadOnlyList<InventoryItem>> ListInventoryAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<InvRow>("select user_id, item_id, acquired_at, expires_at from inventory where user_id=@userId", new { userId });
        return rows.Select(r => new InventoryItem(r.user_id, r.item_id, PgDb.Dto(r.acquired_at), PgDb.Dto(r.expires_at))).ToList();
    }

    public async Task AddInventoryAsync(InventoryItem i, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("insert into inventory (user_id, item_id, acquired_at, expires_at) values (@UserId, @ItemId, @acq, @exp)",
            new { i.UserId, i.ItemId, acq = i.AcquiredAt.UtcDateTime, exp = PgDb.Utc(i.ExpiresAt) });
    }
}
