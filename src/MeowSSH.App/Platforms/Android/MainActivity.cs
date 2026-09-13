using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace MeowSSH.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 15 draws every app targeting SDK 35 or later edge to edge,
        // with no way to opt out, so the web view fills the window including the
        // strip behind the status bar -- which is why the title sat underneath
        // the clock. The bottom edge was already handled, by
        // env(safe-area-inset-bottom) in the stylesheet; the top is not, because
        // Android's web view reports that variable for a display cutout and not
        // for the system bars themselves. So the inset is applied here, where
        // the real measurement is, rather than guessed at in CSS.
        var content = FindViewById(global::Android.Resource.Id.Content);
        if (content is not null) ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsets());
    }

    /// <summary>
    /// Keeps the web view clear of the status bar and any display cutout.
    /// </summary>
    /// <remarks>
    /// The bottom is deliberately left alone. The stylesheet already pads the
    /// tab bar with <c>env(safe-area-inset-bottom)</c> and that works, so
    /// padding here as well would push it up twice.
    /// </remarks>
    private sealed class SystemBarInsets : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        // Fully qualified: MAUI has its own View, and the ambiguity is only
        // resolvable here because this file sees both namespaces.
        public WindowInsetsCompat OnApplyWindowInsets(global::Android.Views.View? view, WindowInsetsCompat? insets)
        {
            if (view is null || insets is null) return insets ?? WindowInsetsCompat.Consumed!;

            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            if (bars is not null) view.SetPadding(bars.Left, bars.Top, bars.Right, view.PaddingBottom);

            // Returned rather than consumed: something else may still need to
            // know where the system bars are, and swallowing them here would
            // silently break it.
            return insets;
        }
    }
}
