namespace Rowfinch.Core;

public sealed record SqlCompletionItem(string Text, string InsertionText, string Description, bool IsCommandContinuation = false)
{
    public bool IsSql => Description.StartsWith("T-SQL ", StringComparison.Ordinal);
}

public sealed record SqlCompletionContext(int StartOffset, int EndOffset, string Prefix,
    IReadOnlyList<SqlCompletionItem> Items, IReadOnlyList<DatabaseObject> ColumnSources, char Quote,
    bool SuggestAfterSpace = false)
{
    public SqlCompletionItem Column(DatabaseObject source, DatabaseColumn column) =>
        new(SqlCompletion.Filter(column.Name, Quote), ObjectSql.Quote(column.Name), $"{source.DisplayName} · {column.Description}");
}

/// <summary>Tolerant completion for unfinished SQL. Never executes or rewrites a statement.</summary>
public static class SqlCompletion
{
    // Expression/function/type suggestions must not change SQL alias recognition.
    private static readonly HashSet<string> AliasTerminators = new(
        "SELECT FROM WHERE JOIN INNER LEFT RIGHT FULL OUTER CROSS APPLY ON AS AND OR NOT NULL IS IN LIKE BETWEEN TOP DISTINCT ORDER GROUP BY HAVING ASC DESC INSERT INTO VALUES UPDATE SET DELETE EXEC EXECUTE DECLARE BEGIN END TRANSACTION COMMIT ROLLBACK GO WITH UNION ALL CASE WHEN THEN ELSE CREATE ALTER DROP TRUNCATE MERGE OUTPUT OFFSET FETCH OPTION FOR RETURN PIVOT UNPIVOT EXCEPT INTERSECT USING".Split(' '),
        StringComparer.OrdinalIgnoreCase);
    private sealed record Token(string Value, int Start, int End, int Scope, char Kind, int ChildScope = -1);
    private sealed record Source(string Alias, DatabaseObject? Object, int Scope, int Position);

    public static SqlCompletionContext? Analyze(string sql, int offset, IReadOnlyList<DatabaseObject> objects)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > sql.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var (tokens, parents) = Lex(sql);
        if (tokens.Any(t => t.Kind is 's' or 'c' && offset > t.Start && offset <= t.End)) return null;
        var word = tokens.FirstOrDefault(t => t.Kind is 'w' or '[' or '"' && offset >= t.Start && offset <= t.End);
        var start = word?.Start ?? offset;
        var end = word?.End ?? offset;
        var quote = word?.Kind is '[' or '"' ? word.Kind : '\0';
        var prefix = sql[start..offset];
        if (word?.Value.StartsWith('@') == true || word?.Value.StartsWith('#') == true) return null;
        var before = tokens.Where(t => t.End <= start && t.Kind != 'c').ToList();
        var statementStart = 0;
        var statementEnd = sql.Length;
        foreach (var token in tokens.Where(t => t.Kind == ';' || IsGo(t, sql)))
        {
            if (token.End <= start) statementStart = token.End;
            else if (token.Start >= offset) { statementEnd = token.Start; break; }
        }
        before.RemoveAll(t => t.Start < statementStart);
        var statement = tokens.Where(t => t.Start >= statementStart && t.Start < statementEnd && t.Kind != 'c').ToList();
        var previous = tokens.LastOrDefault(t => t.End <= start);
        var scope = word?.Scope ?? (previous is { Kind: '(' } ? previous.ChildScope : previous?.Scope) ?? 0;
        var visibleScopes = new List<int>();
        for (var current = scope; current >= 0; current = parents[current]) visibleScopes.Add(current);

