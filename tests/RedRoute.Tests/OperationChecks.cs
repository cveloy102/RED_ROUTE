using RedRoute.Core;

internal static class OperationChecks
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        {
            var entered = Signal(); var cleanupEntered = Signal(); var finishCleanup = Signal();
            await using var queue = new OperationQueue(default, async _ => { cleanupEntered.TrySetResult(); await finishCleanup.Task; });
            var first = queue.Schedule(async t => { entered.SetResult(); await Task.Delay(Timeout.Infinite, t); }, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var skipped = false;
            await queue.Schedule(_ => { skipped = true; return Task.CompletedTask; }, false);
            assert(!skipped && !first.IsCompleted, "Periodic health checks do not preempt an active command");
            var second = queue.Schedule(_ => Task.CompletedTask, true);
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var shutdown = queue.ShutdownAsync();
            await second.WaitAsync(TimeSpan.FromSeconds(3));
            assert(!shutdown.IsCompleted && !first.IsCompleted, "Service shutdown waits for old cancellation cleanup even if the newest task already finished");
            finishCleanup.SetResult(); await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
            assert(first.IsCompleted && second.IsCompleted, "Service shutdown drains every scheduled operation before disposing the engine");
            var ranAfterShutdown = false;
            await queue.Schedule(_ => { ranAfterShutdown = true; return Task.CompletedTask; }, true);
            assert(!ranAfterShutdown, "No tunnel operation can start after service shutdown");
        }
        {
            var events = new List<string>();
            await using var queue = new OperationQueue(default, code => { events.Add(code); throw new IOException("Synthetic cleanup failure"); });
            await queue.Schedule(_ => throw new IOException("Synthetic engine failure"), true);
            var recovered = false; await queue.Schedule(_ => { recovered = true; return Task.CompletedTask; }, false);
            assert(recovered && events.SequenceEqual(new[] { "operation_failed_IOException" }), "Engine and cleanup failures leave the scheduler alive for recovery");
        }
        {
            var running = 0; var maximum = 0; var completed = 0;
            await using var queue = new OperationQueue(default, _ => Task.CompletedTask);
            var tasks = new List<Task>();
            for (var i = 0; i < 1000; i++)
                tasks.Add(queue.Schedule(async t =>
                {
                    var count = Interlocked.Increment(ref running); maximum = Math.Max(maximum, count);
                    try { await Task.Yield(); t.ThrowIfCancellationRequested(); completed++; }
                    finally { Interlocked.Decrement(ref running); }
                }, true));
            var finalRan = false;
            await queue.Schedule(_ => { finalRan = true; return Task.CompletedTask; }, true).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            assert(maximum <= 1 && running == 0 && finalRan, "A burst of 1000 mode commands stays serialized and executes the final command");
            assert(completed <= 1000, "Preempted commands complete without leaking asynchronous work");
        }
    }
}
