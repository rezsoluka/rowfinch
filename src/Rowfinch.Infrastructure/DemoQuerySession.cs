using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

// An explicit UI preview, not a SQL emulator. It never opens a network connection.
public sealed class DemoQuerySession : IQuerySession, IObjectMetadataSession
{
    public bool IsConnected { get; private set; } = true;
    public Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); IsConnected = true; return Task.CompletedTask; }

    public async Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds,
        IProgress<string>? messages, CancellationToken cancellationToken)
    {
        await Task.Delay(600, cancellationToken);
        if (!IsConnected) throw new InvalidOperationException("The demo session is closed.");
        object?[][] rows =
        [
            [1, "Northwind", "Azure SQL", "East US", 99.95m],
            [2, "AdventureWorks", "Azure SQL", "West Europe", 99.99m],
            [3, "Wide World Importers", "SQL Server", null, 99.9m],
            [4, "Contoso", "Azure SQL", "Central US", 99.99m]
        ];
        messages?.Report("Demo results only. The editor text was not executed against a database.");
        var result = new QueryResultSet("Demo results",
            [new("Id", "int"), new("Name", "nvarchar"), new("Engine", "nvarchar"), new("Region", "nvarchar"), new("Availability", "decimal")],
            rows.Take(rowLimit).ToArray(), rowLimit < rows.Length);
        return new QueryExecution([result], 0, TimeSpan.FromMilliseconds(600));
    }

    public Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DatabaseObject>>([new("dbo", "Customers", "Table"), new("dbo", "Orders", "Table"), new("reporting", "MonthlySales", "View"), new("dbo", "GetCustomers", "Procedure"), new("dbo", "OrderTotal", "Function")]);
    }
    public Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(DatabaseObject item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DatabaseColumn> columns = item.Name switch
        {
            "Customers" => [new(1, "CustomerId", "int", false, 1, IsIdentity: true), new(2, "Name", "nvarchar(200)", false), new(3, "Email", "nvarchar(320)", true)],
            "Orders" => [new(1, "OrderId", "int", false, 1, IsIdentity: true), new(2, "CustomerId", "int", false), new(3, "Total", "decimal(18,2)", false), new(4, "PlacedAt", "datetime2(7)", false)],
            "MonthlySales" => [new(1, "Month", "date", true), new(2, "Total", "decimal(38,2)", true)],
            _ => throw new ArgumentException("No demo columns for this object.")
        };
        return Task.FromResult(columns);
    }

    public Task<string> GetDefinitionAsync(DatabaseObject item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(item.Name switch
        {
            "MonthlySales" => "CREATE VIEW [reporting].[MonthlySales] AS\nSELECT DATEFROMPARTS(YEAR(PlacedAt), MONTH(PlacedAt), 1) AS [Month], SUM(Total) AS [Total]\nFROM [dbo].[Orders]\nGROUP BY YEAR(PlacedAt), MONTH(PlacedAt);",
            "GetCustomers" => "CREATE PROCEDURE [dbo].[GetCustomers] AS\nSELECT [CustomerId], [Name], [Email] FROM [dbo].[Customers];",
            "OrderTotal" => "CREATE FUNCTION [dbo].[OrderTotal] (@OrderId int) RETURNS decimal(18,2) AS\nBEGIN\n    RETURN (SELECT [Total] FROM [dbo].[Orders] WHERE [OrderId] = @OrderId);\nEND;",
            _ => throw new ArgumentException("No demo definition for this object.")
        });
    }
    public Task<int> GetTransactionCountAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
}
