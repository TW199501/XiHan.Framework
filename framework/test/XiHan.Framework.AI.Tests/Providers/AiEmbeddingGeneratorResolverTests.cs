// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.AI;
using XiHan.Framework.AI.Abstractions.Configuration;
using XiHan.Framework.AI.Abstractions.Providers;
using XiHan.Framework.AI.Providers;

namespace XiHan.Framework.AI.Tests.Providers;

public sealed class AiEmbeddingGeneratorResolverTests
{
    [Fact]
    public async Task Invalidate_DuringGeneration_DefersDisposeUntilCallCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new ControlledEmbeddingGenerator(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        using var resolver = CreateResolver(_ => generator);
        var leasedGenerator = resolver.Resolve();

        var generation = leasedGenerator.GenerateAsync(["text"]);
        await entered.Task;

        resolver.Invalidate();

        Assert.False(generator.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => leasedGenerator.GenerateAsync(["after-invalidation"]));

        release.SetResult();
        await generation;

        Assert.True(generator.IsDisposed);
    }

    [Fact]
    public void Invalidate_ReplacesCachedGeneratorForNextResolve()
    {
        var created = new List<ControlledEmbeddingGenerator>();
        using var resolver = CreateResolver(_ =>
        {
            var generator = new ControlledEmbeddingGenerator(() => Task.CompletedTask);
            created.Add(generator);
            return generator;
        });

        var first = resolver.Resolve();
        resolver.Invalidate();
        var second = resolver.Resolve();

        Assert.NotSame(first, second);
        Assert.True(created[0].IsDisposed);
        Assert.False(created[1].IsDisposed);
    }

    private static AiEmbeddingGeneratorResolver CreateResolver(
        Func<AiProviderOptions, IEmbeddingGenerator<string, Embedding<float>>> createGenerator)
        => new(new TestConfigStore(), createGenerator);

    private sealed class TestConfigStore : IAiProviderConfigStore
    {
        public Task<AiProviderOptions?> GetAsync(string? providerName = null, CancellationToken cancellationToken = default)
            => Task.FromResult<AiProviderOptions?>(new AiProviderOptions { Provider = "test", EmbeddingModel = "model" });

        public Task<IReadOnlyList<AiProviderOptions>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiProviderOptions>>([]);
    }

    private sealed class ControlledEmbeddingGenerator(Func<Task> generate) : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await generate();
            return new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1 })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() => IsDisposed = true;
    }
}
