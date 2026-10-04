// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Columns;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Columns;

/// <summary>
/// 属性驱动列构建器测试
/// </summary>
public class ExcelColumnBuilderTests
{
    /// <summary>
    /// 带特性的列按特性顺序升序排列
    /// </summary>
    [Fact]
    public void 特性列_按Order升序且缺省排后()
    {
        var columns = ExcelColumnBuilder.CreateColumns<AnnotatedRow>();

        Assert.Equal(["乙", "甲"], columns.Select(c => c.Header));   // Order 10 / 1
    }

    /// <summary>
    /// 被忽略标记的属性不产出列
    /// </summary>
    [Fact]
    public void 特性列_忽略标记的列不出现()
    {
        var columns = ExcelColumnBuilder.CreateColumns<AnnotatedRow>();

        Assert.DoesNotContain(columns, c => c.Key == "Secret");
    }

    /// <summary>
    /// 无特性属性的标题取自描述信息，键仍是属性名
    /// </summary>
    [Fact]
    public void 特性列_无特性时标题走GetDescription回退()
    {
        var columns = ExcelColumnBuilder.CreateColumns<PlainRow>();

        Assert.Equal(nameof(PlainRow.Title), columns.Single().Key);
        Assert.Equal("标题", columns.Single().Header);   // [Description("标题")]
    }

    /// <summary>
    /// 同一行类型的结果被缓存，清除该类型缓存后重建出新引用
    /// </summary>
    [Fact]
    public void 构建器结果被缓存且可清()
    {
        var cached = ExcelColumnBuilder.CreateColumns<PlainRow>();

        Assert.Same(cached, ExcelColumnBuilder.CreateColumns<PlainRow>());

        ExcelColumnBuilder.ClearCache(typeof(PlainRow));

        // 重建得到新列表，重建结果同样进缓存，再取一次仍是同一引用
        var rebuilt = ExcelColumnBuilder.CreateColumns<PlainRow>();

        Assert.NotSame(cached, rebuilt);
        Assert.Same(rebuilt, ExcelColumnBuilder.CreateColumns<PlainRow>());
    }

    /// <summary>
    /// 取值委托读取真实属性值
    /// </summary>
    [Fact]
    public void 特性列_取值委托能读出真实属性值()
    {
        var columns = ExcelColumnBuilder.CreateColumns<AnnotatedRow>();
        var row = new AnnotatedRow { Name = "甲", Quantity = 3, Secret = "x" };

        var raw = columns.Single(c => c.Key == "Quantity").GetValue(row);

        Assert.Equal(3, Assert.IsType<int>(raw));
    }

    /// <summary>
    /// 无特性列一律排在带特性列之后，并按声明顺序排列
    /// </summary>
    [Fact]
    public void 特性列_无特性列排在带特性列之后()
    {
        var columns = ExcelColumnBuilder.CreateColumns<MixedRow>();

        Assert.Equal(["甲", "First", "Second"], columns.Select(c => c.Header));
        Assert.Equal([0, 1, 2], columns.Select(c => c.Order));
    }

    /// <summary>
    /// 未指定顺序的列排在所有显式指定顺序的列之后，显式顺序含 0
    /// </summary>
    [Fact]
    public void 特性列_未指定顺序的列排在显式顺序之后()
    {
        var columns = ExcelColumnBuilder.CreateColumns<PartlyOrderedRow>();

        // 显式 Order 升序：甲(0) 乙(1)；未指定顺序的两列按声明顺序：丙 Tail
        Assert.Equal(["甲", "乙", "丙", "Tail"], columns.Select(c => c.Header));
        Assert.Equal([0, 1, 2, 3], columns.Select(c => c.Order));
        Assert.Equal(["ZeroOrder", "Ordered", "Unordered", "Tail"], columns.Select(c => c.Key));
    }

    /// <summary>
    /// 特性上的呈现项映射到列，未标注的属性保持列默认值
    /// </summary>
    [Fact]
    public void 特性列_特性呈现项映射到列()
    {
        var annotated = ExcelColumnBuilder.CreateColumns<MixedRow>().Single(c => c.Key == nameof(MixedRow.Annotated));
        var plain = ExcelColumnBuilder.CreateColumns<PlainRow>().Single();

        Assert.Null(annotated.Width);
        Assert.Equal("0.00", annotated.NumberFormat);
        Assert.Equal(ExcelAlignment.Center, annotated.Alignment);
        Assert.True(annotated.Wrap);

        Assert.Null(plain.Width);
        Assert.Null(plain.NumberFormat);
        Assert.Equal(ExcelAlignment.Auto, plain.Alignment);
        Assert.False(plain.Wrap);
        Assert.Null(plain.CellStyle);
        Assert.Null(plain.FixedWidth);
        Assert.Equal(ExcelTextPadding.Right, plain.Padding);
        Assert.Equal(' ', plain.PadChar);
    }

    /// <summary>
    /// 特性上的列宽映射到列，未写列宽时是未指定
    /// </summary>
    [Fact]
    public void 特性列_特性列宽映射到列且缺省为未指定()
    {
        var columns = ExcelColumnBuilder.CreateColumns<AnnotatedRow>();

        // Quantity 标了 Width = 20.5，Name 标了特性但没写 Width，特性上的 0 一律映射成 null
        Assert.Equal(20.5, columns.Single(c => c.Key == nameof(AnnotatedRow.Quantity)).Width);
        Assert.Null(columns.Single(c => c.Key == nameof(AnnotatedRow.Name)).Width);
    }

    /// <summary>
    /// 索引器、只写属性和只有私有读取器的属性都不产出列
    /// </summary>
    [Fact]
    public void 特性列_索引器与公共读不到的属性不成列()
    {
        var columns = ExcelColumnBuilder.CreateColumns<NonColumnMemberRow>();

        Assert.Equal([nameof(NonColumnMemberRow.Name)], columns.Select(c => c.Key));
        Assert.Equal("AWB1", columns.Single().GetValue(new NonColumnMemberRow { Name = "AWB1" }));
    }

    /// <summary>
    /// 清空全部缓存后所有行类型都重建
    /// </summary>
    [Fact]
    public void 清空全部缓存后重建列()
    {
        var before = ExcelColumnBuilder.CreateColumns<AnnotatedRow>();

        ExcelColumnBuilder.ClearCache();

        Assert.NotSame(before, ExcelColumnBuilder.CreateColumns<AnnotatedRow>());
        Assert.Same(ExcelColumnBuilder.CreateColumns<AnnotatedRow>(), ExcelColumnBuilder.CreateColumns<AnnotatedRow>());
    }

    /// <summary>
    /// 清除单类型缓存时行类型不能为空
    /// </summary>
    [Fact]
    public void 清除缓存_行类型为空时抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => ExcelColumnBuilder.ClearCache(null!));
    }

    /// <summary>
    /// 构建出的列可以直接组装进表规格
    /// </summary>
    [Fact]
    public void 特性列_列清单可直接用于表规格()
    {
        var columns = ExcelColumnBuilder.CreateColumns<AnnotatedRow>();

        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            Columns = columns,
            Rows = new[] { new AnnotatedRow { Name = "甲", Quantity = 3 } },
            RowType = typeof(AnnotatedRow)
        };

        Assert.Same(columns, spec.Columns);
        Assert.Equal("乙", spec.Columns[0].Header);
    }
}
