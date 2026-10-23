namespace RedRoute.Core;

/// <summary>Serializes tunnel operations, preempts stale work and drains every operation before shutdown.</summary>
public sealed class OperationQueue(CancellationToken stop, Func<string, Task> interrupted) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Task> pending = [];
    private CancellationTokenSource? active;
    private bool stopping;
    private Task? shutdown;
    public Task Schedule(Func<CancellationToken, Task> action, bool preempt)
    {
        lock (sync)
        {
            if (stopping || (!preempt && active != null)) return Task.CompletedTask;
            active?.Cancel();
            var source = CancellationTokenSource.CreateLinkedTokenSource(stop);
            active = source;
            var task = Task.Run(async () =>
            {
                var held = false;
                try
                {
                    await gate.WaitAsync(source.Token); held = true;
                    await action(source.Token);
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested)
                {
                    if (held) await InterruptSafely("operation_cancelled");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (held) await InterruptSafely("operation_failed_" + ex.GetType().Name);
                }
                finally
                {
                    if (held) gate.Release();
                    lock (sync) { if (ReferenceEquals(active, source)) active = null; source.Dispose(); }
                }
            }, CancellationToken.None);
            pending.RemoveAll(t => t.IsCompleted); pending.Add(task);
            return task;
        }
    }
    private async Task InterruptSafely(string code)
    {
        try { await interrupted(code); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Cleanup failure must not stop the queue. */ }
    }
    public Task ShutdownAsync()
    {
        lock (sync)
        {
            if (shutdown != null) return shutdown;
            stopping = true; active?.Cancel();
            return shutdown = Task.WhenAll(pending.ToArray());
        }
    }
    public async ValueTask DisposeAsync() { await ShutdownAsync(); gate.Dispose(); }
}
