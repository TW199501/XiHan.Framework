// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 测试用行类型
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。
/// </remarks>
public class SampleRow
{
    /// <summary>
    /// 提单号
    /// </summary>
    public string AwbNo { get; set; } = string.Empty;

    /// <summary>
    /// 重量
    /// </summary>
    public decimal Weight { get; set; }

    /// <summary>
    /// 预计到达时间
    /// </summary>
    public DateTime Eta { get; set; }
}
