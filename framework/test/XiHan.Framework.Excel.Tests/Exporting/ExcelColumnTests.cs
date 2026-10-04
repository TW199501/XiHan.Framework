// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// 列模型测试
/// </summary>
public class ExcelColumnTests
{
    /// <summary>
    /// 行类型为 null 或与 TRow 不符时取值为空，不抛异常
    /// </summary>
    [Fact]
    public void 列取值_行型别不符时返回空而不是抛()
    {
        var column = new ExcelColumn<SampleRow>
        {
            Key = "awb", Header = "提单号", Value = r => r.AwbNo
        };

        Assert.Equal("AWB1", column.GetValue(new SampleRow { AwbNo = "AWB1" }));
        Assert.Null(column.GetValue(null));
        Assert.Null(column.GetValue("不是行类型"));
    }

    /// <summary>
    /// 未显式赋值时列取约定默认值，其余可空项保持为空
    /// </summary>
    [Fact]
    public void 列默认值_对齐自动补位在右补空格()
    {
        var column = new ExcelColumn<SampleRow>
        {
            Key = "weight", Header = "重量", Value = r => r.Weight
        };

        Assert.Equal(ExcelAlignment.Auto, column.Alignment);
        Assert.Equal(ExcelTextPadding.Right, column.Padding);
        Assert.Equal(' ', column.PadChar);
        Assert.Equal(0, column.Order);
        Assert.False(column.Wrap);
        Assert.Null(column.Width);
        Assert.Null(column.FixedWidth);
        Assert.Null(column.NumberFormat);
        Assert.Null(column.TextFormat);
        Assert.Null(column.CellStyle);
    }

    /// <summary>
    /// 逐格样式委托按行返回样式，返回 null 表示该格不额外套样式
    /// </summary>
    [Fact]
    public void 列样式委托_命中时返回样式否则空()
    {
        var column = new ExcelColumn<SampleRow>
        {
            Key = "weight",
            Header = "重量",
            Value = r => r.Weight,
            CellStyle = row => row is SampleRow typed && typed.Weight > 30
                ? new ExcelTextStyle("#C00000", null, true)
                : null
        };

        Assert.Null(column.CellStyle!(new SampleRow { Weight = 1.5m }));

        var style = column.CellStyle!(new SampleRow { Weight = 40m });
        Assert.Equal("#C00000", style!.FontColor);
        Assert.Null(style.Fill);
        Assert.True(style.Bold);
    }

    /// <summary>
    /// 样式记录未赋值时取默认值，相等性按值比较
    /// </summary>
    [Fact]
    public void 列样式记录_预设全空且不粗体()
    {
        var style = new ExcelTextStyle();

        Assert.Null(style.FontColor);
        Assert.Null(style.Fill);
        Assert.False(style.Bold);
        Assert.Equal(new ExcelTextStyle("#000000", "#FFFFFF", true), new ExcelTextStyle("#000000", "#FFFFFF", true));
    }
}
