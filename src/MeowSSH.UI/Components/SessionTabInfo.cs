namespace MeowSSH.UI.Components;

public enum SessionView
{
    Terminal,
    Files
}

public sealed record SessionTabInfo(Guid Id, string Label, SessionView View, bool IsConnected);
