using System.Data.Common;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

public static class ResultMetadata
{
    public static IReadOnlyList<ResultColumn> ReadColumns(DbDataReader reader) => reader.GetColumnSchema()
        .Select((column, index) => new ResultColumn(reader.GetName(index), reader.GetDataTypeName(index),
            column.DataType, column.AllowDBNull, column.IsLong == true ? -1 : column.ColumnSize, column.NumericPrecision,
            column.NumericScale, column.IsKey, column.IsIdentity)).ToArray();
}
