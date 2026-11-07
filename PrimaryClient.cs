using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Core.Resilience;

namespace FinancialTerminal.Brokers.Primary;

/// <summary>
/// Cliente Primary API (Matriz). Implementa market data + cuenta.
/// WebSocket para tiempo real, REST como fallback resiliente.
/// </summary>
public sealed class PrimaryClient : IMarketDataProvider, IAccountProvider, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly PrimarySettings _settings;
    private readonly ResilienceSettings _resilience;
    private readonly ILogger<PrimaryClient> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _wsReconnectLock = new(1, 1);
    private readonly SimpleCircuitBreaker _restCircuit;
    private readonly Random _random = new();

    private string? _accessToken;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private Task? _wsListenTask;
    private bool _connected;

    private IReadOnlyList<string> _lastSubscriptionTickers = [];
    private Settlement _lastSubscriptionSettlement = Settlement.CI;
    private CancellationToken _subscriptionToken = CancellationToken.None;
    private CancellationTokenSource? _fallbackPollingCts;
    private Task? _fallbackPollingTask;

    private readonly Dictionary<string, Quote> _quoteCache = new();
    private readonly object _cacheLock = new();

    public BrokerType ProviderType => BrokerType.Primary;
    public BrokerType BrokerType => BrokerType.Primary;
    public bool IsConnected => _connected && _accessToken is not null;

    public event EventHandler<QuoteUpdateEventArgs>? OnQuoteUpdate;

    public PrimaryClient(
        PrimarySettings settings,
        ResilienceSettings resilience,
        ILogger<PrimaryClient> logger,
        HttpClient? httpClient = null)
    {
        _settings = settings;
        _resilience = resilience;
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _restCircuit = new SimpleCircuitBreaker(
            _resilience.CircuitBreakerFailures,
            TimeSpan.FromSeconds(_resilience.CircuitBreakerBreakSeconds));
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return true;

            var uri = new Uri(new Uri(_settings.BaseUrl), "auth/getToken");
            var response = await ExecuteResilientHttpAsync("PRIMARY auth/getToken", async token =>
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("X-Username", _settings.Username);
                _http.DefaultRequestHeaders.Add("X-Password", _settings.Password);
                return await _http.PostAsync(uri, new StringContent(""), token);
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Primary login failed: {Status}", response.StatusCode);
                return false;
            }

            _accessToken = response.Headers.TryGetValues("X-Auth-Token", out var values)
                ? values.FirstOrDefault()
                : null;

            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("X-Auth-Token", _accessToken);

            _connected = !string.IsNullOrWhiteSpace(_accessToken);
            if (_connected)
                _logger.LogInformation("Primary conectado: {Url}", _settings.BaseUrl);

            return _connected;
        }
        catch (CircuitOpenException ex)
        {
            _logger.LogError("Circuit breaker REST de Primary abierto hasta {UntilUtc}", ex.OpenUntilUtc);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error conectando a Primary");
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        _connected = false;
        _accessToken = null;
        await StopFallbackPollingAsync();
        await StopWebSocketAsync();
        _logger.LogInformation("Primary desconectado");
    }

    public async Task<Quote?> GetQuoteAsync(string ticker, Settlement settlement, CancellationToken ct = default)
    {
        if (!IsConnected && !await ConnectAsync(ct)) return null;

        try
        {
            var symbol = $"MERV - XMEV - {ticker} - {SettlementToString(settlement)}";
            var builder = new UriBuilder(new Uri(new Uri(_settings.BaseUrl), "rest/marketdata/get"));
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["symbol"] = symbol;
            query["marketId"] = "MERV";
            query["entries"] = "BI,OF,LA,OP,CL,SE,OI,HI,LO,TV,NV";
            query["depth"] = "5";
            builder.Query = query.ToString();

            var json = await GetStringWithResilienceAsync("PRIMARY marketdata/get quote", builder.Uri, ct);
            return ParseMarketDataToQuote(json, ticker, settlement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo quote {Ticker}/{Settlement}", ticker, settlement);
            return null;
        }
    }

    public async Task<OrderBook?> GetOrderBookAsync(string ticker, Settlement settlement, CancellationToken ct = default)
    {
        if (!IsConnected && !await ConnectAsync(ct)) return null;

        try
        {
            var symbol = $"MERV - XMEV - {ticker} - {SettlementToString(settlement)}";
            var builder = new UriBuilder(new Uri(new Uri(_settings.BaseUrl), "rest/marketdata/get"));
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["symbol"] = symbol;
            query["marketId"] = "MERV";
            query["entries"] = "BI,OF";
            query["depth"] = "5";
            builder.Query = query.ToString();

            var json = await GetStringWithResilienceAsync("PRIMARY marketdata/get book", builder.Uri, ct);
            return ParseOrderBook(json, ticker, settlement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo book {Ticker}/{Settlement}", ticker, settlement);
            return null;
        }
    }

    public async Task<IReadOnlyList<Quote>> GetQuotesBatchAsync(
        IEnumerable<string> tickers, Settlement settlement, CancellationToken ct = default)
    {
        var tasks = tickers.Select(t => GetQuoteAsync(t, settlement, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(q => q is not null).Cast<Quote>().ToList();
    }

    public async Task SubscribeQuotesAsync(
        IEnumerable<string> tickers, Settlement settlement, CancellationToken ct = default)
    {
        _lastSubscriptionTickers = tickers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lastSubscriptionSettlement = settlement;
        _subscriptionToken = ct;
        await OpenWebSocketAsync(_lastSubscriptionTickers, settlement, ct);
    }

    private async Task OpenWebSocketAsync(
        IReadOnlyList<string> tickers,
        Settlement settlement,
        CancellationToken ct)
    {
        if (!IsConnected && !await ConnectAsync(ct)) return;

        await StopFallbackPollingAsync();
        await StopWebSocketAsync();

        var wsScheme = _settings.BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var host = new Uri(_settings.BaseUrl).Host;
        var wsUri = new Uri($"{wsScheme}://{host}");

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("X-Auth-Token", _accessToken!);
        await _ws.ConnectAsync(wsUri, ct);
        _logger.LogInformation("WebSocket Primary conectado");

        var products = tickers.Select(t => new
        {
            symbol = $"MERV - XMEV - {t} - {SettlementToString(settlement)}",
            marketId = "MERV"
        }).ToArray();

        var subscriptionMsg = JsonSerializer.Serialize(new
        {
            type = "smd",
            level = 1,
            entries = new[] { "BI", "OF", "LA", "SE", "HI", "LO", "TV" },
            products,
            depth = 5
        });

        var bytes = Encoding.UTF8.GetBytes(subscriptionMsg);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _wsListenTask = Task.Run(() => WebSocketListenLoop(_wsCts.Token), _wsCts.Token);
    }

    private async Task WebSocketListenLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("WebSocket Primary cerrado por servidor");
                    await TryReconnectWebSocketAsync();
                    return;
                }

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    ProcessWebSocketMessage(messageBuffer.ToString());
                    messageBuffer.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en WebSocket listen loop");
                await TryReconnectWebSocketAsync();
                return;
            }
        }
    }

    private async Task TryReconnectWebSocketAsync()
    {
        if (_lastSubscriptionTickers.Count == 0 || _subscriptionToken.IsCancellationRequested)
            return;

        await _wsReconnectLock.WaitAsync();
        try
        {
            if (_subscriptionToken.IsCancellationRequested)
                return;

            var maxRetries = Math.Max(1, _resilience.MaxRetries);
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var delay = RetryBackoff.ComputeDelay(attempt, _resilience, _random);
                    await Task.Delay(delay, _subscriptionToken);
                    await OpenWebSocketAsync(_lastSubscriptionTickers, _lastSubscriptionSettlement, _subscriptionToken);
                    _logger.LogWarning("WebSocket Primary reconectado en intento {Attempt}", attempt);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reintento de reconexión WS Primary falló ({Attempt}/{Max})", attempt, maxRetries);
                }
            }

            _logger.LogError("No se pudo reestablecer WS Primary tras {MaxRetries} intentos. Activando polling fallback.", maxRetries);
            StartFallbackPolling();
        }
        finally
        {
            _wsReconnectLock.Release();
        }
    }

    private void StartFallbackPolling()
    {
        if (_lastSubscriptionTickers.Count == 0 || _subscriptionToken.IsCancellationRequested || _fallbackPollingCts is not null)
            return;

        _fallbackPollingCts = CancellationTokenSource.CreateLinkedTokenSource(_subscriptionToken);
        var ct = _fallbackPollingCts.Token;

        _fallbackPollingTask = Task.Run(async () =>
        {
            _logger.LogWarning("Fallback polling Primary activo");
            var intervalMs = Math.Max(1000, _resilience.FallbackPollingIntervalMs);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var quotes = await GetQuotesBatchAsync(_lastSubscriptionTickers, _lastSubscriptionSettlement, ct);
                    foreach (var quote in quotes)
                    {
                        lock (_cacheLock) { _quoteCache[$"{quote.Ticker}:{quote.Settlement}"] = quote; }
                        OnQuoteUpdate?.Invoke(this, new QuoteUpdateEventArgs
                        {
                            Quote = quote,
                            Source = BrokerType.Primary
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error en fallback polling Primary");
                }

                await Task.Delay(intervalMs, ct);
            }
        }, ct);
    }

    private async Task StopFallbackPollingAsync()
    {
        _fallbackPollingCts?.Cancel();
        if (_fallbackPollingTask is not null)
        {
            try { await _fallbackPollingTask; } catch { }
        }
        _fallbackPollingTask = null;
        _fallbackPollingCts?.Dispose();
        _fallbackPollingCts = null;
    }

    private void ProcessWebSocketMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "Md")
            {
                var instrumentId = root.GetProperty("instrumentId");
                var symbol = instrumentId.GetProperty("symbol").GetString() ?? "";
                var parts = symbol.Split(" - ");
                if (parts.Length < 4) return;

                var ticker = parts[2].Trim();
                var settlStr = parts[3].Trim();
                var settlement = StringToSettlement(settlStr);

                var md = root.GetProperty("marketData");
                var quote = ParseMdToQuote(md, ticker, settlement);

                lock (_cacheLock) { _quoteCache[$"{ticker}:{settlement}"] = quote; }

                OnQuoteUpdate?.Invoke(this, new QuoteUpdateEventArgs
                {
                    Quote = quote,
                    Source = BrokerType.Primary
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error parseando WS message");
        }
    }

    private async Task StopWebSocketAsync()
    {
        _wsCts?.Cancel();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch
            {
                // ignored
            }
        }

        _ws?.Dispose();
        _ws = null;
        _wsCts?.Dispose();
        _wsCts = null;
        _wsListenTask = null;
    }

    public Task<AccountBalance> GetBalanceAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new AccountBalance { Broker = BrokerType.Primary });
    }

    public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        if (!IsConnected && !await ConnectAsync(ct)) return [];

        try
        {
            var accountsJson = await GetStringWithResilienceAsync(
                "PRIMARY rest/accounts",
                new Uri(new Uri(_settings.BaseUrl), "rest/accounts"),
                ct);

            using var accountsDoc = JsonDocument.Parse(accountsJson);
            var accounts = accountsDoc.RootElement.GetProperty("accounts");

            var positions = new List<Position>();
            foreach (var acc in accounts.EnumerateArray())
            {
                var accName = acc.GetProperty("name").GetString();
                if (accName is null) continue;

                var posJson = await GetStringWithResilienceAsync(
                    "PRIMARY risk positions",
                    new Uri(new Uri(_settings.BaseUrl), $"rest/risk/position/getPositions/{accName}"),
                    ct);

                using var posDoc = JsonDocument.Parse(posJson);
                if (posDoc.RootElement.GetProperty("status").GetString() == "OK")
                {
                    foreach (var pos in posDoc.RootElement.GetProperty("positions").EnumerateArray())
                    {
                        positions.Add(new Position
                        {
                            Ticker = pos.GetProperty("instrument").GetProperty("symbolReference").GetString() ?? "",
                            BuySize = pos.GetProperty("buySize").GetDecimal(),
                            BuyPrice = pos.GetProperty("buyPrice").GetDecimal(),
                            SellSize = pos.GetProperty("sellSize").GetDecimal(),
                            SellPrice = pos.GetProperty("sellPrice").GetDecimal(),
                            TotalDailyDiff = pos.GetProperty("totalDailyDiff").GetDecimal(),
                            TotalDiff = pos.GetProperty("totalDiff").GetDecimal()
                        });
                    }
                }
            }
            return positions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo posiciones Primary");
            return [];
        }
    }

    public async Task<IReadOnlyList<(DateTime Date, decimal Close, decimal Volume)>> GetHistoricalTradesAsync(
        string ticker, DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (!IsConnected && !await ConnectAsync(ct)) return [];

        try
        {
            var builder = new UriBuilder(new Uri(new Uri(_settings.BaseUrl), "rest/data/getTrades"));
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["marketId"] = "MERV";
            query["symbol"] = ticker;
            query["dateFrom"] = from.ToString("yyyy-MM-dd");
            query["dateTo"] = to.ToString("yyyy-MM-dd");
            query["external"] = "1";
            builder.Query = query.ToString();

            var json = await GetStringWithResilienceAsync("PRIMARY data/getTrades", builder.Uri, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetProperty("status").GetString() == "ERROR") return [];

            var trades = new List<(DateTime, decimal, decimal)>();
            foreach (var trade in root.GetProperty("trades").EnumerateArray())
            {
                var date = trade.GetProperty("datetime").GetDateTime();
                var price = trade.GetProperty("price").GetDecimal();
                var vol = trade.GetProperty("size").GetDecimal();
                trades.Add((date, price, vol));
            }
            return trades;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo histórico {Ticker}", ticker);
            return [];
        }
    }

    private async Task<string> GetStringWithResilienceAsync(string operation, Uri uri, CancellationToken ct)
    {
        return await ExecuteResilientHttpAsync(operation, async token => await _http.GetStringAsync(uri, token), ct);
    }

    private async Task<T> ExecuteResilientHttpAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        _restCircuit.ThrowIfOpen();
        var maxRetries = Math.Max(1, _resilience.MaxRetries);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                FaultInjection.MaybeThrow(_resilience, _random, operation);
                var result = await action(ct);
                _restCircuit.RecordSuccess();
                return result;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                lastError = ex;
                _restCircuit.RecordFailure();
                if (attempt >= maxRetries || _restCircuit.IsOpen)
                    break;

                var delay = RetryBackoff.ComputeDelay(attempt, _resilience, _random);
                _logger.LogWarning(ex, "Primary transient error in {Operation}. Retry {Attempt}/{Max} in {Delay}ms",
                    operation, attempt, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }

        throw new HttpRequestException($"Primary operation failed after retries: {operation}", lastError);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
            return false;

        return ex is HttpRequestException
            or TaskCanceledException
            or TimeoutException
            or CircuitOpenException;
    }

    private static Quote? ParseMarketDataToQuote(string json, string ticker, Settlement settlement)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "OK") return null;

        var md = root.GetProperty("marketData");
        return ParseMdToQuote(md, ticker, settlement);
    }

    private static Quote ParseMdToQuote(JsonElement md, string ticker, Settlement settlement)
    {
        var bid = GetNestedDecimal(md, "BI", 0, "price");
        var ask = GetNestedDecimal(md, "OF", 0, "price");
        var last = GetDecimalProp(md, "LA", "price");

        return new Quote
        {
            Ticker = ticker,
            Settlement = settlement,
            Bid = bid,
            Ask = ask,
            Last = last,
            Open = GetDecimalProp(md, "OP", "price"),
            Close = GetDecimalProp(md, "CL", "price"),
            High = GetDecimalProp(md, "HI", "price"),
            Low = GetDecimalProp(md, "LO", "price"),
            Volume = GetDecimalProp(md, "TV", "size"),
            VolumeAmount = GetDecimalProp(md, "NV", "price"),
            Timestamp = DateTime.UtcNow
        };
    }

    private static OrderBook? ParseOrderBook(string json, string ticker, Settlement settlement)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "OK") return null;

        var md = root.GetProperty("marketData");

        var bids = ParseBookLevels(md, "BI");
        var asks = ParseBookLevels(md, "OF");

        return new OrderBook
        {
            Ticker = ticker,
            Settlement = settlement,
            Bids = bids,
            Asks = asks
        };
    }

    private static List<BookLevel> ParseBookLevels(JsonElement md, string entry)
    {
        var levels = new List<BookLevel>();
        if (!md.TryGetProperty(entry, out var arr)) return levels;

        foreach (var level in arr.EnumerateArray())
        {
            var price = level.TryGetProperty("price", out var p) ? p.GetDecimal() : 0m;
            var qty = level.TryGetProperty("size", out var s) ? s.GetDecimal() : 0m;
            if (price > 0) levels.Add(new BookLevel(price, qty));
        }
        return levels;
    }

    private static decimal GetNestedDecimal(JsonElement parent, string prop, int index, string field)
    {
        if (!parent.TryGetProperty(prop, out var arr)) return 0m;
        var items = arr.EnumerateArray().ToList();
        if (items.Count <= index) return 0m;
        return items[index].TryGetProperty(field, out var val) ? val.GetDecimal() : 0m;
    }

    private static decimal GetDecimalProp(JsonElement parent, string prop, string field)
    {
        if (!parent.TryGetProperty(prop, out var obj)) return 0m;
        if (obj.ValueKind == JsonValueKind.Array)
        {
            var items = obj.EnumerateArray().ToList();
            if (items.Count == 0) return 0m;
            return items[0].TryGetProperty(field, out var v) ? v.GetDecimal() : 0m;
        }
        return obj.TryGetProperty(field, out var val) ? val.GetDecimal() : 0m;
    }

    private static string SettlementToString(Settlement s) => s switch
    {
        Settlement.CI => "CI",
        Settlement.T24hs => "24hs",
        Settlement.T48hs => "48hs",
        _ => "CI"
    };

    private static Settlement StringToSettlement(string s) => s switch
    {
        "CI" => Settlement.CI,
        "24hs" => Settlement.T24hs,
        "48hs" => Settlement.T48hs,
        _ => Settlement.CI
    };

    public async ValueTask DisposeAsync()
    {
        await StopFallbackPollingAsync();
        await StopWebSocketAsync();
        _http.Dispose();
        _connectLock.Dispose();
        _wsReconnectLock.Dispose();
    }
}
