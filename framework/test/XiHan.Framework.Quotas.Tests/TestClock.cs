// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Timing;

namespace XiHan.Framework.Quotas.Tests;

/// <summary>
/// 可拨动且可标注时间类型的测试时钟
/// </summary>
internal sealed class TestClock : IClock
{
    private DateTime _utcNow = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 构造测试时钟
    /// </summary>
    /// <param name="kind">Now 返回时标注的时间类型</param>
    public TestClock(DateTimeKind kind = DateTimeKind.Utc)
    {
        Kind = kind;
    }

    /// <summary>
    /// 当前时间，按 <see cref="Kind"/> 标注同一瞬时
    /// </summary>
    public DateTime Now => Kind switch
    {
        DateTimeKind.Utc => _utcNow,
        DateTimeKind.Local => TimeZoneInfo.ConvertTimeFromUtc(_utcNow, TimeZoneInfo.Local),
        _ => DateTime.SpecifyKind(_utcNow, DateTimeKind.Unspecified)
    };

    /// <summary>
    /// 时间类型
    /// </summary>
    public DateTimeKind Kind { get; }

    /// <summary>
    /// 是否支持多时区，固定不支持
    /// </summary>
    public bool SupportsMultipleTimezone => false;

    /// <summary>
    /// 拨动时钟
    /// </summary>
    /// <param name="duration">前进时长</param>
    public void Advance(TimeSpan duration)
    {
        _utcNow = _utcNow.Add(duration);
    }

    /// <summary>
    /// 规范化时间，原样返回
    /// </summary>
    /// <param name="dateTime">时间</param>
    /// <returns>原时间</returns>
    public DateTime Normalize(DateTime dateTime)
    {
        return dateTime;
    }

    /// <summary>
    /// 转换为用户时间，原样返回
    /// </summary>
    /// <param name="utcDateTime">UTC 时间</param>
    /// <returns>原时间</returns>
    public DateTime ConvertToUserTime(DateTime utcDateTime)
    {
        return utcDateTime;
    }

    /// <summary>
    /// 转换为用户时间，原样返回
    /// </summary>
    /// <param name="dateTimeOffset">时间偏移</param>
    /// <returns>原时间偏移</returns>
    public DateTimeOffset ConvertToUserTime(DateTimeOffset dateTimeOffset)
    {
        return dateTimeOffset;
    }

    /// <summary>
    /// 转换为 UTC 时间，原样返回
    /// </summary>
    /// <param name="dateTime">时间</param>
    /// <returns>原时间</returns>
    public DateTime ConvertToUtc(DateTime dateTime)
    {
        return dateTime;
    }
}
