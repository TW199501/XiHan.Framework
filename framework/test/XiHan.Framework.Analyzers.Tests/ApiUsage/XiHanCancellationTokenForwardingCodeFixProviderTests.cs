// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.CodeAnalysis.CodeFixes;
using XiHan.Framework.Analyzers.ApiUsage;
using XiHan.Framework.Analyzers.Tests.Infrastructure;

namespace XiHan.Framework.Analyzers.Tests.ApiUsage;

/// <summary>
/// XHFA002 代码修复测试
/// </summary>
public class XiHanCancellationTokenForwardingCodeFixProviderTests
{
    private static readonly string WorkerPath = AnalyzerTestHost.FilePath("src", "Demo", "Worker.cs");

    /// <summary>
    /// 没有实参时补上命名实参
    /// </summary>
    [Fact]
    public async Task 修复以命名实参转发取消令牌()
    {
        var code = Worker("    public async Task RunAsync(CancellationToken cancellationToken) { await InnerAsync(); }");

        var run = await RunAsync(code);

        Assert.Single(run.Actions);
        Assert.Contains("await InnerAsync(cancellationToken: cancellationToken);", run.FixedText);
    }

    /// <summary>
    /// 已有实参时在末尾追加命名实参
    /// </summary>
    [Fact]
    public async Task 修复在已有实参后追加命名实参()
    {
        var code = Worker("    public async Task RunAsync(CancellationToken ct) { await InnerWithValueAsync(1); }");

        var run = await RunAsync(code);

        Assert.Contains("await InnerWithValueAsync(1, token: ct);", run.FixedText);
    }

    /// <summary>
    /// 修复器声明可处理 XHFA002 并支持批量修复
    /// </summary>
    [Fact]
    public void 修复器声明可修复的诊断并支持批量修复()
    {
        var provider = new XiHanCancellationTokenForwardingCodeFixProvider();

        Assert.Equal(["XHFA002"], provider.FixableDiagnosticIds);
        Assert.Same(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    private static Task<CodeFixRun> RunAsync(string code)
    {
        return AnalyzerTestHost.RunCodeFixAsync(
            new XiHanCancellationTokenForwardingAnalyzer(),
            new XiHanCancellationTokenForwardingCodeFixProvider(),
            code,
            WorkerPath,
            TestContext.Current.CancellationToken);
    }

    private static string Worker(string member)
    {
        return AnalyzerTestHost.Source(
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "namespace Demo;",
            "public class Worker",
            "{",
            member,
            "    private static Task InnerAsync(CancellationToken cancellationToken = default) { return Task.CompletedTask; }",
            "    private static Task InnerWithValueAsync(int value = 0, CancellationToken token = default) { return Task.CompletedTask; }",
            "}");
    }
}
