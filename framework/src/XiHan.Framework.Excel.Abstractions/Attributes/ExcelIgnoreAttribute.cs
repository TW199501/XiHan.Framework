// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Attributes;

/// <summary>
/// 标记属性不参与导出列构建
/// </summary>
/// <remarks>
/// 纯标记特性，不带任何成员；只能标在属性上，对类型本身、字段与方法都没有效果。
/// 排除以「键」为单位：同名属性（<c>new</c> 遮蔽出来的那一对）先去重再判本特性，标在 CLR 看得见
/// 的那一个上会让整个键不成列，被它盖住的基类属性不会顶上来；标在被盖住的那一个上不影响成列。
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcelIgnoreAttribute : Attribute
{
}
