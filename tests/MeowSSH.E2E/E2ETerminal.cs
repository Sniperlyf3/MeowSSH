using System.Text;
using System.Text.RegularExpressions;
using MeowSSH.Core.Ssh;

namespace MeowSSH.E2E;

/// <summary>
/// A live terminal session read the way a person reads one: everything the
/// remote side printed, in order, with a way to run a command and get its
/// output and exit status back.
/// </summary>
public sealed partial class E2ETerminal : IAsyncDisposable
{
    private readonly StringBuilder _screen = new();
    private readonly object _gate = new();
    private int _commands;

    public E2ETerminal(ITerminalSession session)
    {
        Session = session;
        session.OutputReceived += OnOutput;
        session.Exited += (_, code) => ExitCode.TrySetResult(code);
    }

    public ITerminalSession Session { get; }
    public TaskCompletionSource<int> ExitCode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? Changed;

    public string Screen { get { lock (_gate) return _screen.ToString(); } }

    private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
    {
        lock (_gate) _screen.Append(Encoding.UTF8.GetString(data.Span));
        Changed?.Invoke();
    }

    public Task SendAsync(string text, CancellationToken cancellationToken = default) =>
        Session.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>Waits until the screen satisfies <paramref name="condition"/>; throws with the screen on timeout.</summary>
    public async Task<string> WaitForAsync(Func<string, bool> condition, TimeSpan timeout, string what)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (condition(Screen)) met.TrySetResult(); }
        Changed += Check;
        try
        {
            Check();
            if (await Task.WhenAny(met.Task, Task.Delay(timeout)) != met.Task)
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {what}. Screen:\n{Screen}");
            return Screen;
        }
        finally { Changed -= Check; }
    }

    public Task<string> WaitForTextAsync(string text, TimeSpan? timeout = null) =>
        WaitForAsync(s => s.Contains(text, StringComparison.Ordinal), timeout ?? TimeSpan.FromSeconds(30), $"'{text}'");

    /// <summary>
    /// Types <paramref name="command"/> at the shell and returns what it printed
    /// and its exit status.
    /// </summary>
    /// <remarks>
    /// The markers are split with "" so the pty's echo of the typed line never
    /// matches them; only the shell's evaluated output does.
    /// </remarks>
    public async Task<(string Output, int ExitCode)> RunAsync(string command, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _commands);
        var begin = $"__MEOW_B{id}__";
        var end = $"__MEOW_E{id}__";
        await SendAsync($"echo \"__MEOW_B{id}\"\"__\"; {command}; echo \"__MEOW_E{id}\"\"__$?__\"\n");

        var pattern = new Regex(Regex.Escape(begin) + @"\r?\n(?<out>.*?)" + Regex.Escape(end) + @"(?<code>\d+)__", RegexOptions.Singleline);
        var screen = await WaitForAsync(s => pattern.IsMatch(s), timeout ?? TimeSpan.FromSeconds(30), $"'{command}' to finish");
        var match = pattern.Matches(screen)[^1];
        var output = AnsiEscape().Replace(match.Groups["out"].Value, "").Replace("\r", "").TrimEnd('\n');
        return (output, int.Parse(match.Groups["code"].Value));
    }

    /// <summary>Runs a command that must succeed and returns its output.</summary>
    public async Task<string> CheckAsync(string command, TimeSpan? timeout = null)
    {
        var (output, code) = await RunAsync(command, timeout);
        if (code != 0) throw new InvalidOperationException($"'{command}' exited {code}:\n{output}");
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        Session.OutputReceived -= OnOutput;
        await Session.DisposeAsync();
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07")]
    private static partial Regex AnsiEscape();
}
