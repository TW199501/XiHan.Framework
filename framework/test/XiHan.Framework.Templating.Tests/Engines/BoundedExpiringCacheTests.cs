// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Templating.Engines;

namespace XiHan.Framework.Templating.Tests.Engines;

/// <summary>
/// 有界过期缓存测试
/// </summary>
public class BoundedExpiringCacheTests
{
    /// <summary>
    /// 过期条目在下次访问时清除并释放容量
    /// </summary>
    [Fact]
    public void TryGetValue_AfterExpiration_RemovesEntry()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new BoundedExpiringCache<string, string>(2, TimeSpan.FromMinutes(1), timeProvider);
        cache.Set("first", "value");
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        Assert.False(cache.TryGetValue("first", out _));
        cache.Set("second", "value");
        Assert.True(cache.TryGetValue("second", out var value));
        Assert.Equal("value", value);
    }

    /// <summary>
    /// 无过期缓存仍会遵守容量并淘汰最久未使用项
    /// </summary>
    [Fact]
    public void InfiniteExpiration_StillEvictsToStayWithinCapacity()
    {
        var cache = new BoundedExpiringCache<string, string>(1, Timeout.InfiniteTimeSpan);
        cache.Set("first", "one");
        cache.Set("second", "two");

        Assert.False(cache.TryGetValue("first", out _));
        Assert.True(cache.TryGetValue("second", out var value));
        Assert.Equal("two", value);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
        }
    }
}
