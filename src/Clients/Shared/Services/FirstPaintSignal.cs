namespace K7.Clients.Shared.Services;

/// <summary>
/// Fired when the first Blazor layout has painted (EmptyLayout / MainLayout OnAfterRender).
/// Heavier than <see cref="AppReadySignal"/> which may fire at page OnInitialized.
/// MAUI uses this to start OpenIddict/EF so they do not contend with first paint JIT.
/// </summary>
public static class FirstPaintSignal
{
    private static TaskCompletionSource<bool>? _tcs = new();

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
        if (tcs is null)
            return Task.CompletedTask;
        return tcs.Task.WaitAsync(cancellationToken);
    }

    public static void Reset() => _tcs = new TaskCompletionSource<bool>();
}
