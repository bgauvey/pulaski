using System.Data;
using Dapper;

namespace Pulaski.Infrastructure.Persistence;

/// <summary>Process-wide Dapper settings. Safe to call more than once.</summary>
public static class DapperSetup
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        // Routines return snake_case columns; DTOs use PascalCase properties.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Npgsql reads timestamptz as a UTC DateTime, which Dapper cannot put into a
        // DateTimeOffset on its own. Dapper only consults a handler for a built-in type
        // once its default mapping is removed.
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
        SqlMapper.AddTypeHandler(new UtcDateTimeOffsetHandler());
    }

    private sealed class UtcDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset."),
        };

        // Npgsql writes timestamptz only from UTC values (offset 0).
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.DateTimeOffset;
            parameter.Value = value.ToUniversalTime();
        }
    }
}
