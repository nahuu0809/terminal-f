using Dapper;
using Microsoft.Data.Sqlite;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.Data.Repositories;

public sealed class DatabaseRepository : IAsyncDisposable
{
    private readonly string _connectionString;

    public DatabaseRepository(string dbPath = "terminal_data.sqlite3")
    {
        _connectionString = $"Data Source={dbPath};Cache=Shared;Pooling=True";
        using var db = CreateConnection();
        db.Open();
        InitializeSchema(db);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private void InitializeSchema(SqliteConnection db)
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS trades (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                strategy TEXT NOT NULL,
                ticker TEXT NOT NULL,
                side TEXT NOT NULL,
                settlement TEXT NOT NULL,
                quantity REAL NOT NULL,
                price REAL NOT NULL,
                gross_profit REAL,
                net_profit REAL,
                fees REAL,
                order_id TEXT,
                status TEXT,
                broker TEXT
            );

            CREATE TABLE IF NOT EXISTS bot_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                strategy TEXT NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS daily_pnl (
                date TEXT PRIMARY KEY,
                total_gross REAL NOT NULL DEFAULT 0,
                total_net REAL NOT NULL DEFAULT 0,
                total_fees REAL NOT NULL DEFAULT 0,
                trade_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS quotes_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                ticker TEXT NOT NULL,
                settlement TEXT NOT NULL,
                bid REAL, ask REAL, last REAL,
                volume REAL,
                source TEXT
            );

            CREATE TABLE IF NOT EXISTS active_orders (
                execution_id TEXT PRIMARY KEY,
                strategy TEXT NOT NULL,
                ticker TEXT NOT NULL,
                side TEXT NOT NULL,
                settlement TEXT NOT NULL,
                quantity REAL NOT NULL,
                price REAL NOT NULL,
                state TEXT NOT NULL,
                broker_order_id TEXT,
                error TEXT,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS order_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                execution_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                message TEXT NOT NULL,
                payload TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS order_fills (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                fill_id TEXT,
                execution_id TEXT NOT NULL,
                broker_order_id TEXT,
                ticker TEXT,
                side TEXT,
                settlement TEXT,
                quantity REAL NOT NULL,
                price REAL NOT NULL,
                status TEXT NOT NULL,
                success INTEGER NOT NULL,
                error TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS daily_account_snapshots (
                date TEXT NOT NULL,
                broker TEXT NOT NULL,
                available_ars REAL NOT NULL,
                available_usd REAL NOT NULL,
                total_ars REAL NOT NULL,
                captured_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (date, broker)
            );

            CREATE TABLE IF NOT EXISTS daily_position_snapshots (
                date TEXT NOT NULL,
                broker TEXT NOT NULL,
                ticker TEXT NOT NULL,
                settlement TEXT NOT NULL,
                buy_size REAL NOT NULL,
                sell_size REAL NOT NULL,
                buy_price REAL NOT NULL,
                sell_price REAL NOT NULL,
                net_quantity REAL NOT NULL,
                total_daily_diff REAL NOT NULL,
                total_diff REAL NOT NULL,
                captured_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (date, broker, ticker, settlement)
            );

            CREATE INDEX IF NOT EXISTS idx_trades_date ON trades(timestamp);
            CREATE INDEX IF NOT EXISTS idx_trades_ticker ON trades(ticker);
            CREATE INDEX IF NOT EXISTS idx_quotes_ticker ON quotes_history(ticker, timestamp);
            CREATE INDEX IF NOT EXISTS idx_active_orders_state ON active_orders(state);
            CREATE INDEX IF NOT EXISTS idx_order_events_execution ON order_events(execution_id, created_at);
            CREATE INDEX IF NOT EXISTS idx_order_fills_execution ON order_fills(execution_id, created_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_order_fills_broker_order_id ON order_fills(broker_order_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_trades_broker_order_id ON trades(broker, order_id)
                WHERE order_id IS NOT NULL AND order_id <> '';
            CREATE UNIQUE INDEX IF NOT EXISTS ux_trades_broker_order_id_full ON trades(broker, order_id);
            CREATE INDEX IF NOT EXISTS idx_trades_order_id ON trades(order_id);
            CREATE INDEX IF NOT EXISTS idx_daily_position_snapshots_date ON daily_position_snapshots(date, broker);
        """);

        EnsureOrderFillsSchema(db);
    }

    private static void EnsureOrderFillsSchema(SqliteConnection db)
    {
        var hasFillId = db.ExecuteScalar<long>("""
            SELECT COUNT(1)
            FROM pragma_table_info('order_fills')
            WHERE name = 'fill_id';
        """);

        if (hasFillId == 0)
        {
            db.Execute("ALTER TABLE order_fills ADD COLUMN fill_id TEXT;");
        }

        db.Execute("DROP INDEX IF EXISTS ux_order_fills_execution;");
        db.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_order_fills_broker_order_id ON order_fills(broker_order_id);");
        db.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_order_fills_fill_id ON order_fills(fill_id);");
    }

    public async Task<long> InsertTradeAsync(string strategy, OrderResult order,
        decimal grossProfit = 0, decimal netProfit = 0, decimal fees = 0)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        var insertedRows = await db.ExecuteAsync("""
            INSERT INTO trades (strategy, ticker, side, settlement, quantity, price,
                              gross_profit, net_profit, fees, order_id, status, broker)
            VALUES (@Strategy, @Ticker, @Side, @Settlement, @Qty, @Price,
                   @GrossProfit, @NetProfit, @Fees, @OrderId, @Status, @Broker)
            ON CONFLICT(broker, order_id) DO NOTHING;
        """, new
        {
            Strategy = strategy,
            Ticker = order.Ticker,
            Side = order.Side.ToString(),
            Settlement = order.Settlement.ToString(),
            Qty = order.ExecutedQuantity,
            Price = order.ExecutedPrice,
            GrossProfit = grossProfit,
            NetProfit = netProfit,
            Fees = fees,
            OrderId = order.OrderId,
            Status = order.Status.ToString(),
            Broker = order.Broker.ToString()
        }, tx);

        if (insertedRows == 0)
        {
            await tx.RollbackAsync();
            return 0;
        }

        var tradeId = await db.ExecuteScalarAsync<long>("SELECT last_insert_rowid();", transaction: tx);

        await db.ExecuteAsync("""
            INSERT INTO daily_pnl (date, total_gross, total_net, total_fees, trade_count)
            VALUES (date('now'), @GrossProfit, @NetProfit, @Fees, 1)
            ON CONFLICT(date) DO UPDATE SET
                total_gross = total_gross + excluded.total_gross,
                total_net = total_net + excluded.total_net,
                total_fees = total_fees + excluded.total_fees,
                trade_count = trade_count + 1;
        """, new
        {
            GrossProfit = grossProfit,
            NetProfit = netProfit,
            Fees = fees
        }, tx);

        await tx.CommitAsync();
        return tradeId;
    }

    public async Task InsertBotLogAsync(string strategy, string level, string message)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO bot_logs (strategy, level, message) VALUES (@Strategy, @Level, @Message)
        """, new { Strategy = strategy, Level = level, Message = message });
    }

    public async Task<IEnumerable<dynamic>> GetTodayTradesAsync()
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        return await db.QueryAsync("""
            SELECT * FROM trades WHERE date(timestamp) = date('now') ORDER BY timestamp DESC
        """);
    }

    public async Task<(decimal TotalNet, int TradeCount)> GetTodayPnLAsync()
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        var daily = await db.QuerySingleOrDefaultAsync<dynamic>("""
            SELECT total_net as TotalNet, trade_count as TradeCount
            FROM daily_pnl WHERE date = date('now')
        """);
        if (daily is not null)
            return (Convert.ToDecimal(daily.TotalNet ?? 0m), Convert.ToInt32(daily.TradeCount ?? 0));

        var fallback = await db.QuerySingleOrDefaultAsync<dynamic>("""
            SELECT COALESCE(SUM(net_profit), 0) as TotalNet, COUNT(*) as TradeCount
            FROM trades WHERE date(timestamp) = date('now')
        """);
        return (Convert.ToDecimal(fallback?.TotalNet ?? 0m), Convert.ToInt32(fallback?.TradeCount ?? 0));
    }

    public async Task<IEnumerable<dynamic>> GetRecentLogsAsync(string? strategy = null, int limit = 100)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        var where = strategy is not null ? "WHERE strategy = @Strategy" : "";
        return await db.QueryAsync($"""
            SELECT * FROM bot_logs {where} ORDER BY timestamp DESC LIMIT @Limit
        """, new { Strategy = strategy, Limit = limit });
    }

