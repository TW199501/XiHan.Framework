// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace DbCheck.Checks;

/// <summary>
/// 一次检查运行的汇总
/// </summary>
/// <param name="ExitCode">进程退出码：任一失败为 2，否则任一警告为 1，否则为 0</param>
/// <param name="FailCount">失败项数量</param>
/// <param name="WarnCount">警告项数量</param>
public sealed record CheckRunSummary(int ExitCode, int FailCount, int WarnCount)
{
    /// <summary>
    /// 从检查结果汇总退出码与失败、警告计数，不适用与未实现的结果不计入
    /// </summary>
    public static CheckRunSummary FromResults(IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var failCount = 0;
        var warnCount = 0;
        foreach (var result in results)
        {
            if (result.Status == CheckStatus.Fail)
            {
                failCount++;
            }
            else if (result.Status == CheckStatus.Warn)
            {
                warnCount++;
            }
        }

        var exitCode = failCount > 0 ? 2 : warnCount > 0 ? 1 : 0;
        return new CheckRunSummary(exitCode, failCount, warnCount);
    }
}
