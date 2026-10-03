// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace DbCheck.Checks;

/// <summary>
/// 检查结果状态
/// </summary>
public enum CheckStatus
{
    /// <summary>
    /// 通过
    /// </summary>
    Pass,

    /// <summary>
    /// 警告
    /// </summary>
    Warn,

    /// <summary>
    /// 失败
    /// </summary>
    Fail,

    /// <summary>
    /// 不适用
    /// </summary>
    NotApplicable,

    /// <summary>
    /// 未实现
    /// </summary>
    NotImplemented
}
