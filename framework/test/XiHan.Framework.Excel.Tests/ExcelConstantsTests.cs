// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Exporting;

namespace XiHan.Framework.Excel.Tests;

/// <summary>
/// Excel 常量测试：钉住对外承诺的那几个数值本身，以及两条 xlsx 写出路径读的确实是同一个常量
/// </summary>
/// <remarks>
/// <para>
/// 这些常量是写进 XML 文档与异常消息里的对外数值，改一个就等于改承诺，因此逐条钉住数值本身：
/// 有人把上限「顺手调一档」时，红的应该是这里，而不是等到某份档写坏才发现。
/// </para>
/// <para>
/// 接线断言走两个导出器的 <c>internal</c> 读点，不走反射：两个导出器都只有一处读
/// <see cref="ExcelConstants.MaxSheetRows"/> 的地方（各自的 <c>_maxSheetRows</c> 字段初始值），
/// 公开构造函数不注入，读到的就是那个常量。任何一条路径改成读别的常量、读一个写死的字面量，
/// 或干脆把上限判定删掉，这里与导出器测试里对应的边界用例会一起红。
/// </para>
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
    /// 上限只在逐行循环里判，真实上限下要写满一百多万行才触线，因此「两条路径判得一样」不能靠跑满行数来证明，
    /// 改由读点证明：两个导出器经公开构造函数建出来时，生效的上限都等于那个常量。
    /// 只给一条路径加判定、或让一条路径读别的数值，这条与导出器测试里对应的边界用例会分别红。
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
    /// 两者都是 1 MiB 那个数（1,048,576），一个数的是工作表的行、一个数的是定宽档单行的字节，
    /// 语义毫不相干。把它们合并成一个常量会让「改一道界」悄悄改掉另一道，因此这里钉住两个名字各自的值：
    /// 删掉其中一个改成复用另一个，编译就会红；只改其中一个的值，这条也会红。
    /// </remarks>
    [Fact]
    public void 行数上限与定宽单行字节上限同值但各自独立()
    {
        Assert.Equal(1_048_576, ExcelConstants.MaxSheetRows);
        Assert.Equal(1_048_576, ExcelConstants.MaxFixedRowWidthBytes);
    }
}
