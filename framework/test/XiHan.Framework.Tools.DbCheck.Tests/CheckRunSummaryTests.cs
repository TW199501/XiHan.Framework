// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using DbCheck.Checks;

namespace XiHan.Framework.Tools.DbCheck.Tests;

/// <summary>
/// 检查运行汇总测试
/// </summary>
public class CheckRunSummaryTests
{
    /// <summary>
    /// 创建指定状态的检查结果
    /// </summary>
    private static CheckResult CreateResult(CheckStatus status)
    {
        return new CheckResult("db-main", "backup", status, "证据描述");
    }

    /// <summary>
    /// 全部通过时退出码为 0
    /// </summary>
    [Fact]
    public void FromResults_AllPass_ReturnsExitCodeZero()
    {
        var summary = CheckRunSummary.FromResults(
        [
            CreateResult(CheckStatus.Pass),
            CreateResult(CheckStatus.Pass),
        ]);

        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(0, summary.FailCount);
        Assert.Equal(0, summary.WarnCount);
    }

    /// <summary>
    /// 含警告且无失败时退出码为 1
    /// </summary>
    [Fact]
    public void FromResults_WarnWithoutFail_ReturnsExitCodeOne()
    {
        var summary = CheckRunSummary.FromResults(
        [
            CreateResult(CheckStatus.Pass),
            CreateResult(CheckStatus.Warn),
        ]);

        Assert.Equal(1, summary.ExitCode);
        Assert.Equal(0, summary.FailCount);
        Assert.Equal(1, summary.WarnCount);
    }

    /// <summary>
    /// 含失败时退出码为 2
    /// </summary>
    [Fact]
    public void FromResults_ContainsFail_ReturnsExitCodeTwo()
    {
        var summary = CheckRunSummary.FromResults(
        [
            CreateResult(CheckStatus.Pass),
            CreateResult(CheckStatus.Warn),
            CreateResult(CheckStatus.Fail),
        ]);

        Assert.Equal(2, summary.ExitCode);
        Assert.Equal(1, summary.FailCount);
        Assert.Equal(1, summary.WarnCount);
    }

    /// <summary>
    /// 仅不适用时退出码为 0 且不计入失败与警告
    /// </summary>
    [Fact]
    public void FromResults_OnlyNotApplicable_ReturnsZeroWithoutCounts()
    {
        var summary = CheckRunSummary.FromResults(
        [
            CreateResult(CheckStatus.NotApplicable),
        ]);

        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(0, summary.FailCount);
        Assert.Equal(0, summary.WarnCount);
    }

    /// <summary>
    /// 仅未实现时退出码为 0 且不计入失败与警告
    /// </summary>
    [Fact]
    public void FromResults_OnlyNotImplemented_ReturnsZeroWithoutCounts()
    {
        var summary = CheckRunSummary.FromResults(
        [
            CreateResult(CheckStatus.NotImplemented),
        ]);

        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(0, summary.FailCount);
        Assert.Equal(0, summary.WarnCount);
    }
}
