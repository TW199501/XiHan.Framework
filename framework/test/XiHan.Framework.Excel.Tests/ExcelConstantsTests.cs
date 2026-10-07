// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Exporting;

namespace XiHan.Framework.Excel.Tests;

/// <summary>
/// Excel 常量测试：钉住常量数值，以及两条 xlsx 写出路径读的是同一个常量
/// </summary>
/// <remarks>
/// 接线断言读两个导出器的 <c>internal</c> 上限字段，不走反射。
/// </remarks>
public class ExcelConstantsTests
{
    /// <summary>
    /// 工作表行数上限是 xlsx 的 1,048,576 行
    /// </summary>
    [Fact]
    public void 工作表行数上限是xlsx的一百零四万八千五百七十六行()
        => Assert.Equal(1_048_576, ExcelConstants.MaxSheetRows);

    /// <summary>
    /// 单元格字符数上限是 xlsx 的 32,767 个字符
    /// </summary>
    [Fact]
    public void 单元格字符数上限是xlsx的三万两千七百六十七个字符()
        => Assert.Equal(32_767, ExcelConstants.MaxCellTextLength);

    /// <summary>
    /// 两条 xlsx 写出路径的公开入口读的是同一个 <see cref="ExcelConstants.MaxSheetRows"/>
    /// </summary>
    /// <remarks>
    /// 比对两个导出器经公开构造函数建出后生效的上限与该常量。
    /// </remarks>
    [Fact]
    public void 两条xlsx写出路径读的是同一个行数上限常量()
    {
        Assert.Equal(ExcelConstants.MaxSheetRows, new ClosedXmlExporter(new XiHanExcelOptions()).MaxSheetRows);
        Assert.Equal(ExcelConstants.MaxSheetRows, new MiniExcelStreamExporter().MaxSheetRows);
    }

    /// <summary>
    /// 行数上限与定宽导入的单行字节上限数值相同，但两个常量各自独立存在
    /// </summary>
    /// <remarks>
    /// 两者都是 1,048,576：一个数的是工作表的行，一个数的是定宽档单行的字节。
    /// </remarks>
    [Fact]
    public void 行数上限与定宽单行字节上限同值但各自独立()
    {
        Assert.Equal(1_048_576, ExcelConstants.MaxSheetRows);
        Assert.Equal(1_048_576, ExcelConstants.MaxFixedRowWidthBytes);
    }
}
