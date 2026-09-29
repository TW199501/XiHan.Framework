// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Traffic.GrayRouting.Enums;
using XiHan.Framework.Traffic.GrayRouting.Models;
using XiHan.Framework.Traffic.SqlSugar.Entities;

namespace XiHan.Framework.Traffic.SqlSugar.Mapping;

/// <summary>
/// 灰度规则实体与模型的映射
/// </summary>
public static class GrayRuleMapper
{
    /// <summary>
    /// 把实体转换为模型
    /// </summary>
    /// <param name="entity">灰度规则实体</param>
    /// <returns>灰度规则模型</returns>
    public static GrayRule ToModel(SysGrayRule entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new GrayRule
        {
            RuleId = entity.BasicId,
            RuleName = entity.RuleName,
            RuleType = (GrayRuleType)entity.RuleType,
            IsEnabled = entity.IsEnabled,
            Priority = entity.Priority,
            TargetVersion = entity.TargetVersion,
            TargetServiceId = entity.TargetServiceId,
            Configuration = entity.Configuration,
            EffectiveTime = entity.EffectiveTime?.UtcDateTime,
            ExpiryTime = entity.ExpiryTime?.UtcDateTime,
            CreatedTime = entity.CreatedTime.UtcDateTime,
            UpdatedTime = entity.UpdatedTime?.UtcDateTime,
            Remark = entity.Remark
        };
    }

    /// <summary>
    /// 把模型转换为实体
    /// </summary>
    /// <param name="model">灰度规则模型</param>
    /// <returns>灰度规则实体</returns>
    public static SysGrayRule ToEntity(GrayRule model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new SysGrayRule(model.RuleId)
        {
            RuleName = model.RuleName,
            RuleType = (int)model.RuleType,
            IsEnabled = model.IsEnabled,
            Priority = model.Priority,
            TargetVersion = model.TargetVersion,
            TargetServiceId = model.TargetServiceId,
            Configuration = model.Configuration,
            EffectiveTime = ToUtcOffset(model.EffectiveTime),
            ExpiryTime = ToUtcOffset(model.ExpiryTime),
            CreatedTime = ToUtcOffset(model.CreatedTime),
            UpdatedTime = ToUtcOffset(model.UpdatedTime),
            Remark = model.Remark
        };
    }

    /// <summary>
    /// 把未标注时区的时间按 UTC 解释后转换为 DateTimeOffset
    /// </summary>
    /// <remarks>
    /// 把 <paramref name="value"/> 的 <see cref="DateTime.Kind"/> 统一视为 <see cref="DateTimeKind.Utc"/>
    /// 后再转换为 <see cref="DateTimeOffset"/>，忽略原有的 <see cref="DateTime.Kind"/> 标注。
    /// </remarks>
    /// <param name="value">原始时间</param>
    /// <returns>按 UTC 解释后的时间</returns>
    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>
    /// 把可空的未标注时区时间按 UTC 解释后转换为 DateTimeOffset
    /// </summary>
    /// <param name="value">原始时间</param>
    /// <returns>按 UTC 解释后的时间，输入为空时返回空</returns>
    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        return value.HasValue ? ToUtcOffset(value.Value) : null;
    }
}
