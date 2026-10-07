// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using XiHan.Framework.AI.Abstractions.Configuration;
using XiHan.Framework.AI.Abstractions.Providers;

namespace XiHan.Framework.AI.Providers;

/// <summary>
/// 多 provider 嵌入生成器解析器（按名从配置源构建并缓存）
/// </summary>
/// <remarks>
/// 按 provider 名缓存已构建的嵌入生成器。失效时不打断已开始的生成请求；请求结束后释放旧实例，
/// 旧解析引用不再接受新请求。下次解析会按最新配置构建新实例。
/// </remarks>
public sealed class AiEmbeddingGeneratorResolver : IAiEmbeddingGeneratorResolver, IDisposable
{
    private const string DefaultKey = " default";

    private readonly IAiProviderConfigStore _configStore;
    private readonly Func<AiProviderOptions, IEmbeddingGenerator<string, Embedding<float>>> _createGenerator;
    private readonly ConcurrentDictionary<string, GeneratorEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>
    /// 构造函数
    /// </summary>
    public AiEmbeddingGeneratorResolver(IAiProviderConfigStore configStore, OpenAiEmbeddingGeneratorFactory factory)
    {
        _configStore = configStore;
        ArgumentNullException.ThrowIfNull(factory);
        _createGenerator = factory.Create;
    }

    internal AiEmbeddingGeneratorResolver(
        IAiProviderConfigStore configStore,
        Func<AiProviderOptions, IEmbeddingGenerator<string, Embedding<float>>> createGenerator)
    {
        _configStore = configStore;
        _createGenerator = createGenerator;
    }

    /// <summary>
    /// 解析指定 provider 的嵌入生成器，为空取默认 provider，构建后按名缓存复用
    /// </summary>
    /// <param name="providerName">provider 配置名，为空取默认 provider</param>
    /// <returns>该 provider 的嵌入生成器</returns>
    public IEmbeddingGenerator<string, Embedding<float>> Resolve(string? providerName = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cacheKey = string.IsNullOrWhiteSpace(providerName) ? DefaultKey : providerName;
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached.Generator;
            }

            var options = _configStore.GetAsync(providerName).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"未找到 AI Provider 配置:{providerName ?? "(默认)"}。请检查 XiHan:AI 配置或 provider 名。");
            var generator = _createGenerator(options)
                ?? throw new InvalidOperationException("嵌入生成器工厂返回了 null。");
            var entry = new GeneratorEntry(generator);
            _cache[cacheKey] = entry;
            return entry.Generator;
        }
    }

    /// <summary>
    /// 使已缓存的嵌入生成器失效，下次解析按最新配置重建
    /// </summary>
    /// <param name="providerName">provider 配置名，为空则清空全部缓存，否则清该 provider 及默认槽</param>
    /// <remarks>已开始的生成请求会完成后再释放旧实例；失效后的旧解析引用不能再启动新请求。</remarks>
    public void Invalidate(string? providerName = null)
    {
        List<GeneratorEntry> removed;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(providerName))
            {
                removed = _cache.Values.Distinct().ToList();
                _cache.Clear();
            }
            else
            {
                removed = [];
                Remove(providerName, removed);
                Remove(DefaultKey, removed);
            }
        }

        foreach (var entry in removed)
        {
            entry.Retire();
        }
    }

    /// <summary>
    /// 释放缓存的可释放生成器
    /// </summary>
    public void Dispose()
    {
        List<GeneratorEntry> removed;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            removed = _cache.Values.Distinct().ToList();
            _cache.Clear();
        }

        foreach (var entry in removed)
        {
            entry.Retire();
        }
    }

    private void Remove(string key, List<GeneratorEntry> removed)
    {
        if (_cache.Remove(key, out var entry) && !removed.Contains(entry))
        {
            removed.Add(entry);
        }
    }

    private sealed class GeneratorEntry
    {
        private readonly object _sync = new();
        private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
        private int _activeCalls;
        private bool _retired;
        private bool _released;

        public GeneratorEntry(IEmbeddingGenerator<string, Embedding<float>> inner)
        {
            _inner = inner;
            Generator = new LeasedEmbeddingGenerator(this, inner);
        }

        public IEmbeddingGenerator<string, Embedding<float>> Generator { get; }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options,
            CancellationToken cancellationToken)
        {
            using var lease = Acquire();
            return await _inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }

        public void Retire()
        {
            var release = false;
            lock (_sync)
            {
                _retired = true;
                if (_activeCalls == 0 && !_released)
                {
                    _released = true;
                    release = true;
                }
            }

            if (release)
            {
                ReleaseInner();
            }
        }

        private IDisposable Acquire()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_retired, this);
                _activeCalls++;
            }

            return new CallLease(this);
        }

        private void Exit()
        {
            var release = false;
            lock (_sync)
            {
                _activeCalls--;
                if (_retired && _activeCalls == 0 && !_released)
                {
                    _released = true;
                    release = true;
                }
            }

            if (release)
            {
                ReleaseInner();
            }
        }

        private void ReleaseInner()
        {
            if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private sealed class CallLease(GeneratorEntry entry) : IDisposable
        {
            private GeneratorEntry? _entry = entry;

            public void Dispose()
            {
                Interlocked.Exchange(ref _entry, null)?.Exit();
            }
        }

        private sealed class LeasedEmbeddingGenerator(
            GeneratorEntry entry,
            IEmbeddingGenerator<string, Embedding<float>> inner)
            : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
        {
            public override Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
                IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
                => entry.GenerateAsync(values, options, cancellationToken);

            protected override void Dispose(bool disposing)
            {
                // 生命周期由 resolver 管理；调用方持有的是共享缓存视图。
            }
        }
    }
}
