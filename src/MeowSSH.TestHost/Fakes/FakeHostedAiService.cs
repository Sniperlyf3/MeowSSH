using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using Microsoft.AspNetCore.Components;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Stands in for HostedAiService, which needs MeowSSHAPI and a model. The
/// gating, validation and wire rules are tested in Core; this echoes back what
/// it was sent, so a page test can prove exactly which text would have left
/// the phone.
/// </summary>
/// <remarks>
/// "aifails" in the query string makes every ask fail as a server refusal
/// does; "aimultiline" makes the suggested command span two lines.
/// </remarks>
public sealed class FakeHostedAiService(IEntitlementService entitlements, NavigationManager navigation) : IHostedAiService
{
    private readonly bool _fails = new Uri(navigation.Uri).Query.Contains("aifails", StringComparison.OrdinalIgnoreCase);
    private readonly bool _multiline = new Uri(navigation.Uri).Query.Contains("aimultiline", StringComparison.OrdinalIgnoreCase);

    public bool CanUse => entitlements.Has(PremiumFeature.HostedAi);

    public AiAssistRequest? LastRequest { get; private set; }

    public async Task<AiAssistAnswer> AskAsync(AiAssistRequest request, CancellationToken cancellationToken = default)
    {
        if (!CanUse) throw new InvalidOperationException("Ask AI requires MeowSSH Pro Cloud.");
        // A beat of latency, so the page's busy state is actually rendered.
        await Task.Delay(50, cancellationToken);
        if (_fails) throw new HostedAiException("ai_quota_exceeded", "You have used today's Ask AI allowance. It resets at midnight UTC.");
        LastRequest = request;
        if (request.Mode == AiAssistMode.Command && _multiline)
            return new AiAssistAnswer("Clear the cache:\n```sh\ncd /srv/app\nrm -rf cache\n```", "cd /srv/app\nrm -rf cache");
        return request.Mode == AiAssistMode.Command
            ? new AiAssistAnswer("This shows how full each disk is:\n```sh\ndf -h\n```", "df -h")
            : new AiAssistAnswer($"You sent {request.Input.Length} characters starting \"{request.Input[..Math.Min(24, request.Input.Length)]}\".", null);
    }
}
