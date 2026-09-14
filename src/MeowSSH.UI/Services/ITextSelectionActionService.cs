namespace MeowSSH.UI.Services;

/// <summary>Shows the platform-native contextual actions for selected terminal text.</summary>
public interface ITextSelectionActionService
{
    Task ShowAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Browser/test-host implementation; xterm still keeps the visual selection.</summary>
public sealed class NoOpTextSelectionActionService : ITextSelectionActionService
{
    public Task ShowAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
