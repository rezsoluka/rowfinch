using System.Globalization;

namespace Rowfinch.Core;

public enum AuthenticationKind { SqlPassword, EntraInteractive }

// Passwords and tokens deliberately have no representation in a saved profile.
public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New connection";
    public string Server { get; init; } = "";
    public string Database { get; init; } = "";
    public string UserName { get; init; } = "";
    public AuthenticationKind Authentication { get; init; }
    public bool RememberPassword { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server)) throw new ArgumentException("Enter a server name.");
        if (string.IsNullOrWhiteSpace(Database)) throw new ArgumentException("Enter a database name.");
        if (!Enum.IsDefined(Authentication)) throw new ArgumentException("Choose a supported authentication method.");
        if (Authentication == AuthenticationKind.SqlPassword && string.IsNullOrWhiteSpace(UserName))
            throw new ArgumentException("Enter a SQL user name.");
    }
}

public sealed record ResultColumn(string Name, string TypeName);
public sealed record QueryResultSet(string Name, IReadOnlyList<ResultColumn> Columns,
    IReadOnlyList<object?[]> Rows, bool IsTruncated);
public sealed record QueryExecution(IReadOnlyList<QueryResultSet> Results, int TransactionCount, TimeSpan Elapsed);
public sealed record DatabaseObject(string Schema, string Name, string Kind, int ObjectId = 0)
{
    public string DisplayName => $"{Schema}.{Name}";
    public string QuotedName => $"[{Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{Name.Replace("]", "]]", StringComparison.Ordinal)}]";
    public bool HasColumns => Kind is "Table" or "View";
    public bool HasDefinition => Kind is "View" or "Procedure" or "Function";
}

public static class CellFormatter
{
    public static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
