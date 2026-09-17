using Microsoft.JSInterop;

namespace MeowSSH.UI.Pages;

public partial class KeysPage
{
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        await using var module = await Js.InvokeAsync<IJSObjectReference>(
            "import",
            "./_content/MeowSSH.UI/js/credential-draft-guard.js");
        await module.InvokeVoidAsync("installCredentialDraftGuard");
    }
}