    public async Task InsertQuoteSnapshotAsync(Quote quote, string source)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO quotes_history (ticker, settlement, bid, ask, last, volume, source)
            VALUES (@Ticker, @Settlement, @Bid, @Ask, @Last, @Volume, @Source)
        """, new
        {
            quote.Ticker,
            Settlement = quote.Settlement.ToString(),
            quote.Bid,
            quote.Ask,
            quote.Last,
            quote.Volume,
            Source = source
        });
    }

    public async Task UpsertManagedOrderAsync(ManagedOrderSnapshot snapshot)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO active_orders
            (execution_id, strategy, ticker, side, settlement, quantity, price, state, broker_order_id, error, updated_at)
            VALUES
            (@ExecutionId, @StrategyName, @Ticker, @Side, @Settlement, @Quantity, @Price, @State, @BrokerOrderId, @Error, @UpdatedAtUtc)
            ON CONFLICT(execution_id) DO UPDATE SET
                strategy = excluded.strategy,
                ticker = excluded.ticker,
                side = excluded.side,
                settlement = excluded.settlement,
                quantity = excluded.quantity,
                price = excluded.price,
                state = excluded.state,
                broker_order_id = excluded.broker_order_id,
                error = excluded.error,
                updated_at = excluded.updated_at;
        """, new
        {
            snapshot.ExecutionId,
            snapshot.StrategyName,
            snapshot.Ticker,
            Side = snapshot.Side.ToString(),
            Settlement = snapshot.Settlement.ToString(),
            snapshot.Quantity,
            snapshot.Price,
            State = snapshot.State.ToString(),
            snapshot.BrokerOrderId,
            snapshot.Error,
            UpdatedAtUtc = snapshot.UpdatedAtUtc.ToString("O"),
        });
    }

    public async Task AppendOrderEventAsync(string executionId, string eventType, string message, string? payload = null)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO order_events (execution_id, event_type, message, payload)
            VALUES (@ExecutionId, @EventType, @Message, @Payload);
        """, new { ExecutionId = executionId, EventType = eventType, Message = message, Payload = payload });
    }

    public async Task InsertOrderFillAsync(string executionId, OrderResult result)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        var brokerOrderId = string.IsNullOrWhiteSpace(result.OrderId) ? null : result.OrderId;
        await db.ExecuteAsync("""
            INSERT INTO order_fills
            (fill_id, execution_id, broker_order_id, ticker, side, settlement, quantity, price, status, success, error)
            VALUES
            (@FillId, @ExecutionId, @BrokerOrderId, @Ticker, @Side, @Settlement, @Quantity, @Price, @Status, @Success, @Error)
            ON CONFLICT(broker_order_id) DO UPDATE SET
                broker_order_id = excluded.broker_order_id,
                ticker = excluded.ticker,
                side = excluded.side,
                settlement = excluded.settlement,
                quantity = excluded.quantity,
                price = excluded.price,
                status = excluded.status,
                success = excluded.success,
                error = excluded.error;
        """, new
        {
            FillId = Guid.NewGuid().ToString("N"),
            ExecutionId = executionId,
            BrokerOrderId = brokerOrderId,
            result.Ticker,
            Side = result.Side.ToString(),
            Settlement = result.Settlement.ToString(),
            Quantity = result.ExecutedQuantity,
            Price = result.ExecutedPrice,
            Status = result.Status.ToString(),
            Success = result.Success ? 1 : 0,
            Error = result.ErrorMessage,
        });
    }

    public async Task<bool> HasTradeForOrderIdAsync(string? orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return false;

        await using var db = CreateConnection();
        await db.OpenAsync();
        var count = await db.ExecuteScalarAsync<long>("""
            SELECT COUNT(1) FROM trades WHERE order_id = @OrderId
        """, new { OrderId = orderId });
        return count > 0;
    }

    /// <summary>
    /// Reconstruye posiciones netas a partir de fills del día actual (UTC).
    /// Se limita al día en curso para que el RiskManager no acumule historial
    /// de días anteriores, que ya deberían estar planos (estrategia intradiaria).
    /// </summary>
    public async Task<Dictionary<string, decimal>> GetNetPositionsSnapshotAsync()
    {
        await using var db = CreateConnection();
        await db.OpenAsync();

        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");

        var fillRows = await db.QueryAsync("""
            SELECT ticker, side, quantity
            FROM order_fills
            WHERE success = 1
              AND date(created_at) >= @Today
        """, new { Today = today });

        var snapshot = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in fillRows)
        {
            var ticker = (string?)row.ticker;
            if (string.IsNullOrWhiteSpace(ticker))
                continue;

            var side = ((string?)row.side) ?? string.Empty;
            var qty = Convert.ToDecimal(row.quantity);
            var signedQty = side.Equals(nameof(OrderSide.Buy), StringComparison.OrdinalIgnoreCase) ? qty : -qty;
            snapshot[ticker] = snapshot.GetValueOrDefault(ticker) + signedQty;
        }

        if (snapshot.Count > 0)
            return snapshot;

        // Fallback: reconstruir desde trades si order_fills está vacío (datos legacy)
        var tradeRows = await db.QueryAsync("""
            SELECT ticker, side, quantity
            FROM trades
            WHERE status IN ('Filled', 'PartiallyFilled')
              AND date(timestamp) >= @Today
        """, new { Today = today });

        foreach (var row in tradeRows)
        {
            var ticker = (string?)row.ticker;
            if (string.IsNullOrWhiteSpace(ticker))
                continue;

            var side = ((string?)row.side) ?? string.Empty;
            var qty = Convert.ToDecimal(row.quantity);
            var signedQty = side.Equals(nameof(OrderSide.Buy), StringComparison.OrdinalIgnoreCase) ? qty : -qty;
            snapshot[ticker] = snapshot.GetValueOrDefault(ticker) + signedQty;
        }

        return snapshot;
    }

    public async Task RebuildDailyPnlFromTradesAsync()
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        await db.ExecuteAsync("DELETE FROM daily_pnl;", transaction: tx);
        await db.ExecuteAsync("""
            INSERT INTO daily_pnl (date, total_gross, total_net, total_fees, trade_count)
            SELECT
                date(timestamp) as date,
                COALESCE(SUM(gross_profit), 0) as total_gross,
                COALESCE(SUM(net_profit), 0) as total_net,
                COALESCE(SUM(fees), 0) as total_fees,
                COUNT(*) as trade_count
            FROM trades
            GROUP BY date(timestamp);
        """, transaction: tx);

        await tx.CommitAsync();
    }

    public async Task SaveDailyPortfolioSnapshotAsync(AccountBalance balance, IReadOnlyList<Position> positions)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var broker = balance.Broker.ToString();

        await db.ExecuteAsync("""
            INSERT INTO daily_account_snapshots
            (date, broker, available_ars, available_usd, total_ars, captured_at)
            VALUES
            (@Date, @Broker, @AvailableArs, @AvailableUsd, @TotalArs, datetime('now'))
            ON CONFLICT(date, broker) DO UPDATE SET
                available_ars = excluded.available_ars,
                available_usd = excluded.available_usd,
                total_ars = excluded.total_ars,
                captured_at = datetime('now');
        """, new
        {
            Date = today,
            Broker = broker,
            AvailableArs = balance.AvailableARS,
            AvailableUsd = balance.AvailableUSD,
            TotalArs = balance.TotalARS
        }, tx);

        await db.ExecuteAsync("""
            DELETE FROM daily_position_snapshots
            WHERE date = @Date AND broker = @Broker;
        """, new { Date = today, Broker = broker }, tx);

        foreach (var pos in positions)
        {
            await db.ExecuteAsync("""
                INSERT INTO daily_position_snapshots
                (date, broker, ticker, settlement, buy_size, sell_size, buy_price, sell_price, net_quantity, total_daily_diff, total_diff, captured_at)
                VALUES
                (@Date, @Broker, @Ticker, @Settlement, @BuySize, @SellSize, @BuyPrice, @SellPrice, @NetQuantity, @TotalDailyDiff, @TotalDiff, datetime('now'));
            """, new
            {
                Date = today,
                Broker = broker,
                pos.Ticker,
                Settlement = pos.Settlement.ToString(),
                pos.BuySize,
                pos.SellSize,
                pos.BuyPrice,
                pos.SellPrice,
                NetQuantity = pos.NetQuantity,
                pos.TotalDailyDiff,
                pos.TotalDiff
            }, tx);
        }

        await tx.CommitAsync();
    }

    public async Task<(bool HasAccountSnapshot, int PositionCount)> GetTodayPortfolioSnapshotHealthAsync(BrokerType broker = BrokerType.PPI)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var brokerStr = broker.ToString();

        var hasAccount = await db.ExecuteScalarAsync<long>("""
            SELECT COUNT(1)
            FROM daily_account_snapshots
            WHERE date = @Date AND broker = @Broker;
        """, new { Date = today, Broker = brokerStr });

        var positionCount = await db.ExecuteScalarAsync<long>("""
            SELECT COUNT(1)
            FROM daily_position_snapshots
            WHERE date = @Date AND broker = @Broker;
        """, new { Date = today, Broker = brokerStr });

        return (hasAccount > 0, (int)positionCount);
    }

    public async Task MarkManagedOrderStateAsync(string executionId, ManagedOrderState state, string? brokerOrderId = null, string? error = null)
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        await db.ExecuteAsync("""
            UPDATE active_orders
            SET state = @State, broker_order_id = COALESCE(@BrokerOrderId, broker_order_id), error = COALESCE(@Error, error), updated_at = @UpdatedAt
            WHERE execution_id = @ExecutionId;
        """, new
        {
            ExecutionId = executionId,
            State = state.ToString(),
            BrokerOrderId = brokerOrderId,
            Error = error,
            UpdatedAt = DateTime.UtcNow.ToString("O"),
        });
    }

    public async Task<IReadOnlyList<ManagedOrderSnapshot>> GetActiveManagedOrdersAsync()
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        var rows = await db.QueryAsync("""
            SELECT execution_id, strategy, ticker, side, settlement, quantity, price, state, broker_order_id, error, updated_at
            FROM active_orders
            WHERE state IN ('Created','RiskApproved','Submitted','PartiallyFilled');
        """);

        var results = new List<ManagedOrderSnapshot>();
        foreach (var row in rows)
        {
            if (!Enum.TryParse<ManagedOrderState>((string)row.state, out var state))
                continue;
            if (!Enum.TryParse<OrderSide>((string)row.side, out var side))
                continue;
            if (!Enum.TryParse<Settlement>((string)row.settlement, out var settlement))
                continue;

            results.Add(new ManagedOrderSnapshot
            {
                ExecutionId = row.execution_id,
                StrategyName = row.strategy,
                Ticker = row.ticker,
                Side = side,
                Settlement = settlement,
                Quantity = Convert.ToDecimal(row.quantity),
                Price = Convert.ToDecimal(row.price),
                State = state,
                BrokerOrderId = row.broker_order_id,
                Error = row.error,
                UpdatedAtUtc = DateTime.TryParse((string)row.updated_at, out var parsed) ? parsed : DateTime.UtcNow,
            });
        }
        return results;
    }

    public async Task<bool> HasInFlightManagedOrdersAsync()
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        var count = await db.ExecuteScalarAsync<long>("""
            SELECT COUNT(1)
            FROM active_orders
            WHERE state IN ('Submitted', 'PartiallyFilled');
        """);
        return count > 0;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }
}