        var qualifiers = new List<string>();
        var cursor = before.Count - 1;
        while (cursor >= 1 && before[cursor].Kind == '.' && IsIdentifier(before[cursor - 1]))
        {
            qualifiers.Insert(0, before[cursor - 1].Value); cursor -= 2;
        }
        var lastKeyword = before.Take(cursor + 1).LastOrDefault()?.Value.ToUpperInvariant();
        var objectPosition = lastKeyword is "FROM" or "JOIN" or "UPDATE" or "INTO" or "EXEC" or "EXECUTE" or "APPLY";
        var procedures = lastKeyword is "EXEC" or "EXECUTE";
        var precedingWords = before.AsEnumerable().Reverse().TakeWhile(t => t.Kind == 'w')
            .Reverse().Select(t => t.Value).ToArray();
        var sources = Sources(statement, objects).Where(source =>
        {
            var headers = statement.Where(t => t.Scope == source.Scope && t.Kind == 'w' &&
                t.Value.ToUpperInvariant() is "SELECT" or "UPDATE" or "INSERT" or "DELETE" or "EXEC" or "EXECUTE").ToArray();
            var from = headers.LastOrDefault(t => t.Start <= offset)?.Start ?? statementStart;
            var to = headers.FirstOrDefault(t => t.Start > offset)?.Start ?? statementEnd;
            return source.Position >= from && source.Position < to;
        }).ToList();
        var items = new List<SqlCompletionItem>();
        var columns = new List<DatabaseObject>();
        if (qualifiers.Count > 0)
        {
            if (qualifiers.Count > (objectPosition ? 1 : 2)) return new(start, end, prefix, [], [], quote);
            var alias = !objectPosition && qualifiers.Count == 1
                ? sources.Where(s => s.Alias.Equals(qualifiers[0], StringComparison.OrdinalIgnoreCase) && visibleScopes.Contains(s.Scope))
                    .OrderBy(s => visibleScopes.IndexOf(s.Scope)).ThenBy(s => s.Object is null ? 1 : 0).FirstOrDefault() : null;
            if (alias is not null)
            {
                if (alias.Object is { HasColumns: true } target) columns.Add(target);
            }
            else if (!objectPosition && qualifiers.Count == 2)
                columns.AddRange(objects.Where(o => o.HasColumns && Equal(o.Schema, qualifiers[0]) && Equal(o.Name, qualifiers[1])));
            else if (qualifiers.Count == 1)
                items.AddRange(objects.Where(o => Equal(o.Schema, qualifiers[0]) && Eligible(o, objectPosition, procedures))
                    .Select(o => new SqlCompletionItem(Filter(o.Name, quote), ObjectSql.Quote(o.Name), $"{o.Kind} · {o.DisplayName}")));
        }
        else
        {
            items.AddRange(objects.Where(o => Eligible(o, objectPosition, procedures))
                .Select(o => new SqlCompletionItem(Filter(o.Name, quote), o.QuotedName, $"{o.Kind} · {o.DisplayName}")));
            items.AddRange(objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(s => new SqlCompletionItem(Filter(s, quote), ObjectSql.Quote(s), "Schema")));
            if (!objectPosition)
            {
                if (quote == '\0') items.AddRange(SqlCommands.Items(precedingWords));
                // Prefer the nearest query scope. Do not import columns from unrelated nested queries.
                var local = sources.Where(s => s.Scope == visibleScopes.FirstOrDefault(v => sources.Any(s => s.Scope == v)));
                columns.AddRange(local.Where(s => s.Object?.HasColumns == true).Select(s => s.Object!).Distinct());
            }
        }
        return new(start, end, prefix, items, columns, quote,
            qualifiers.Count == 0 && quote == '\0' && (objectPosition || SqlCommands.HasContinuation(precedingWords)));
    }

    public static string Filter(string name, char quote) => quote switch
    {
        '[' => ObjectSql.Quote(name),
        '"' => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        _ => name
    };

    private static bool Equal(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool Eligible(DatabaseObject item, bool objectPosition, bool procedures) =>
        !objectPosition || (procedures ? item.Kind == "Procedure" : item.HasColumns || item.Kind == "Function");
    private static bool IsIdentifier(Token token) => token.Kind is 'w' or '[' or '"';
    private static bool CanAlias(Token token) => IsIdentifier(token) &&
        (token.Kind != 'w' || !AliasTerminators.Contains(token.Value));
    private static bool IsGo(Token token, string sql)
    {
        if (token.Kind != 'w' || !Equal(token.Value, "GO")) return false;
        var lineStart = sql.LastIndexOf('\n', Math.Max(0, token.Start - 1));
        var lineEnd = sql.IndexOf('\n', token.End);
        var line = sql[(lineStart + 1)..(lineEnd < 0 ? sql.Length : lineEnd)].Trim();
        return line.Equals("GO", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("GO ", StringComparison.OrdinalIgnoreCase) &&
            (int.TryParse(line[3..].Trim(), out _) || line[3..].TrimStart().StartsWith("--", StringComparison.Ordinal));
    }

    private static List<Source> Sources(List<Token> tokens, IReadOnlyList<DatabaseObject> objects)
    {
        var result = new List<Source>();
        // CTEs and derived tables shadow real object names; their projected columns need a full binder.
        var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Kind != 'w' || !Equal(tokens[i].Value, "WITH")) continue;
            var at = i + 1;
            while (at < tokens.Count && IsIdentifier(tokens[at]))
            {
                var name = tokens[at++].Value;
                if (at < tokens.Count && tokens[at].Kind == '(') at = AfterParenthesis(tokens, at);
                if (at + 1 >= tokens.Count || !Equal(tokens[at].Value, "AS") || tokens[at + 1].Kind != '(') break;
                ctes.Add(name);
                at = AfterParenthesis(tokens, at + 1);
                if (at >= tokens.Count || tokens[at].Kind != ',') break;
                at++;
            }
        }
        var fromScopes = new HashSet<int>();
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var t = tokens[i];
            var keyword = t.Kind == 'w' ? t.Value.ToUpperInvariant() : "";
            if (keyword is "WHERE" or "GROUP" or "ORDER" or "HAVING" or "SET" or "VALUES" or "UNION") fromScopes.Remove(t.Scope);
            if (keyword == "FROM") fromScopes.Add(t.Scope);
            if (!(keyword is "FROM" or "JOIN" or "UPDATE" or "INTO" or "APPLY" || t.Kind == ',' && fromScopes.Contains(t.Scope))) continue;
            var at = i + 1;
            var names = new List<string>();
            if (tokens[at].Kind == '(')
            {
                at++;
                while (at < tokens.Count && !(tokens[at].Kind == ')' && tokens[at].Scope == t.Scope)) at++;
                at++;
            }
            else
            {
                if (!IsIdentifier(tokens[at])) continue;
                names.Add(tokens[at++].Value);
                while (at + 1 < tokens.Count && tokens[at].Kind == '.' && IsIdentifier(tokens[at + 1]))
                { names.Add(tokens[at + 1].Value); at += 2; }
                // Function/table-valued-function output is not a table's catalog column list.
                if (at < tokens.Count && tokens[at].Kind == '(') continue;
            }
            if (at < tokens.Count && Equal(tokens[at].Value, "AS")) at++;
            var alias = at < tokens.Count && CanAlias(tokens[at]) ? tokens[at].Value : names.LastOrDefault();
            if (alias is null) continue;
            DatabaseObject? target = null;
            if (names.Count is 1 or 2 && !(names.Count == 1 && ctes.Contains(names[0])))
            {
                var matches = objects.Where(o => o.HasColumns && Equal(o.Name, names[^1]) && (names.Count == 1 || Equal(o.Schema, names[0]))).ToArray();
                if (matches.Length == 1) target = matches[0];
            }
            result.Add(new(alias, target, t.Scope, t.Start));
        }
        return result;
    }

    private static int AfterParenthesis(List<Token> tokens, int index)
    {
        var scope = tokens[index++].Scope;
        while (index < tokens.Count && !(tokens[index].Kind == ')' && tokens[index].Scope == scope)) index++;
        return Math.Min(index + 1, tokens.Count);
    }

    private static (List<Token> Tokens, List<int> Parents) Lex(string sql)
    {
        var tokens = new List<Token>();
        var parents = new List<int> { -1 };
        var scope = 0;
        for (var i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            var start = i;
            var kind = sql[i];
            string value;
            if (kind == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2; while (i < sql.Length && sql[i] != '\n') i++;
                kind = 'c'; value = "";
            }
            else if (kind == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2; var depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                kind = 'c'; value = "";
            }
            else if (kind is '\'' or '[' or '"')
            {
                var close = kind == '[' ? ']' : kind;
                var name = new System.Text.StringBuilder(); i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        i++;
                        if (i < sql.Length && sql[i] == close) { name.Append(close); i++; }
                        else break;
                    }
                    else name.Append(sql[i++]);
                }
                value = name.ToString(); if (kind == '\'') kind = 's';
            }
            else if (char.IsLetterOrDigit(kind) || kind is '_' or '@' or '#' or '$')
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$')) i++;
                value = sql[start..i]; kind = 'w';
            }
            else { value = sql[i++].ToString(); }
            if (kind == ')' && parents[scope] >= 0) scope = parents[scope];
            var child = -1;
            if (kind == '(') { parents.Add(scope); child = parents.Count - 1; }
            tokens.Add(new(value, start, i, scope, kind, child));
            if (child >= 0) scope = child;
        }
        return (tokens, parents);
    }
}
