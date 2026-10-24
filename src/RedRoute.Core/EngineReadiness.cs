namespace RedRoute.Core;

public sealed class EngineReadiness
{
    public const string Signal = "RED_ROUTE_READY";
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsReady => completion.Task.IsCompletedSuccessfully;
    public void Observe(string? line) { if (line == Signal) completion.TrySetResult(); }
    public Task WaitAsync(CancellationToken token) => completion.Task.WaitAsync(token);
}
