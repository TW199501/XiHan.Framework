// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using XiHan.Framework.Excel.Abstractions.Attributes;
using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Tests.Attributes;

/// <summary>
/// 导出列特性测试
/// </summary>
public class ExcelColumnAttributeTests
{
    /// <summary>
    /// 无参构造不填标题，标题留给构建器回退描述信息
    /// </summary>
    [Fact]
    public void 特性_无参构造标题为空()
    {
        var attribute = new ExcelColumnAttribute();

        Assert.Null(attribute.Header);
    }

    /// <summary>
    /// 带标题的构造把参数赋给标题
    /// </summary>
    [Fact]
    public void 特性_构造参数赋给标题()
    {
        var attribute = new ExcelColumnAttribute("提单号");

        Assert.Equal("提单号", attribute.Header);
    }

    /// <summary>
    /// 未显式赋值时特性取约定默认值，顺序与列宽都是「未指定」的标记值
    /// </summary>
    [Fact]
    public void 特性_默认值顺序未指定宽度未指定对齐自动不换行()
    {
        var attribute = new ExcelColumnAttribute();

        // -1 与 0 是「未指定」的标记值
        Assert.Equal(-1, attribute.Order);
        Assert.Equal(0, attribute.Width);
        Assert.Null(attribute.NumberFormat);
        Assert.Equal(ExcelAlignment.Auto, attribute.Alignment);
        Assert.False(attribute.Wrap);
    }

    /// <summary>
    /// 代码构造特性时全部成员可命名赋值
    /// </summary>
    [Fact]
    public void 特性_命名赋值全部成员()
    {
        var attribute = new ExcelColumnAttribute("重量")
        {
            Order = 7,
            Width = 12.5,
            NumberFormat = "#,##0.00",
            Alignment = ExcelAlignment.Right,
            Wrap = true
        };

        Assert.Equal("重量", attribute.Header);
        Assert.Equal(7, attribute.Order);
        Assert.Equal(12.5, attribute.Width);
        Assert.Equal("#,##0.00", attribute.NumberFormat);
        Assert.Equal(ExcelAlignment.Right, attribute.Alignment);
        Assert.True(attribute.Wrap);
    }

    /// <summary>
    /// 两个特性都只能标注属性
    /// </summary>
    [Fact]
    public void 特性_只允许标注属性()
    {
        var columnUsage = typeof(ExcelColumnAttribute).GetCustomAttributes<AttributeUsageAttribute>(true).Single();
        var ignoreUsage = typeof(ExcelIgnoreAttribute).GetCustomAttributes<AttributeUsageAttribute>(true).Single();

        Assert.Equal(AttributeTargets.Property, columnUsage.ValidOn);
        Assert.Equal(AttributeTargets.Property, ignoreUsage.ValidOn);
    }

    /// <summary>
    /// 忽略特性是纯标记，不带任何自有属性
    /// </summary>
    [Fact]
    public void 忽略特性_是纯标记()
    {
        Assert.Empty(typeof(ExcelIgnoreAttribute).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }
}
