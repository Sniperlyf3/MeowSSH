using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MeowSSH.Core.Model;

namespace MeowSSH.UI.Pages;

public partial class HostEditorPage
{
    private EventCallback _parentCancelled;
    private HostDraftSnapshot? _initialDraft;

    [Inject]
    private IJSRuntime DraftGuardJs { get; set; } = default!;

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        if (parameters.TryGetValue<EventCallback>(nameof(OnCancelled), out var incomingCancelled))
            _parentCancelled = incomingCancelled;

        await base.SetParametersAsync(parameters);

        // Keep the existing Razor markup untouched while routing its Cancel button
        // through the draft guard. Parent re-renders can supply a fresh callback,
        // so this assignment is intentionally repeated after every parameter set.
        OnCancelled = EventCallback.Factory.Create(this, RequestCancelAsync);
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            _initialDraft = CaptureDraft();

        return Task.CompletedTask;
    }

    private async Task RequestCancelAsync()
    {
        if (_initialDraft is null || CaptureDraft() == _initialDraft.Value)
        {
            await _parentCancelled.InvokeAsync();
            return;
        }

        var discard = await DraftGuardJs.InvokeAsync<bool>(
            "confirm",
            "Discard unsaved connection changes?");

        if (discard)
            await _parentCancelled.InvokeAsync();
    }

    private HostDraftSnapshot CaptureDraft() => new(
        _label,
        _group,
        _tags,
        _isFavorite,
        _address,
        _username,
        _port,
        _credentialId,
        _jumpHostId,
        _proxyUrl,
        _protocol,
        _transport,
        _autoReconnect,
        _forwardAgent,
        _serialBaudRate,
        _serialDataBits,
        _serialParity,
        _serialStopBits);

    private readonly record struct HostDraftSnapshot(
        string Label,
        string Group,
        string Tags,
        bool IsFavorite,
        string Address,
        string Username,
        int Port,
        string CredentialId,
        string JumpHostId,
        string ProxyUrl,
        HostProtocol Protocol,
        SshTransport Transport,
        bool AutoReconnect,
        bool ForwardAgent,
        int SerialBaudRate,
        int SerialDataBits,
        SerialParity SerialParity,
        SerialStopBits SerialStopBits);
}
