// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 测试收集到的单条日志
/// </summary>
/// <param name="Level">日志等级</param>
/// <param name="Category">日志分类名称</param>
/// <param name="Message">格式化后的日志消息</param>
public sealed record FakeLogEntry(LogLevel Level, string Category, string Message);

/// <summary>
/// 最小日志收集器，供测试断言是否留下了应有的日志，不引第三方日志测试包
/// </summary>
public sealed class FakeLogSink
{
    private readonly ConcurrentQueue<FakeLogEntry> _entries = new();

    /// <summary>
    /// 当前已收集的日志条目快照
    /// </summary>
    public IReadOnlyCollection<FakeLogEntry> Entries => [.. _entries];

    /// <summary>
    /// 记入一条日志
    /// </summary>
    /// <param name="entry">日志条目</param>
    internal void Add(FakeLogEntry entry)
    {
        _entries.Enqueue(entry);
    }
}

/// <summary>
/// 把日志写进 <see cref="FakeLogSink"/> 的日志提供器
/// </summary>
/// <param name="sink">日志收集器</param>
public sealed class SinkLoggerProvider(FakeLogSink sink) : ILoggerProvider
{
    /// <summary>
    /// 创建写入收集器的日志器
    /// </summary>
    /// <param name="categoryName">日志分类名称</param>
    /// <returns>日志器</returns>
    public ILogger CreateLogger(string categoryName)
    {
        return new SinkLogger(categoryName, sink);
    }

    /// <summary>
    /// 释放资源，本提供器不持有需释放的资源
    /// </summary>
    public void Dispose()
    {
        // 无资源需要清理
    }
}

/// <summary>
/// 只负责把日志条目交给收集器的日志器
/// </summary>
internal sealed class SinkLogger : ILogger
{
    private readonly string _category;

    private readonly FakeLogSink _sink;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="category">日志分类名称</param>
    /// <param name="sink">日志收集器</param>
    public SinkLogger(string category, FakeLogSink sink)
    {
        _category = category;
        _sink = sink;
    }

    /// <summary>
    /// 开始作用域，测试不验证作用域内容，返回空的 disposable
    /// </summary>
    /// <typeparam name="TState">作用域状态类型</typeparam>
    /// <param name="state">作用域状态</param>
    /// <returns>空作用域句柄</returns>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    /// <summary>
    /// 一律放行，好让测试看到全部日志
    /// </summary>
    /// <param name="logLevel">日志等级</param>
    /// <returns>恒为 <c>true</c></returns>
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <summary>
    /// 记入一条日志
    /// </summary>
    /// <param name="logLevel">日志等级</param>
    /// <param name="eventId">事件编号</param>
    /// <param name="state">日志状态</param>
    /// <param name="exception">附带异常</param>
    /// <param name="formatter">消息格式化委托</param>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _sink.Add(new FakeLogEntry(logLevel, _category, formatter(state, exception)));
    }
}

/// <summary>
/// 不做任何事的日志作用域句柄
/// </summary>
internal sealed class NullScope : IDisposable
{
    /// <summary>
    /// 共享实例
    /// </summary>
    public static readonly NullScope Instance = new();

    /// <summary>
    /// 释放资源，本句柄不持有需释放的资源
    /// </summary>
    public void Dispose()
    {
        // 无资源需要清理
    }
}
