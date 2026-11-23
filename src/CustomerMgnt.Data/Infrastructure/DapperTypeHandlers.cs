using System.Data;
using Dapper;

namespace CustomerMgnt.Data.Infrastructure;

/// <summary>
/// ลงทะเบียน type handler ของ Dapper:
/// DateOnly/TimeOnly (Npgsql อ่าน date/time เป็น DateTime/TimeSpan) และ DateTimeOffset (timestamptz ต้องเขียนเป็น UTC และอ่านกลับเป็น DateTime UTC)
/// </summary>
public static class DapperTypeHandlers
{
    private static int _registered;

    /// <summary>ลงทะเบียนครั้งเดียว (thread-safe, เรียกซ้ำได้)</summary>
    public static void Register()
    {
        // Step 1: กันลงทะเบียนซ้ำ
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        // Step 2: handler
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    /// <summary>DateTimeOffset ↔ timestamptz (เก็บเป็น UTC เสมอ)</summary>
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        /// <inheritdoc />
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            // Npgsql รับ DateTimeOffset ที่ offset = 0 เท่านั้นสำหรับ timestamptz
            parameter.Value = value.ToUniversalTime();
        }

        /// <inheritdoc />
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset d => d,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)).ToUniversalTime(),
            _ => DateTimeOffset.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>DateOnly ↔ date</summary>
    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        /// <inheritdoc />
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }

        /// <inheritdoc />
        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>TimeOnly ↔ time</summary>
    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        /// <inheritdoc />
        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.DbType = DbType.Time;
            parameter.Value = value;
        }

        /// <inheritdoc />
        public override TimeOnly Parse(object value) => value switch
        {
            TimeOnly t => t,
            TimeSpan ts => TimeOnly.FromTimeSpan(ts),
            DateTime dt => TimeOnly.FromDateTime(dt),
            _ => TimeOnly.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
