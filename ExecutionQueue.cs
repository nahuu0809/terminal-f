using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.App.Trading;

public sealed class ExecutionQueue : IExecutionQueue, IAsyncDisposable
{
    private readonly Channel<QueuedIntent> _channel;
    private readonly IOrderExecutor _orderExecutor;
    private readonly ILogger<ExecutionQueue> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private int _pendingCount;

    public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    public ExecutionQueue(IOrderExecutor orderExecutor, ILogger<ExecutionQueue> logger)
    {
        _orderExecutor = orderExecutor;
        _logger = logger;
        _channel = Channel.CreateBounded<QueuedIntent>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _consumer = Task.Run(ProcessLoopAsync, _cts.Token);
    }

    public async Task<OrderResult> EnqueueAsync(TradeIntent intent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<OrderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new QueuedIntent(intent, tcs, DateTime.UtcNow);

        Interlocked.Increment(ref _pendingCount);
        try
        {
            await _channel.Writer.WriteAsync(queued, ct);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingCount);
            throw;
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    try
                    {
                        if (!_orderExecutor.IsConnected)
                        {
                            await _orderExecutor.ConnectAsync(_cts.Token);
                        }

                        var result = await _orderExecutor.SendOrderAsync(new OrderRequest
                        {
                            Ticker = item.Intent.Ticker,
                            Side = item.Intent.Side,
                            Settlement = item.Intent.Settlement,
                            Quantity = item.Intent.Quantity,
                            Price = item.Intent.Price,
                            AssetType = item.Intent.AssetType
                        }, _cts.Token);

                        item.Completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException oce)
                    {
                        item.Completion.TrySetCanceled(oce.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ExecutionQueue fallo para {Ticker} {Side}", item.Intent.Ticker, item.Intent.Side);
                        item.Completion.TrySetResult(new OrderResult
                        {
                            Success = false,
                            Broker = _orderExecutor.BrokerType,
                            Ticker = item.Intent.Ticker,
                            Side = item.Intent.Side,
                            Settlement = item.Intent.Settlement,
                            Status = OrderStatus.Rejected,
                            ErrorMessage = ex.Message
                        });
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingCount);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            await _consumer;
        }
        catch
        {
            // ignored during shutdown
        }
        _cts.Dispose();
    }

    private sealed record QueuedIntent(
        TradeIntent Intent,
        TaskCompletionSource<OrderResult> Completion,
        DateTime EnqueuedAtUtc);
}
