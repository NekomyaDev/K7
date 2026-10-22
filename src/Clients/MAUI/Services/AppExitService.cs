using K7.Clients.Shared.Interfaces;

namespace K7.Clients.MAUI.Services;

public sealed class AppExitService : IAppExitService
{
    public void Exit()
    {
#if ANDROID
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not null)
            {
                activity.FinishAffinity();
                return;
            }
        }
        catch
        {
            // Fall through to Quit.
        }
#endif
#if LINUX
        // Application.Current.Quit() is a no-op on the labs GTK host: close the native
        // window (and quit the Gtk.Application) so double-Escape on Home actually exits.
        try
        {
            if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()
                    ?.Handler?.PlatformView is Gtk.Window gtkWindow)
            {
                gtkWindow.Close();
                return;
            }
        }
        catch
        {
        }

        try
        {
            Gtk.Application.GetDefault()?.Quit();
            return;
        }
        catch
        {
        }
#endif
        Microsoft.Maui.Controls.Application.Current?.Quit();
#if LINUX
        Environment.Exit(0);
#endif
    }
}
