using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MeowSSH.Core.Model;

namespace MeowSSH.UI.Pages;

public partial class HostEditorPage : IAsyncDisposable
{
    private EventCallback _parentCancelled;
    private HostDraftSnapshot? _initialDraft;
    private IJSObjectReference? _draftGuardModule;

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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        _initialDraft = CaptureDraft();
        await EnsureDraftGuardModuleAsync();
    }

    private async Task RequestCancelAsync()
    {
        if (_initialDraft is null || CaptureDraft() == _initialDraft.Value)
        {
            await _parentCancelled.InvokeAsync();
            return;
        }

        var module = await EnsureDraftGuardModuleAsync();
        var discard = await module.InvokeAsync<bool>("confirmDiscard");

        if (discard)
            await _parentCancelled.InvokeAsync();
    }

    private async Task<IJSObjectReference> EnsureDraftGuardModuleAsync() =>
        _draftGuardModule ??= await DraftGuardJs.InvokeAsync<IJSObjectReference>(
            "import",
            "./_content/MeowSSH.UI/js/host-draft-guard.js");

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

    public async ValueTask DisposeAsync()
    {
        if (_draftGuardModule is null) return;

        try { await _draftGuardModule.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }

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
