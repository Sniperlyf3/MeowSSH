using MeowSSH.UI.Services;
using Microsoft.Maui.ApplicationModel;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace MeowSSH.App;

public sealed class AndroidQrScanner : IQrScanner
{
    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
            throw new InvalidOperationException("Camera permission is required to scan a Tailcat QR code.");
        if (!BarcodeScanning.IsSupported)
            throw new NotSupportedException("This device does not expose a camera that can scan QR codes.");

        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var app = Application.Current ?? throw new InvalidOperationException("No active application is available for QR scanning.");
            if (app.Windows.Count == 0) throw new InvalidOperationException("No active application window is available for QR scanning.");
            var window = app.Windows[0];
            var hostPage = window.Page ?? throw new InvalidOperationException("No active application page is available for QR scanning.");
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = new CameraBarcodeReaderView
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                CameraLocation = CameraLocation.Rear,
                Options = new BarcodeReaderOptions
                {
                    Formats = BarcodeFormats.TwoDimensional,
                    AutoRotate = true,
                    Multiple = false,
                    TryHarder = true,
                },
            };

            var cancel = new Button { Text = "Cancel", Margin = new Thickness(16) };
            var page = new ContentPage
            {
                Title = "Scan Tailcat QR",
                BackgroundColor = Colors.Black,
                Content = new Grid
                {
                    RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
                    Children = { reader, cancel },
                },
            };
            Grid.SetRow(cancel, 1);

            var completed = 0;
            async Task CompleteAsync(string? value)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0) return;
                reader.IsDetecting = false;
                tcs.TrySetResult(value);
                try { await hostPage.Navigation.PopModalAsync(); } catch { }
            }

            reader.BarcodesDetected += (_, args) =>
            {
                var value = args.Results.Count > 0 ? args.Results[0].Value?.Trim() : null;
                if (!string.IsNullOrWhiteSpace(value)) MainThread.BeginInvokeOnMainThread(() => _ = CompleteAsync(value));
            };
            cancel.Clicked += (_, _) => _ = CompleteAsync(null);

            using var registration = cancellationToken.Register(() => MainThread.BeginInvokeOnMainThread(() => _ = CompleteAsync(null)));
            await hostPage.Navigation.PushModalAsync(page);
            return await tcs.Task;
        });
    }
}
