using System.Text;
using System.Text.RegularExpressions;

namespace MeowSSH.Core.Services;

/// <summary>
/// Expands runtime Action variables using POSIX shell single-quote escaping. Variable values are
/// supplied only for one run and are never persisted in the Action definition.
/// </summary>
public static partial class CommandActionTemplate
{
    private const int MaxVariables = 20;
    private const int MaxValueLength = 8_192;

    [GeneratedRegex(@"\{\{([A-Za-z_][A-Za-z0-9_]{0,63})\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();

    public static IReadOnlyList<string> GetVariables(string command)
    {
        if (string.IsNullOrEmpty(command)) return [];
        return [.. VariableRegex().Matches(command)
            .Select(static match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxVariables + 1)];
    }

    public static string Render(string command, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(values);

        var variables = GetVariables(command);
        if (variables.Count > MaxVariables)
            throw new ArgumentException($"Actions may contain at most {MaxVariables} runtime variables.", nameof(command));
        if (variables.Count == 0) return command;

        foreach (var variable in variables)
        {
            if (!values.TryGetValue(variable, out var value))
                throw new ArgumentException($"Enter a value for Action variable '{variable}'.", nameof(values));
            if (value.Length > MaxValueLength)
                throw new ArgumentException($"Action variable '{variable}' must be {MaxValueLength} characters or fewer.", nameof(values));
        }

        return VariableRegex().Replace(command, match => ShellQuote(values[match.Groups[1].Value]));
    }

    internal static string ShellQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // POSIX-safe literal: close the single-quoted string, emit a literal quote, reopen it.
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
