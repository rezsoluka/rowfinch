using System.Text;
using System.Text.RegularExpressions;

namespace Rowfinch.Core;

public sealed record SqlBatch(string Text, int StartLine, int RepeatCount);

public static partial class SqlBatchParser
{
    [GeneratedRegex(@"^\s*GO(?:\s+([0-9]+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    public static IReadOnlyList<SqlBatch> Split(string sql)
    {
        var batches = new List<SqlBatch>();
        var buffer = new StringBuilder();
        var state = new LexicalState();
        using var reader = new StringReader(sql);
        var lineNumber = 0;
        var startLine = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (state.IsNeutral)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith(':') || trimmed.StartsWith("!!", StringComparison.Ordinal))
                    throw new ArgumentException($"SQLCMD directives are not supported in the query editor (line {lineNumber}).");
                var match = Separator().Match(line);
                if (match.Success)
                {
                    var repeat = 1;
                    if (match.Groups[1].Success &&
                        (!int.TryParse(match.Groups[1].Value, out repeat) || repeat is < 1 or > 100))
                        throw new ArgumentException($"GO repeat count must be between 1 and 100 (line {lineNumber}).");
                    AddBatch(repeat);
                    startLine = lineNumber + 1;
                    continue;
                }
            }
            buffer.AppendLine(line);
            state.Scan(line);
        }
        AddBatch(1);
        return batches;

        void AddBatch(int repeat)
        {
            var text = buffer.ToString();
            if (!string.IsNullOrWhiteSpace(text)) batches.Add(new SqlBatch(text, startLine, repeat));
            buffer.Clear();
        }
    }

    private sealed class LexicalState
    {
        private char quote;
        private int commentDepth;
        public bool IsNeutral => quote == '\0' && commentDepth == 0;

        public void Scan(string line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (commentDepth > 0)
                {
                    if (c == '/' && next == '*') { commentDepth++; i++; }
                    else if (c == '*' && next == '/') { commentDepth--; i++; }
                }
                else if (quote != '\0')
                {
                    if (c == quote)
                    {
                        if (next == quote) i++;
                        else quote = '\0';
                    }
                }
                else if (c == '-' && next == '-') break;
                else if (c == '/' && next == '*') { commentDepth++; i++; }
                else if (c is '\'' or '"') quote = c;
                else if (c == '[') quote = ']';
            }
        }
    }
}
