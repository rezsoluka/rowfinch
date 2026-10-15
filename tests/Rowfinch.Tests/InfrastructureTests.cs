using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void ConnectionValuesCannotDisableEncryptionThroughConnectionStringInjection()
    {
        var profile = new ConnectionProfile { Server = "server;Encrypt=False", Database = "database;TrustServerCertificate=True", UserName = "user" };
        var builder = new SqlConnectionStringBuilder(SqlQuerySession.BuildConnectionString(profile, "a;b=c").ConnectionString);
        Assert.Equal(profile.Server, builder.DataSource);
        Assert.Equal(profile.Database, builder.InitialCatalog);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.False(builder.Pooling);
        Assert.Equal(0, builder.ConnectRetryCount);
        Assert.Equal("a;b=c", builder.Password);
    }

    [Fact]
    public void EntraConnectionDoesNotIncludePassword()
    {
        var profile = new ConnectionProfile { Server = "example.database.windows.net", Database = "db", Authentication = AuthenticationKind.EntraInteractive };
        var builder = SqlQuerySession.BuildConnectionString(profile, "must-not-be-used");
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, builder.Authentication);
        Assert.False(builder.ShouldSerialize("Password"));
        Assert.Equal("", builder.Password);
    }

    [Fact]
    public async Task ProfileStoreRoundTripsWithoutSecretsAndUsesPrivateFilePermissions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "connections.json");
        try
        {
            var profile = new ConnectionProfile { Name = "Development", Server = "example.database.windows.net", Database = "app", UserName = "developer" };
            var store = new JsonProfileStore(path);
            await store.SaveAsync([profile], TestContext.Current.CancellationToken);
            Assert.Equal(profile, Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken)));
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("\"Password\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AccessToken", json, StringComparison.OrdinalIgnoreCase);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CsvPreservesQuotesMultilineValuesUnicodeAndInvariantDecimals()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var result = new QueryResultSet("test", [new("Name, label", "nvarchar"), new("Amount", "decimal"), new("Optional", "nvarchar")],
                [["München \"HQ\"\nsecond line", 12.5m, null]], false);
            await using var output = new MemoryStream();
            await CsvExporter.WriteAsync(result, output, TestContext.Current.CancellationToken);
            var expected = "\"Name, label\",Amount,Optional\n\"München \"\"HQ\"\"\nsecond line\",12.5,\n";
            Assert.Equal(expected, Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.True(output.CanWrite);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }
}
