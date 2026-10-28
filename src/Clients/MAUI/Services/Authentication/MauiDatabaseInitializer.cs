namespace K7.Clients.MAUI.Services.Authentication;

/// <summary>
/// Reserved MAUI scoped init hook. OpenIddict EnsureCreated runs from
/// <c>MauiSessionBootstrap.EnsureHeavyWorkAsync</c> after first paint (or headless EnsureReady).
/// </summary>
public class MauiDatabaseInitializer : IMauiInitializeScopedService
{
    public void Initialize(IServiceProvider services)
    {
    }
}
