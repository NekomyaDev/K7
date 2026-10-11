using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace K7.Clients.Shared.UI.Components;

public partial class K7SettingsActionBar : IAsyncDisposable
{
    private readonly string _heightVariable = "--k7-settings-action-bar-height";
    private ElementReference _root;
    private IJSObjectReference? _module;
    private bool _observed;
    private bool _disposed;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public bool IsDirty { get; set; }
    [Parameter] public bool IsSaving { get; set; }
    [Parameter] public bool ResetDisabled { get; set; }
    [Parameter] public bool ShowResetToDefaults { get; set; } = true;
    [Parameter] public bool ResetRequiresConfirmation { get; set; } = true;
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
    [Parameter] public EventCallback OnResetToDefaults { get; set; }

    private async Task ResetToDefaultsAsync()
    {
        if (IsSaving)
            return;

        if (ResetRequiresConfirmation)
        {
            var confirmed = await DialogService.ShowMessageBoxAsync(
                S["ResetToDefaultsTitle"],
                S["ResetToDefaultsMessage"],
                yesText: S["ResetToDefaults"],
                cancelText: S["Cancel"]);

            if (confirmed is not true)
                return;
        }

        await OnResetToDefaults.InvokeAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _observed || _disposed)
            return;

        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/K7.Clients.Shared.UI/js/settingsActionBar.js");

            if (_disposed)
            {
                await module.DisposeAsync();
                return;
            }

            _module = module;
            await _module.InvokeVoidAsync("observe", _root, _heightVariable);
            _observed = true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var module = _module;
        _module = null;

        if (module is null)
            return;

        try
        {
            if (_observed)
                await module.InvokeVoidAsync("unobserve", _root, _heightVariable);

            await module.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException)
        {
        }
    }
}
