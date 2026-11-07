using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Core.Resilience;

namespace FinancialTerminal.Brokers.PPI;

/// <summary>
/// Cliente PPI (Portfolio Personal). Broker principal para operaciones.
/// Implementa IBroker completo: market data (polling), órdenes, y cuenta.
/// </summary>
public sealed class PPIClient : IBroker, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly PPISettings _settings;
    private readonly ResilienceSettings _resilience;
    private readonly ILogger<PPIClient> _logger;
    private readonly SemaphoreSlim _rateLimiter;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SimpleCircuitBreaker _httpCircuit;
    private readonly Random _random = new();

    private readonly ConcurrentDictionary<string, AssetType> _tickerTypes = new(StringComparer.OrdinalIgnoreCase);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private bool _connected;
    private CancellationTokenSource? _pollingCts;
    private List<(string Ticker, Settlement Settlement)> _subscriptions = [];

    public string Name => "PPI";
    public BrokerType ProviderType => BrokerType.PPI;
    public BrokerType BrokerType => BrokerType.PPI;
    public bool IsConnected => _connected && _accessToken is not null && DateTime.UtcNow < _tokenExpiry;

    public event EventHandler<QuoteUpdateEventArgs>? OnQuoteUpdate;

    private static readonly Dictionary<Settlement, string> SettlementMap = new()
    {
        [Settlement.CI] = "INMEDIATA",
        [Settlement.T24hs] = "A-24HS",
        [Settlement.T48hs] = "A-48HS"
    };

    private static readonly Dictionary<OrderSide, string> SideMap = new()
    {
        [OrderSide.Buy] = "Compra",
        [OrderSide.Sell] = "Venta"
    };

    public PPIClient(
        PPISettings settings,
        ResilienceSettings resilience,
        ILogger<PPIClient> logger,
        HttpClient? httpClient = null)
    {
        _settings = settings;
        _resilience = resilience;
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _rateLimiter = new SemaphoreSlim(settings.RateLimitPerMinute, settings.RateLimitPerMinute);
        _httpCircuit = new SimpleCircuitBreaker(
            _resilience.CircuitBreakerFailures,
            TimeSpan.FromSeconds(_resilience.CircuitBreakerBreakSeconds));

        _ = Task.Run(async () =>
        {
            while (!_lifetimeCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(60_000 / Math.Max(1, settings.RateLimitPerMinute), _lifetimeCts.Token);
                    if (_rateLimiter.CurrentCount < settings.RateLimitPerMinute)
                        _rateLimiter.Release();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _lifetimeCts.Token);
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return true;

            var baseUrl = _settings.ActiveBaseUrl.TrimEnd('/');
            var response = await PostLoginAsync($"{baseUrl}/Account/LoginApi", ct);
            if (response.TryGetProperty("accessToken", out var tokenProp))
            {
                _accessToken = tokenProp.GetString();
                _refreshToken = response.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null;
                _tokenExpiry = DateTime.UtcNow.AddHours(23);

                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                if (!string.IsNullOrWhiteSpace(_settings.AuthorizedClient))
                    _http.DefaultRequestHeaders.TryAddWithoutValidation("AuthorizedClient", _settings.AuthorizedClient);
                if (!string.IsNullOrWhiteSpace(_settings.ClientKey))
                    _http.DefaultRequestHeaders.TryAddWithoutValidation("ClientKey", _settings.ClientKey);
                _connected = true;
                _logger.LogInformation("PPI conectado ({Mode})", _settings.UseSandbox ? "Sandbox" : "Production");

                if (string.IsNullOrWhiteSpace(_settings.AccountNumber))
                    await AutoDetectAccountAsync(ct);

                return true;
            }

            _logger.LogError("PPI login failed: no accessToken en respuesta");
            return false;
        }
        catch (CircuitOpenException ex)
        {
            _logger.LogError("Circuit breaker abierto para PPI hasta {UntilUtc}", ex.OpenUntilUtc);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error conectando a PPI");
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        _accessToken = null;
        StopPolling();
        _logger.LogInformation("PPI desconectado");
        return Task.CompletedTask;
    }

    private async Task RefreshTokenIfNeeded(CancellationToken ct)
    {
        if (DateTime.UtcNow < _tokenExpiry.AddSeconds(-_settings.TokenRenewalSecondsBeforeExpiry))
            return;

        if (_refreshToken is null)
        {
            await ConnectAsync(ct);
            return;
        }

        try
        {
            var baseUrl = _settings.ActiveBaseUrl.TrimEnd('/');
            var payload = new { RefreshToken = _refreshToken };
            var response = await PostJsonAsync<JsonElement>($"{baseUrl}/Account/RefreshToken", payload, ct);

            if (response.TryGetProperty("accessToken", out var tokenProp))
            {
                _accessToken = tokenProp.GetString();
                _refreshToken = response.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : _refreshToken;
                _tokenExpiry = DateTime.UtcNow.AddHours(23);
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                _logger.LogDebug("PPI token renovado");
            }
        }
        catch
        {
            await ConnectAsync(ct);
        }
    }

    private async Task AutoDetectAccountAsync(CancellationToken ct)
    {
        try
        {
            var accounts = await GetAsync<JsonElement>("Account/Accounts", ct);
            if (accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var acc in accounts.EnumerateArray())
                {
                    var num = acc.TryGetProperty("accountNumber", out var n) ? n.GetString() : null;
                    if (num is not null)
                    {
                        _settings.AccountNumber = num;
                        _logger.LogInformation("Account autodetectado: {Account}", num);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo autodetectar account PPI");
        }
    }

    public async Task<Quote?> GetQuoteAsync(string ticker, Settlement settlement, CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var sdkSettlement = SettlementMap.GetValueOrDefault(settlement, "A-24HS");

            var instrumentType = GetInstrumentType(ticker);
            var current = await GetAsync<JsonElement>(
                $"MarketData/Current?ticker={ticker}&type={instrumentType}&settlement={sdkSettlement}", ct);

            var last = GetDecimal(current, "price");
            var volume = GetDecimal(current, "volume");
            var change = GetDecimal(current, "change");
            var open = GetDecimal(current, "openingPrice");
            var high = GetDecimal(current, "max");
            var low = GetDecimal(current, "min");
            var close = GetDecimal(current, "previousClose");

            decimal bid = last, ask = last;
            try
            {
                var book = await GetAsync<JsonElement>(
                    $"MarketData/Book?ticker={ticker}&type={instrumentType}&settlement={sdkSettlement}", ct);

                if (book.TryGetProperty("bids", out var bids) && bids.ValueKind == JsonValueKind.Array)
                {
                    var firstBid = bids.EnumerateArray().FirstOrDefault();
                    if (firstBid.ValueKind != JsonValueKind.Undefined)
                        bid = GetDecimal(firstBid, "price");
                }
                if (book.TryGetProperty("offers", out var offers) && offers.ValueKind == JsonValueKind.Array)
                {
                    var firstOffer = offers.EnumerateArray().FirstOrDefault();
                    if (firstOffer.ValueKind != JsonValueKind.Undefined)
                        ask = GetDecimal(firstOffer, "price");
                }
            }
            catch
            {
                // fallback al last
            }

            return new Quote
            {
                Ticker = ticker,
                Settlement = settlement,
                Bid = bid,
                Ask = ask,
                Last = last,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                ChangePercent = change,
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo quote PPI {Ticker}/{Settlement}", ticker, settlement);
            return null;
        }
    }

    public async Task<OrderBook?> GetOrderBookAsync(string ticker, Settlement settlement, CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var sdkSettlement = SettlementMap.GetValueOrDefault(settlement, "A-24HS");
            var instrumentType = GetInstrumentType(ticker);
            var book = await GetAsync<JsonElement>(
                $"MarketData/Book?ticker={ticker}&type={instrumentType}&settlement={sdkSettlement}", ct);

            var bids = new List<BookLevel>();
            var asks = new List<BookLevel>();

            if (book.TryGetProperty("bids", out var bidsArr))
                foreach (var level in bidsArr.EnumerateArray())
                    bids.Add(new BookLevel(GetDecimal(level, "price"), GetDecimal(level, "quantity")));

            if (book.TryGetProperty("offers", out var offersArr))
                foreach (var level in offersArr.EnumerateArray())
                    asks.Add(new BookLevel(GetDecimal(level, "price"), GetDecimal(level, "quantity")));

            return new OrderBook
            {
                Ticker = ticker,
                Settlement = settlement,
                Bids = bids,
                Asks = asks
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo book PPI {Ticker}/{Settlement}", ticker, settlement);
            return null;
        }
    }

    public async Task<IReadOnlyList<Quote>> GetQuotesBatchAsync(
        IEnumerable<string> tickers, Settlement settlement, CancellationToken ct = default)
    {
        var quotes = new List<Quote>();
        foreach (var ticker in tickers)
        {
            var quote = await GetQuoteAsync(ticker, settlement, ct);
            if (quote is not null) quotes.Add(quote);
        }
        return quotes;
    }

    public Task SubscribeQuotesAsync(
        IEnumerable<string> tickers, Settlement settlement, CancellationToken ct = default)
    {
        _subscriptions = tickers.Select(t => (t, settlement)).ToList();
        StartPolling(ct);
        return Task.CompletedTask;
    }

    private void StartPolling(CancellationToken externalCt)
    {
        StopPolling();
        _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _lifetimeCts.Token);
        var ct = _pollingCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var (ticker, settlement) in _subscriptions)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var quote = await GetQuoteAsync(ticker, settlement, ct);
                        if (quote is not null)
                        {
                            OnQuoteUpdate?.Invoke(this, new QuoteUpdateEventArgs
                            {
                                Quote = quote,
                                Source = BrokerType.PPI
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error en polling quote {Ticker}", ticker);
                    }
                }

                var delayMs = Math.Max(1000, _resilience.FallbackPollingIntervalMs);
                await Task.Delay(delayMs, ct);
            }
        }, ct);
    }

    private void StopPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
    }

    public async Task<OrderResult> SendOrderAsync(OrderRequest order, CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var sdkSettlement = SettlementMap.GetValueOrDefault(order.Settlement, "A-24HS");
            var sdkSide = SideMap.GetValueOrDefault(order.Side, "Compra");
            var sdkInstrumentType = MapInstrumentType(order.AssetType);

            var budgetPayload = new
            {
                accountNumber = _settings.AccountNumber,
                quantity = (int)Math.Max(1, Math.Round(order.Quantity)),
                price = order.Price,
                ticker = order.Ticker,
                instrumentType = sdkInstrumentType,
                quantityType = "PAPELES",
                operationType = "PRECIO-LIMITE",
                operationTerm = "HASTA-SU-EJECUCIÓN",
                condition = (string?)null,
                operation = sdkSide,
                settlement = sdkSettlement
            };

            var budget = await PostJsonAsync<JsonElement>("Order/Budget", budgetPayload, ct);

            var disclaimers = new List<object>();
            if (budget.TryGetProperty("disclaimers", out var disc) && disc.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in disc.EnumerateArray())
                {
                    var code = d.TryGetProperty("code", out var c) ? c.GetString() : "";
                    disclaimers.Add(new { code, accepted = true });
                }
            }

            var confirmPayload = new
            {
                accountNumber = _settings.AccountNumber,
                quantity = (int)Math.Max(1, Math.Round(order.Quantity)),
                price = order.Price,
                ticker = order.Ticker,
                instrumentType = sdkInstrumentType,
                quantityType = "PAPELES",
                operationType = "PRECIO-LIMITE",
                operationTerm = "HASTA-SU-EJECUCIÓN",
                condition = (string?)null,
                operation = sdkSide,
                settlement = sdkSettlement,
                disclaimers,
                validityDate = (string?)null
            };

            var confirmation = await PostJsonAsync<JsonElement>("Order/Confirm", confirmPayload, ct);

            var orderId = confirmation.TryGetProperty("id", out var idProp) ? idProp.ToString() : "";
            var status = MapOrderStatus(confirmation.TryGetProperty("status", out var st) ? st.GetString() : "");
            var execPrice = GetDecimal(confirmation, "price", order.Price);
            var execQty = GetDecimal(confirmation, "quantity", order.Quantity);

            _logger.LogInformation("Orden PPI confirmada: {Ticker} {Side} {Settlement} id={Id}",
                order.Ticker, sdkSide, sdkSettlement, orderId);

            return new OrderResult
            {
                Success = true,
                Broker = BrokerType.PPI,
                OrderId = orderId,
                Ticker = order.Ticker,
                Side = order.Side,
                Settlement = order.Settlement,
                ExecutedPrice = execPrice,
                ExecutedQuantity = execQty,
                Status = status
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando orden PPI {Ticker}", order.Ticker);
            return new OrderResult
            {
                Success = false,
                Broker = BrokerType.PPI,
                Ticker = order.Ticker,
                Side = order.Side,
                Settlement = order.Settlement,
                ErrorMessage = ex.Message,
                Status = OrderStatus.Rejected
            };
        }
    }

    public async Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var payload = new
            {
                orderID = int.Parse(orderId),
                accountNumber = _settings.AccountNumber,
                externalID = (string?)null
            };
            await PostJsonAsync<JsonElement>("Order/Cancel", payload, ct);
            _logger.LogInformation("Orden PPI cancelada: {OrderId}", orderId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo cancelar orden PPI {OrderId}", orderId);
            return false;
        }
    }

    public async Task<OrderResult?> GetOrderStatusAsync(string orderId, CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var detail = await GetAsync<JsonElement>(
                $"Order/Detail?accountNumber={_settings.AccountNumber}&orderID={orderId}", ct);

            var status = MapOrderStatus(detail.TryGetProperty("status", out var st) ? st.GetString() : "");
            var ticker = detail.TryGetProperty("ticker", out var t) ? t.GetString() ?? "" : "";
            var operation = detail.TryGetProperty("operation", out var op) ? op.GetString() ?? "" : "";
            var side = operation.StartsWith("Compra", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Buy : OrderSide.Sell;

            return new OrderResult
            {
                Success = true,
                Broker = BrokerType.PPI,
                OrderId = orderId,
                Ticker = ticker,
                Side = side,
                ExecutedPrice = GetDecimal(detail, "price"),
                ExecutedQuantity = GetDecimal(detail, "quantity"),
                Status = status
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener estado orden PPI {OrderId}", orderId);
            return null;
        }
    }

    public async Task<IReadOnlyList<OrderResult>> GetActiveOrdersAsync(CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var orders = await GetAsync<JsonElement>(
                $"Order/ActiveOrders?accountNumber={_settings.AccountNumber}", ct);

            var results = new List<OrderResult>();
            if (orders.ValueKind == JsonValueKind.Array)
            {
                foreach (var order in orders.EnumerateArray())
                {
                    results.Add(new OrderResult
                    {
                        Success = true,
                        Broker = BrokerType.PPI,
                        OrderId = order.TryGetProperty("id", out var id) ? id.ToString() : "",
                        Ticker = order.TryGetProperty("ticker", out var t) ? t.GetString() ?? "" : "",
                        Status = MapOrderStatus(order.TryGetProperty("status", out var st) ? st.GetString() : ""),
                        ExecutedPrice = GetDecimal(order, "price"),
                        ExecutedQuantity = GetDecimal(order, "quantity")
                    });
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo órdenes activas PPI");
            return [];
        }
    }

    public async Task<AccountBalance> GetBalanceAsync(CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var balances = await GetAsync<JsonElement>(
                $"Account/AvailableBalance?accountNumber={_settings.AccountNumber}", ct);

            decimal availableArs = 0, availableUsd = 0;
            if (balances.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in balances.EnumerateArray())
                {
                    var name = (b.TryGetProperty("name", out var n) ? n.GetString() : "") ?? "";
                    var symbol = (b.TryGetProperty("symbol", out var s) ? s.GetString() : "") ?? "";
                    var amount = GetDecimal(b, "amount");

                    if (name.Contains("PESO", StringComparison.OrdinalIgnoreCase) || symbol == "$")
                        availableArs += amount;
                    else if (name.Contains("DOLAR", StringComparison.OrdinalIgnoreCase) || symbol == "US$")
                        availableUsd += amount;
                }
            }

            return new AccountBalance
            {
                AvailableARS = availableArs,
                AvailableUSD = availableUsd,
                Broker = BrokerType.PPI
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo balance PPI");
            return new AccountBalance { Broker = BrokerType.PPI };
        }
    }

    public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        try
        {
            var data = await GetAsync<JsonElement>(
                $"Account/BalancesAndPositions?accountNumber={_settings.AccountNumber}", ct);

            var positions = new List<Position>();
            if (data.TryGetProperty("positions", out var posArr) && posArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var pos in posArr.EnumerateArray())
                {
                    positions.Add(new Position
                    {
                        Ticker = pos.TryGetProperty("ticker", out var t) ? t.GetString() ?? "" : "",
                        BuySize = GetDecimal(pos, "quantity"),
                        BuyPrice = GetDecimal(pos, "averagePrice"),
                        TotalDailyDiff = GetDecimal(pos, "dailyReturn"),
                        TotalDiff = GetDecimal(pos, "totalReturn")
                    });
                }
            }
            return positions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo posiciones PPI");
            return [];
        }
    }

    private async Task EnsureConnected(CancellationToken ct)
    {
        if (!IsConnected) await ConnectAsync(ct);
        else await RefreshTokenIfNeeded(ct);
    }

    /// <summary>
    /// Registra el tipo de instrumento para un ticker, usado por GetQuoteAsync/GetOrderBookAsync
    /// para enviar el parámetro 'type' correcto a la API de PPI.
    /// </summary>
    public void RegisterTickerType(string ticker, AssetType assetType) =>
        _tickerTypes[ticker] = assetType;

    private string GetInstrumentType(string ticker) =>
        _tickerTypes.TryGetValue(ticker, out var t) ? MapInstrumentType(t) : "ACCIONES";

    private async Task<T> GetAsync<T>(string endpoint, CancellationToken ct)
    {
        return await ExecuteResilientHttpAsync("GET " + endpoint, async token =>
        {
            await _rateLimiter.WaitAsync(token);
            var baseUrl = _settings.ActiveBaseUrl.TrimEnd('/');
            var url = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"{baseUrl}/{endpoint}";
            var response = await _http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(token);
            return JsonSerializer.Deserialize<T>(json, JsonOpts)!;
        }, ct);
    }

    /// <summary>
    /// Login especial: PPI espera ApiKey/ApiSecret como headers HTTP, no en el body.
    /// AuthorizedClient y ClientKey también van como headers.
    /// </summary>
    private async Task<JsonElement> PostLoginAsync(string url, CancellationToken ct)
    {
        return await ExecuteResilientHttpAsync("POST Login", async token =>
        {
            await _rateLimiter.WaitAsync(token);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("ApiKey", _settings.Username);
            request.Headers.Add("ApiSecret", _settings.Password);
            if (!string.IsNullOrWhiteSpace(_settings.AuthorizedClient))
                request.Headers.Add("AuthorizedClient", _settings.AuthorizedClient);
            if (!string.IsNullOrWhiteSpace(_settings.ClientKey))
                request.Headers.Add("ClientKey", _settings.ClientKey);
            request.Content = new StringContent("", Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(token);
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        }, ct);
    }

    private async Task<T> PostJsonAsync<T>(string endpoint, object payload, CancellationToken ct)
    {
        return await ExecuteResilientHttpAsync("POST " + endpoint, async token =>
        {
            await _rateLimiter.WaitAsync(token);
            var baseUrl = _settings.ActiveBaseUrl.TrimEnd('/');
            var url = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"{baseUrl}/{endpoint}";

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(payload, JsonOpts),
                Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(url, jsonContent, token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(token);
            return JsonSerializer.Deserialize<T>(json, JsonOpts)!;
        }, ct);
    }

    private async Task<T> ExecuteResilientHttpAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        _httpCircuit.ThrowIfOpen();
        var maxRetries = Math.Max(1, _resilience.MaxRetries);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                FaultInjection.MaybeThrow(_resilience, _random, operation);
                var result = await action(ct);
                _httpCircuit.RecordSuccess();
                return result;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                lastError = ex;
                _httpCircuit.RecordFailure();
                if (attempt >= maxRetries || _httpCircuit.IsOpen)
                    break;

                var delay = RetryBackoff.ComputeDelay(attempt, _resilience, _random);
                _logger.LogWarning(ex, "PPI transient error in {Operation}. Retry {Attempt}/{Max} in {Delay}ms",
                    operation, attempt, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }

        throw new HttpRequestException($"PPI operation failed after retries: {operation}", lastError);
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

    private static decimal GetDecimal(JsonElement el, string prop, decimal fallback = 0m)
    {
        if (!el.TryGetProperty(prop, out var val)) return fallback;
        return val.ValueKind switch
        {
            JsonValueKind.Number => val.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(val.GetString(), out var d) ? d : fallback,
            _ => fallback
        };
    }

    private static OrderStatus MapOrderStatus(string? status) => (status ?? "").ToUpper().Replace("-", "_") switch
    {
        "NEW" or "PENDING_NEW" => OrderStatus.New,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELLED" or "CANCELED" or "PENDING_CANCEL" => OrderStatus.Cancelled,
        "REJECTED" => OrderStatus.Rejected,
        _ => OrderStatus.New
    };

    private static string MapInstrumentType(AssetType assetType) => assetType switch
    {
        AssetType.Bono or AssetType.Letra => "BONOS",
        AssetType.ON => "OBLIGACIONES-NEGOCIABLES",
        AssetType.Opcion => "OPCIONES",
        AssetType.Cedear => "CEDEARS",
        AssetType.Caucion => "CAUCIONES",
        AssetType.FCI => "FCI",
        _ => "ACCIONES"
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async ValueTask DisposeAsync()
    {
        StopPolling();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _http.Dispose();
        _connectLock.Dispose();
        _rateLimiter.Dispose();
        await Task.CompletedTask;
    }
}
