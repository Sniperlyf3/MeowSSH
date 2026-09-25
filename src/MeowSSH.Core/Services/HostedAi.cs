using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Services;

public enum AiAssistMode
{
    /// <summary>What does this output mean, and what should I do about it?</summary>
    Explain,

    /// <summary>Which command does what I describe? The answer carries it separately so it can be inserted.</summary>
    Command,
}

/// <summary>
/// <see cref="Input"/> is terminal text the user chose to send (may be empty
/// for a command request); <see cref="Question"/> is what the user typed,
/// required for <see cref="AiAssistMode.Command"/>.
/// </summary>
public sealed record AiAssistRequest(AiAssistMode Mode, string Input, string? Question);

/// <summary>
/// <see cref="Command"/>: for a command request, the one command the answer
/// proposes, kept apart from the prose so the UI can insert it without anything
/// around it. Never run automatically: it is typed into the prompt and waits
/// for Enter.
/// </summary>
public sealed record AiAssistAnswer(string Answer, string? Command);

/// <summary><see cref="Exception.Message"/> is written for the user.</summary>
public sealed class HostedAiException(string? code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string? Code { get; } = code;
}

/// <summary>MeowSSHAPI's POST /v1/ai/assist.</summary>
public interface IHostedAiApi
{
    Task<AiAssistAnswer> AssistAsync(SignedEntitlementGrant grant, AiAssistRequest request, CancellationToken cancellationToken = default);
}

public interface IHostedAiService
{
    /// <summary>Hosted AI is Pro Cloud: every answer costs MeowSSH inference.</summary>
    bool CanUse { get; }

    /// <exception cref="HostedAiException">The service refused or could not be reached.</exception>
    /// <exception cref="InvalidOperationException">The request is not allowed or not valid; the message says why.</exception>
    Task<AiAssistAnswer> AskAsync(AiAssistRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends exactly the text the user reviewed and nothing else: no host name,
/// address, username or key ever leaves the phone through here, and nothing
/// is sent until the user presses a button in the Ask AI panel.
/// </summary>
public sealed class HostedAiService(
    IHostedAiApi api,
    ICloudEntitlementGrantSource grants,
    IEntitlementService entitlements) : IHostedAiService
{
    /// <summary>Matches MeowSSHAPI's HostedAiOptions.MaxInputCharacters default.</summary>
    public const int MaxInputCharacters = 8000;

    /// <summary>Matches MeowSSHAPI's HostedAiOptions.MaxQuestionCharacters default.</summary>
    public const int MaxQuestionCharacters = 500;

    public bool CanUse => entitlements.Has(PremiumFeature.HostedAi);

    public async Task<AiAssistAnswer> AskAsync(AiAssistRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanUse)
            throw new InvalidOperationException("Ask AI requires MeowSSH Pro Cloud.");

        var input = request.Input?.Trim() ?? "";
        var question = request.Question?.Trim();
        if (request.Mode == AiAssistMode.Explain && input.Length == 0)
            throw new InvalidOperationException("Select some terminal output to explain first.");
        if (request.Mode == AiAssistMode.Command && string.IsNullOrEmpty(question))
            throw new InvalidOperationException("Describe what the command should do.");
        // Refused rather than cut: a clipped error message loses exactly the
        // line that mattered, and the user would not know it never arrived.
        if (input.Length > MaxInputCharacters)
            throw new InvalidOperationException($"That is {input.Length:N0} characters; Ask AI takes up to {MaxInputCharacters:N0}. Select less of the output.");
        if (question is { Length: > MaxQuestionCharacters })
            throw new InvalidOperationException($"Keep the question under {MaxQuestionCharacters} characters.");

        SignedEntitlementGrant grant;
        try
        {
            grant = await grants.GetPaidGrantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.SecurityException or HttpRequestException)
        {
            throw new HostedAiException("entitlement_unavailable", $"Your Pro Cloud purchase could not be verified: {exception.Message}", exception);
        }

        return await api.AssistAsync(grant, request with { Input = input, Question = question }, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class HttpHostedAiApi(HttpClient httpClient, LicensingApiOptions options) : IHostedAiApi
{
    public async Task<AiAssistAnswer> AssistAsync(SignedEntitlementGrant grant, AiAssistRequest request, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured || options.BaseUri is null)
            throw new HostedAiException("not_configured", "The MeowSSH AI service is not configured in this build.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "v1/ai/assist"))
        {
            Content = JsonContent.Create(
                new AssistBody(request.Mode == AiAssistMode.Command ? "command" : "explain", request.Input, request.Question),
                HostedAiJsonContext.Default.AssistBody),
        };
        message.Headers.Add("X-MeowSSH-Entitlement-Grant", grant.PayloadBase64 + "." + grant.SignatureBase64);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new HostedAiException("network", "Could not reach the MeowSSH AI service. Check your connection and try again.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout arrives as a cancellation; escaping a
            // click handler that way stops the Blazor renderer.
            throw new HostedAiException("timeout", "The AI took too long to answer. Try again.", exception);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync(HostedAiJsonContext.Default.AnswerBody, cancellationToken).ConfigureAwait(false);
                if (body?.Answer is not { Length: > 0 } answer)
                    throw new HostedAiException("empty_response", "The MeowSSH AI service returned an empty answer.");
                return new AiAssistAnswer(answer, string.IsNullOrWhiteSpace(body.Command) ? null : body.Command);
            }

            string? code = null;
            try
            {
                code = (await response.Content.ReadFromJsonAsync(HostedAiJsonContext.Default.ErrorBody, cancellationToken).ConfigureAwait(false))?.Error;
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            throw new HostedAiException(code, Explain(code, response.StatusCode));
        }
    }

    private static string Explain(string? code, HttpStatusCode status) => code switch
    {
        "pro_cloud_required" => "Ask AI requires MeowSSH Pro Cloud.",
        "expired_grant" or "invalid_grant" or "grant_from_future" or "wrong_package" =>
            "Your Pro Cloud purchase could not be verified right now. Try again in a moment.",
        "ai_quota_exceeded" => "You have used today's Ask AI allowance. It resets at midnight UTC.",
        "ai_refused" => "The AI could not help with that. Try rephrasing, or select different output.",
        "input_too_long" => "That is more text than Ask AI accepts. Select less of the output.",
        "invalid_request" => "Ask AI could not read that request. Update the app and try again.",
        "ai_unavailable" => "Ask AI is not available on the MeowSSH service yet.",
        _ when status == HttpStatusCode.TooManyRequests => "Ask AI is busy. Try again in a minute.",
        _ => $"The MeowSSH AI service returned an error ({(int)status}).",
    };

    internal sealed record AssistBody(string Mode, string Input, string? Question);
    internal sealed record AnswerBody(string? Answer, string? Command, string? Model);
    internal sealed record ErrorBody(string? Error, string? Message);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HttpHostedAiApi.AssistBody))]
[JsonSerializable(typeof(HttpHostedAiApi.AnswerBody))]
[JsonSerializable(typeof(HttpHostedAiApi.ErrorBody))]
internal sealed partial class HostedAiJsonContext : JsonSerializerContext
{
}
