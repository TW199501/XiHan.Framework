// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Features;

/// <summary>
/// 功能授权拒绝原因
/// </summary>
public enum FeatureDenyReason
{
    /// <summary>
    /// 该功能标识没有任何授权来源认识它
    /// </summary>
    UnknownFeature = 0,

    /// <summary>
    /// 功能存在，但当前租户未被授权
    /// </summary>
    NotEntitled = 1
}
