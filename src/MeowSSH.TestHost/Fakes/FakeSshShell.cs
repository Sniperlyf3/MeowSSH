using System.Text;
using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// A shell with no SSH connection behind it: it echoes typed input and answers a
/// handful of commands, so the terminal can be driven in a browser on CI.
/// </summary>
/// <remarks>
/// It behaves like a pty rather than like a chat box — echoing each keystroke,
/// handling backspace, and emitting CR before LF. Those are exactly the details a
/// terminal component gets wrong, so a fake that skipped them would let real bugs
/// through.
/// </remarks>
public sealed class FakeSshShell : ISshShell
{
    private readonly StringBuilder _line = new();
    private const string Prompt = "\u001b[38;2;63;191;143mdeploy@prod-web-01\u001b[0m:\u001b[38;2;89;169;255m~\u001b[0m$ ";

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;

    /// <summary>The size the terminal last reported, so a test can assert on it.</summary>
    public (int Columns, int Rows) Size { get; private set; }

    public int ResizeCount { get; private set; }

    /// <summary>Everything the terminal has sent, for tests that assert on input.</summary>
    public List<byte> Received { get; } = [];

    public Task StartAsync()
    {
        Emit($"Linux prod-web-01 6.8.0 x86_64\r\nLast login: {DateTime.UtcNow:ddd MMM d HH:mm:ss yyyy} from 10.4.2.1\r\n\r\n{Prompt}");
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Received.AddRange(data.ToArray());

        foreach (var b in data.Span)
        {
            switch (b)
            {
                case 0x0d:               // Enter
                    Emit("\r\n");
                    RunCommand(_line.ToString().Trim());
                    _line.Clear();
                    Emit(Prompt);
                    break;

                case 0x7f or 0x08:       // Backspace
                    if (_line.Length > 0)
                    {
                        _line.Length--;
                        // Move left, overwrite with a space, move left again --
                        // a terminal does not erase by moving the cursor alone.
                        Emit("\b \b");
                    }
                    break;

                case 0x03:               // Ctrl+C
                    Emit("^C\r\n" + Prompt);
                    _line.Clear();
                    break;

                case 0x04:               // Ctrl+D
                    Emit("logout\r\n");
                    Exited?.Invoke(this, 0);
                    break;

                case 0x09:               // Tab
                    break;

                default:
                    if (b >= 0x20)
                    {
                        var c = (char)b;
                        _line.Append(c);
                        Emit(c.ToString());   // a pty echoes what you type
                    }
                    break;
            }
        }
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        Size = (columns, rows);
        ResizeCount++;
        return Task.CompletedTask;
    }

    private void RunCommand(string command)
    {
        switch (command)
        {
            case "":
                break;
            case "whoami":
                Emit("deploy\r\n");
                break;
            case "pwd":
                Emit("/home/deploy\r\n");
                break;
            case "ls":
                Emit("\u001b[38;2;89;169;255mdeploy\u001b[0m  releases  \u001b[38;2;63;191;143mstart.sh\u001b[0m\r\n");
                break;
            case "tput cols":
                Emit($"{Size.Columns}\r\n");
                break;
            case "exit":
                Emit("logout\r\n");
                Exited?.Invoke(this, 0);
                break;
            default:
                Emit($"\u001b[38;2;242;85;90m{command}: command not found\u001b[0m\r\n");
                break;
        }
    }

    private void Emit(string text) =>
        OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
