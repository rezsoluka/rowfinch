namespace Rowfinch.Core;

/// <summary>Local T-SQL vocabulary and command phrases for editor completion.</summary>
internal static class SqlCommands
{
    private const string KeywordText = """
        SELECT FROM WHERE JOIN INNER LEFT RIGHT FULL OUTER CROSS APPLY ON AS AND OR NOT NULL IS IN LIKE
        BETWEEN TOP DISTINCT ORDER GROUP BY HAVING ASC DESC INSERT INTO VALUES UPDATE SET DELETE EXEC
        EXECUTE DECLARE BEGIN END TRANSACTION COMMIT ROLLBACK GO WITH UNION ALL CASE WHEN THEN ELSE CAST
        CONVERT TRY_CAST TRY_CONVERT COUNT COUNT_BIG SUM AVG MIN MAX CREATE ALTER DROP TRUNCATE MERGE
        TABLE VIEW PROCEDURE FUNCTION INDEX SCHEMA DATABASE TYPE TRIGGER OR REPLACE IF EXISTS RETURN
        RETURNS OUTPUT INSERTED DELETED DEFAULT CONSTRAINT PRIMARY KEY FOREIGN REFERENCES UNIQUE CHECK
        IDENTITY ADD COLUMN NULLIF COALESCE ISNULL OVER PARTITION ROWS RANGE UNBOUNDED PRECEDING FOLLOWING
        CURRENT ROW OFFSET FETCH NEXT ONLY EXCEPT INTERSECT PIVOT UNPIVOT USING MATCHED SOURCE TARGET
        TRY CATCH THROW RAISERROR PRINT WAITFOR DELAY WHILE BREAK CONTINUE SAVE TRAN NOCOUNT XACT_ABORT
        ANSI_NULLS QUOTED_IDENTIFIER ON OFF USE GRANT DENY REVOKE TO ASC DESC
        GETDATE GETUTCDATE SYSDATETIME SYSUTCDATETIME SYSDATETIMEOFFSET DATEADD DATEDIFF DATEDIFF_BIG
        DATEPART DATENAME DATETRUNC DATEFROMPARTS EOMONTH YEAR MONTH DAY LEN DATALENGTH SUBSTRING
        CONCAT CONCAT_WS STRING_AGG STRING_SPLIT TRIM LTRIM RTRIM UPPER LOWER REPLACE CHARINDEX
        ROW_NUMBER RANK DENSE_RANK LAG LEAD FIRST_VALUE LAST_VALUE ROUND ABS CEILING FLOOR
        INT BIGINT SMALLINT TINYINT BIT DECIMAL NUMERIC FLOAT REAL MONEY SMALLMONEY
        VARCHAR NVARCHAR CHAR NCHAR TEXT NTEXT DATE DATETIME DATETIME2 DATETIMEOFFSET SMALLDATETIME
        TIME UNIQUEIDENTIFIER BINARY VARBINARY XML ROWVERSION
        """;

    internal static readonly HashSet<string> Keywords = new(
        KeywordText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static readonly string[][] Phrases = """
        SELECT DISTINCT
        SELECT TOP
        INSERT INTO
        DELETE FROM
        MERGE INTO
        ORDER BY
        GROUP BY
        PARTITION BY
        INNER JOIN
        LEFT JOIN
        LEFT OUTER JOIN
        RIGHT JOIN
        RIGHT OUTER JOIN
        FULL OUTER JOIN
        CROSS JOIN
        CROSS APPLY
        OUTER APPLY
        UNION ALL
        CREATE TABLE
        CREATE VIEW
        CREATE PROCEDURE
        CREATE FUNCTION
        CREATE INDEX
        CREATE UNIQUE INDEX
        CREATE SCHEMA
        CREATE DATABASE
        CREATE TYPE
        CREATE TRIGGER
        CREATE OR ALTER VIEW
        CREATE OR ALTER PROCEDURE
        CREATE OR ALTER FUNCTION
        CREATE OR ALTER TRIGGER
        ALTER TABLE
        ALTER VIEW
        ALTER PROCEDURE
        ALTER FUNCTION
        ALTER INDEX
        ALTER DATABASE
        ALTER TRIGGER
        DROP TABLE
        DROP VIEW
        DROP PROCEDURE
        DROP FUNCTION
        DROP INDEX
        DROP SCHEMA
        DROP DATABASE
        DROP TYPE
        DROP TRIGGER
        DROP TABLE IF EXISTS
        TRUNCATE TABLE
        BEGIN TRANSACTION
        COMMIT TRANSACTION
        ROLLBACK TRANSACTION
        SAVE TRANSACTION
        BEGIN TRY
        END TRY
        BEGIN CATCH
        END CATCH
        SET NOCOUNT ON
        SET XACT_ABORT ON
        SET ANSI_NULLS ON
        SET QUOTED_IDENTIFIER ON
        IS NULL
        IS NOT NULL
        NOT EXISTS
        IF EXISTS
        IF NOT EXISTS
        PRIMARY KEY
        FOREIGN KEY
        NOT NULL
        DEFAULT VALUES
        FETCH NEXT
        ROWS ONLY
        WITH CHECK
        """.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Split(' ')).ToArray();

    internal static IEnumerable<SqlCompletionItem> Items(IReadOnlyList<string> precedingWords)
    {
        foreach (var keyword in Keywords) yield return new(keyword, keyword, "T-SQL keyword");
        foreach (var phrase in Phrases)
        {
            var text = string.Join(' ', phrase);
            yield return new(text, text, "T-SQL command");
            for (var length = phrase.Length - 1; length > 0; length--)
            {
                if (precedingWords.Count < length || !precedingWords.TakeLast(length)
                    .SequenceEqual(phrase.Take(length), StringComparer.OrdinalIgnoreCase)) continue;
                var suffix = string.Join(' ', phrase.Skip(length));
                yield return new(suffix, suffix, "T-SQL command", IsCommandContinuation: true);
                break;
            }
        }
    }

    internal static bool HasContinuation(IReadOnlyList<string> precedingWords) =>
        Items(precedingWords).Any(i => i.IsCommandContinuation);
}
