using Android.Content;
using Android.Text;
using Android.Views.InputMethods;
using Android.Webkit;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace MeowSSH.App;

/// <summary>
/// Blazor WebView handler whose Android editor explicitly disables predictive
/// suggestions. HTML attributes such as autocorrect="off" are only hints and
/// Samsung Keyboard may ignore them; EditorInfo is the contract the IME reads.
/// </summary>
public sealed class NoSuggestionsBlazorWebViewHandler : BlazorWebViewHandler
{
    protected override global::Android.Webkit.WebView CreatePlatformView() =>
        new NoSuggestionsWebView(Context!);

    private sealed class NoSuggestionsWebView(Context context) : global::Android.Webkit.WebView(context)
    {
        public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
        {
            var connection = base.OnCreateInputConnection(outAttrs);
            if (outAttrs is null)
                return connection;

            var inputType = outAttrs.InputType;

            // Remove every flag that invites correction/completion and add the
            // explicit no-suggestions flag.
            inputType &= ~InputTypes.TextFlagAutoCorrect;
            inputType &= ~InputTypes.TextFlagAutoComplete;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                inputType &= ~InputTypes.TextFlagEnableTextConversionSuggestions;
            inputType |= InputTypes.TextFlagNoSuggestions;

            // Some OEM IMEs (notably Samsung Keyboard) still compose text even
            // with NO_SUGGESTIONS. Visible-password variation is the strongest
            // standard Android signal for literal, non-predictive text while
            // keeping the characters visible.
            if ((inputType & InputTypes.MaskClass) == InputTypes.ClassText)
            {
                inputType &= ~InputTypes.MaskVariation;
                inputType |= InputTypes.TextVariationVisiblePassword;
            }

            outAttrs.InputType = inputType;
            return connection;
        }
    }
}
