using System.Text;

namespace Mountloom.Core.Services;

/// <summary>Windows-style tokenization without shell evaluation. ArgumentList handles re-escaping.</summary>
public static class CommandLineArguments
{
    public static IReadOnlyList<string> Parse(string commandLine)
    {
        var result = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
            if (index == commandLine.Length) break;
            var token = new StringBuilder();
            var quoted = false;
            while (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index])))
            {
                var slashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\') { slashes++; index++; }
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    token.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) token.Append('"');
                    else if (quoted && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                    { token.Append('"'); index++; }
                    else quoted = !quoted;
                    index++;
                }
                else
                {
                    token.Append('\\', slashes);
                    if (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index])))
                        token.Append(commandLine[index++]);
                }
            }
            if (quoted) throw new ArgumentException("Advanced arguments contain an unclosed double quote.");
            result.Add(token.ToString());
        }
        return result;
    }
}
