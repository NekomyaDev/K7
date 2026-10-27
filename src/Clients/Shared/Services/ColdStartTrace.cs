namespace K7.Clients.Shared.Services;

/// <summary>
/// Optional host hook for cold-start markers (MAUI wires to logcat).
/// </summary>
public static class ColdStartTrace
{
    public static Action<string>? Mark { get; set; }

    public static void Write(string name) => Mark?.Invoke(name);
}
