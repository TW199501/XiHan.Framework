// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;

namespace XiHan.Framework.Templating.Engines;

internal sealed class BoundedExpiringCache<TKey, TValue> where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _recentlyUsed = [];
    private readonly int _capacity;
    private readonly TimeSpan _expiration;
    private readonly TimeProvider _timeProvider;

    public BoundedExpiringCache(int capacity, TimeSpan expiration, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (expiration < TimeSpan.Zero && expiration != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(expiration));
        }

        _capacity = capacity;
        _expiration = expiration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_sync)
        {
            RemoveExpiredEntries(_timeProvider.GetUtcNow());

            if (!_entries.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }

            _recentlyUsed.Remove(node);
            _recentlyUsed.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            RemoveExpiredEntries(now);

            if (_expiration == TimeSpan.Zero)
            {
                return;
            }

            DateTimeOffset? expiresAt = null;
            if (_expiration != Timeout.InfiniteTimeSpan)
            {
                var remaining = DateTimeOffset.MaxValue - now;
                expiresAt = _expiration >= remaining ? DateTimeOffset.MaxValue : now + _expiration;
            }

            if (_entries.TryGetValue(key, out var existingNode))
            {
                existingNode.Value = new CacheEntry(key, value, expiresAt);
                _recentlyUsed.Remove(existingNode);
                _recentlyUsed.AddFirst(existingNode);
                return;
            }

            var node = _recentlyUsed.AddFirst(new CacheEntry(key, value, expiresAt));
            _entries.Add(key, node);

            if (_entries.Count > _capacity)
            {
                var leastRecentlyUsed = _recentlyUsed.Last!;
                _recentlyUsed.RemoveLast();
                _entries.Remove(leastRecentlyUsed.Value.Key);
            }
        }
    }

    public bool Remove(TKey key)
    {
        lock (_sync)
        {
            if (!_entries.Remove(key, out var node))
            {
                return false;
            }

            _recentlyUsed.Remove(node);
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _recentlyUsed.Clear();
        }
    }

    private void RemoveExpiredEntries(DateTimeOffset now)
    {
        var node = _recentlyUsed.Last;
        while (node is not null)
        {
            var previous = node.Previous;
            if (node.Value.ExpiresAt is { } expiresAt && expiresAt <= now)
            {
                _recentlyUsed.Remove(node);
                _entries.Remove(node.Value.Key);
            }

            node = previous;
        }
    }

    private sealed record CacheEntry(TKey Key, TValue Value, DateTimeOffset? ExpiresAt);
}
