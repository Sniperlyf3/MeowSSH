using Android.Gms.Tasks;
using Android.Runtime;
using MeowSSH.Core.Licensing;
using Xamarin.Google.Android.Play.Core.Integrity;

namespace MeowSSH.App;

public sealed class GooglePlayIntegrityService : Java.Lang.Object, IPlayIntegrityService
{
    private readonly IIntegrityManager _manager = IntegrityManagerFactory.Create(global::Android.App.Application.Context)
        ?? throw new InvalidOperationException("Google Play Integrity manager is unavailable.");

    public async Task<string?> RequestTokenAsync(string nonce, System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        if (nonce.Length is < 16 or > 500)
            throw new ArgumentOutOfRangeException(nameof(nonce), "Play Integrity nonces must be between 16 and 500 characters.");

        cancellationToken.ThrowIfCancellationRequested();

        var builder = IntegrityTokenRequest.InvokeBuilder()
            ?? throw new InvalidOperationException("Google Play Integrity request builder is unavailable.");
        builder.SetNonce(nonce);
        var request = builder.Build()
            ?? throw new InvalidOperationException("Google Play Integrity request could not be created.");

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        var listener = new IntegrityTaskListener(completion);

        var requestTask = _manager.RequestIntegrityToken(request)
            ?? throw new InvalidOperationException("Google Play Integrity request could not be started.");
        requestTask.AddOnSuccessListener(listener);
        requestTask.AddOnFailureListener(listener);

        return await completion.Task.ConfigureAwait(false);
    }

    private sealed class IntegrityTaskListener(TaskCompletionSource<string?> completion) : Java.Lang.Object, IOnSuccessListener, IOnFailureListener
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            try
            {
                var response = result?.JavaCast<IntegrityTokenResponse>();
                completion.TrySetResult(response?.Token());
            }
            catch (System.Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        public void OnFailure(Java.Lang.Exception exception) =>
            completion.TrySetException(new InvalidOperationException("Google Play Integrity token request failed.", exception));
    }
}
