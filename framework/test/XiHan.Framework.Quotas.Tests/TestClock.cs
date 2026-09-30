// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Timing;

namespace XiHan.Framework.Quotas.Tests;

/// <summary>
/// 可拨动测试时钟
/// </summary>
internal sealed class TestClock : IClock
{
    private DateTime _now = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 当前时间
    /// </summary>
    public DateTime Now => _now;

    /// <summary>
    /// 时间类型，固定 UTC
    /// </summary>
    public DateTimeKind Kind => DateTimeKind.Utc;

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
        _now = _now.Add(duration);
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
