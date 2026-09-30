// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Quotas.Providers;
using XiHan.Framework.Quotas;

namespace XiHan.Framework.Quotas.Tests;

/// <summary>
/// 配额政策契约测试
/// </summary>
public class QuotaPolicyTests
{
    /// <summary>
    /// 默认配额政策提供器一律返回无政策
    /// </summary>
    [Fact]
    public async Task 默认配额政策提供器返回无政策()
    {
        var provider = new DefaultQuotaPolicyProvider();

        var policy = await provider.FindPolicyAsync(1001, "export.rows");

        Assert.Null(policy);
    }

    /// <summary>
    /// 默认配额政策提供器对非法参数快速失败
    /// </summary>
    [Fact]
    public async Task 默认配额政策提供器对非法参数快速失败()
    {
        var provider = new DefaultQuotaPolicyProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.FindPolicyAsync(-1, "export.rows"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.FindPolicyAsync(1001, "   "));
    }

    /// <summary>
    /// 有限额政策的配额上限必须为正数
    /// </summary>
    [Fact]
    public void 有限额政策的配额上限必须为正数()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuotaPolicy.Limited(0, QuotaPeriod.Day, "v1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuotaPolicy.Limited(-1, QuotaPeriod.Day, "v1"));
    }

    /// <summary>
    /// 无限额是显式政策，上限为 null 且周期为不周期
    /// </summary>
    [Fact]
    public void 无限额政策上限为空且周期为不周期()
    {
        var policy = QuotaPolicy.Unlimited("v1");

        Assert.True(policy.IsUnlimited);
        Assert.Null(policy.Limit);
        Assert.Equal(QuotaPeriod.None, policy.Period);
    }

    /// <summary>
    /// 政策版本空白的政策快速失败
    /// </summary>
    [Fact]
    public void 政策版本空白快速失败()
    {
        Assert.Throws<ArgumentException>(() => QuotaPolicy.Limited(10, QuotaPeriod.Day, " "));
        Assert.Throws<ArgumentException>(() => QuotaPolicy.Unlimited(""));
    }

    /// <summary>
    /// 计量周期不是已定义的值时快速失败
    /// </summary>
    [Fact]
    public void 未定义的计量周期快速失败()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuotaPolicy.Limited(10, (QuotaPeriod)99, "v1"));
    }

    /// <summary>
    /// 按 UTC 自然日对齐计量周期起点
    /// </summary>
    [Fact]
    public void 日周期按UTC自然日对齐()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");
        var now = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), policy.ResolvePeriodStart(now));
    }

    /// <summary>
    /// 按 UTC 自然月对齐计量周期起点
    /// </summary>
    [Fact]
    public void 月周期按UTC自然月对齐()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Month, "v1");
        var now = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), policy.ResolvePeriodStart(now));
    }

    /// <summary>
    /// 按 UTC ISO 自然周对齐计量周期起点，起点恒为星期一
    /// </summary>
    [Theory]
    [InlineData(2026, 9, 28, 2026, 9, 28)]
    [InlineData(2026, 9, 30, 2026, 9, 28)]
    [InlineData(2026, 10, 4, 2026, 9, 28)]
    [InlineData(2026, 10, 5, 2026, 10, 5)]
    [InlineData(2026, 9, 27, 2026, 9, 21)]
    public void 周周期按UTCISO周一起点对齐(
        int year, int month, int day, int expectedYear, int expectedMonth, int expectedDay)
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Week, "v1");
        var now = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);

        var start = policy.ResolvePeriodStart(now);

        Assert.Equal(new DateTimeOffset(expectedYear, expectedMonth, expectedDay, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
    }

    /// <summary>
    /// 周期结束比起始正好多一个完整周期
    /// </summary>
    [Fact]
    public void 周期结束比起始多一个完整周期()
    {
        var day = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");
        var week = QuotaPolicy.Limited(10, QuotaPeriod.Week, "v1");
        var month = QuotaPolicy.Limited(10, QuotaPeriod.Month, "v1");
        var none = QuotaPolicy.Limited(10, QuotaPeriod.None, "v1");
        var now = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

        var dayStart = day.ResolvePeriodStart(now);
        var weekStart = week.ResolvePeriodStart(now);
        var monthStart = month.ResolvePeriodStart(now);

        Assert.Equal(TimeSpan.FromDays(1), day.ResolvePeriodEnd(dayStart) - dayStart);
        Assert.Equal(TimeSpan.FromDays(7), week.ResolvePeriodEnd(weekStart) - weekStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), month.ResolvePeriodEnd(monthStart));
        Assert.Equal(DateTimeOffset.MaxValue, none.ResolvePeriodEnd(none.ResolvePeriodStart(now)));
    }

    /// <summary>
    /// 不周期政策不切分周期，起点固定为最小值
    /// </summary>
    [Fact]
    public void 不周期政策不切分周期()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.None, "v1");

        Assert.Equal(DateTimeOffset.MinValue, policy.ResolvePeriodStart(new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>
    /// 带偏移的时刻先转 UTC 再对齐周期，UTC 日界不因本地偏移而误判
    /// </summary>
    [Fact]
    public void 带偏移的时刻先转UTC再对齐周期()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");
        var withOffset = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(8));

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), policy.ResolvePeriodStart(withOffset));
    }
}
