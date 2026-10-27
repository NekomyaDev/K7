namespace K7.Clients.Shared.Services;

public static class AppReadySignal
{
    private static TaskCompletionSource<bool>? _tcs = new();

    /// <summary>
    /// Optional host hook (MAUI cold-start trace). Invoked once per successful <see cref="Signal"/>.
    /// </summary>
    public static Action? OnSignaled { get; set; }

    public static bool IsSignaled
    {
        get
        {
            var tcs = _tcs;
            return tcs is null || tcs.Task.IsCompleted;
        }
    }

    public static void Signal()
    {
        var tcs = _tcs;
        if (tcs is null)
            return;

        if (tcs.TrySetResult(true))
            OnSignaled?.Invoke();
    }

    public static Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _tcs;
        if (tcs is null) return Task.CompletedTask;
        return tcs.Task.WaitAsync(cancellationToken);
    }

    public static void Reset() => _tcs = new TaskCompletionSource<bool>();
}
