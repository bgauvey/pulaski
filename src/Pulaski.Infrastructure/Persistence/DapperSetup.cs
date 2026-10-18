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

        // Dates (a shift schedule's cycle start, D-038): Npgsql reads and writes date as DateOnly; Dapper needs telling.
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly."),
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }
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
