// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Attributes;

/// <summary>
/// 标记属性不参与导出列构建
/// </summary>
/// <remarks>
/// 纯标记特性，不带任何成员；只能标在属性上，对类型本身、字段与方法都没有效果。
/// 排除以「键」为单位：判本特性只看代表这个键的那一个属性，它标了忽略就整个键不成列。
/// 同名属性（<c>new</c> 遮蔽出来的那一对）先去重再判，代表键的是 CLR 看得见的那一个，被它盖住的
/// 基类属性不会顶上来，标在被盖住那一个上的忽略不影响成列；遮蔽者没有公共读取器或是索引器时不进
/// 候选，此时代表键的换成基类属性（它自身也得构成候选，两个都不构成候选就没有这个键），标在它
/// 上面的忽略使整个键不成列。
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcelIgnoreAttribute : Attribute
{
}
