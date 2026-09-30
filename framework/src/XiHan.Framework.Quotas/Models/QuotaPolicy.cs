// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas;

/// <summary>
/// 配额政策
/// </summary>
/// <remarks>
/// 实例只能经 <see cref="Limited"/> 或 <see cref="Unlimited"/> 创建：无限额是显式政策，不是「没配政策」。
/// 带周期的政策按 UTC 对齐边界，<see cref="QuotaPeriod.Day"/> 取当日 00:00、
/// <see cref="QuotaPeriod.Week"/> 取 ISO 周星期一 00:00、<see cref="QuotaPeriod.Month"/> 取当月 1 日 00:00。
/// </remarks>
public sealed class QuotaPolicy
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="limit">上限</param>
    /// <param name="period">计量周期</param>
    /// <param name="version">政策版本</param>
    private QuotaPolicy(long? limit, QuotaPeriod period, string version)
    {
        Limit = limit;
        Period = period;
        Version = version;
    }

    /// <summary>
    /// 单个计量周期内的用量上限，显式无限额时为 null
    /// </summary>
    public long? Limit { get; }

    /// <summary>
    /// 计量周期，无限额政策固定为 <see cref="QuotaPeriod.None"/>
    /// </summary>
    public QuotaPeriod Period { get; }

    /// <summary>
    /// 政策版本，参与预留冲突判定
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// 是否显式无限额
    /// </summary>
    public bool IsUnlimited => Limit is null;

    /// <summary>
    /// 创建有限额政策
    /// </summary>
    /// <param name="limit">用量上限，必须大于 0</param>
    /// <param name="period">计量周期，必须是已定义的枚举值</param>
    /// <param name="version">政策版本，不可为空白</param>
    /// <returns>有限额政策</returns>
    /// <exception cref="ArgumentOutOfRangeException">上限不大于 0，或周期不是已定义的枚举值</exception>
    /// <exception cref="ArgumentException">政策版本为空白</exception>
    public static QuotaPolicy Limited(long limit, QuotaPeriod period, string version)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (!Enum.IsDefined(period))
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "计量周期不是已定义的值。");
        }

        return new QuotaPolicy(limit, period, RequireVersion(version));
    }

    /// <summary>
    /// 创建显式无限额政策
    /// </summary>
    /// <param name="version">政策版本，不可为空白</param>
    /// <returns>无限额政策，计量周期为 <see cref="QuotaPeriod.None"/></returns>
    /// <exception cref="ArgumentException">政策版本为空白</exception>
    public static QuotaPolicy Unlimited(string version)
    {
        return new QuotaPolicy(null, QuotaPeriod.None, RequireVersion(version));
    }

    /// <summary>
    /// 计算某个时刻所属计量周期的起始时刻
    /// </summary>
    /// <param name="utcNow">用于定位周期的时刻，非 UTC 时先转为 UTC</param>
    /// <returns>周期起始的 UTC 时刻；<see cref="QuotaPeriod.None"/> 返回 <see cref="DateTimeOffset.MinValue"/>，表示累计型不切分周期</returns>
    /// <exception cref="ArgumentOutOfRangeException">计量周期不是已定义的枚举值</exception>
    public DateTimeOffset ResolvePeriodStart(DateTimeOffset utcNow)
    {
        var utc = utcNow.ToUniversalTime();
        var midnight = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);

        return Period switch
        {
            QuotaPeriod.None => DateTimeOffset.MinValue,
            QuotaPeriod.Day => new DateTimeOffset(midnight, TimeSpan.Zero),
            QuotaPeriod.Week => new DateTimeOffset(midnight.AddDays(-(6 + (int)utc.DayOfWeek) % 7), TimeSpan.Zero),
            QuotaPeriod.Month => new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(Period), Period, "计量周期不是已定义的值。")
        };
    }

    /// <summary>
    /// 计算某个计量周期的结束时刻
    /// </summary>
    /// <param name="periodStart">由 <see cref="ResolvePeriodStart"/> 得到的周期起始 UTC 时刻</param>
    /// <returns>周期结束的 UTC 时刻；<see cref="QuotaPeriod.None"/> 返回 <see cref="DateTimeOffset.MaxValue"/>，表示累计型没有周期边界</returns>
    /// <exception cref="ArgumentOutOfRangeException">计量周期不是已定义的枚举值</exception>
    public DateTimeOffset ResolvePeriodEnd(DateTimeOffset periodStart)
    {
        return Period switch
        {
            QuotaPeriod.None => DateTimeOffset.MaxValue,
            QuotaPeriod.Day => periodStart.AddDays(1),
            QuotaPeriod.Week => periodStart.AddDays(7),
            QuotaPeriod.Month => periodStart.AddMonths(1),
            _ => throw new ArgumentOutOfRangeException(nameof(Period), Period, "计量周期不是已定义的值。")
        };
    }

    /// <summary>
    /// 校验政策版本并返回原值
    /// </summary>
    /// <param name="version">政策版本</param>
    /// <returns>已校验的政策版本</returns>
    private static string RequireVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return version;
    }
}
