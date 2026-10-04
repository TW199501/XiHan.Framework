// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Attributes;

/// <summary>
/// 标记属性不参与导出列构建
/// </summary>
/// <remarks>
/// 纯标记特性，不带任何成员；只对属性生效，不影响类型本身或其他成员的导出。
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcelIgnoreAttribute : Attribute
{
}
