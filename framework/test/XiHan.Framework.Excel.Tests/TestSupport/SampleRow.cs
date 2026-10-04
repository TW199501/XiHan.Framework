// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 测试用行类型
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。后续任务的「属性列来源」与「特性列来源」都叠加在这一个类型上，
/// 避免每加一种列来源就多造一个行类型。
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
