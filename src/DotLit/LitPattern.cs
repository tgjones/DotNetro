using System.Text;
using System.Text.RegularExpressions;

namespace DotLit;

/// <summary>
/// Translates a FileCheck-style CHECK pattern into a .NET regex. Following LLVM FileCheck, the
/// pattern text is matched <em>literally</em> by default; a regular expression can be embedded by
/// surrounding it with double braces, e.g. <c>addr {{[0-9]+}} done</c>. So most CHECK lines are the
/// verbatim expected output and need no escaping, and only the genuinely-variable parts are regex.
/// (Variable capture/use — FileCheck's <c>[[...]]</c> — is not supported.)
/// </summary>
internal static class LitPattern
{
    public static string ToRegex(string pattern)
    {
        var builder = new StringBuilder();
        var index = 0;

        while (index < pattern.Length)
        {
            var regexStart = pattern.IndexOf("{{", index, StringComparison.Ordinal);
            if (regexStart < 0)
            {
                builder.Append(Regex.Escape(pattern[index..]));
                break;
            }

            builder.Append(Regex.Escape(pattern[index..regexStart]));

            var regexEnd = pattern.IndexOf("}}", regexStart + 2, StringComparison.Ordinal);
            if (regexEnd < 0)
            {
                // Unterminated "{{" — treat the remainder as literal text.
                builder.Append(Regex.Escape(pattern[regexStart..]));
                break;
            }

            builder.Append("(?:").Append(pattern[(regexStart + 2)..regexEnd]).Append(')');
            index = regexEnd + 2;
        }

        return builder.ToString();
    }
}
