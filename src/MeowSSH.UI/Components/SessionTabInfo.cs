namespace MeowSSH.UI.Components;

public enum SessionView
{
    Terminal,
    Files,
    Forwards
}

public sealed record SessionTabInfo(
    Guid Id,
    string Label,
    SessionView View,
    bool IsConnected,
    bool SupportsFiles = true,
    bool SupportsForwards = true);
