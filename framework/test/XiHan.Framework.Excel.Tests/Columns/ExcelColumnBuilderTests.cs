// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Attributes;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Columns;
using XiHan.Framework.Excel.Exporting;
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

    /// <summary>
    /// 特性上的负列宽不会被当成固定列宽收下，构建时点名抛异常
    /// </summary>
    [Fact]
    public void 特性列宽为负数时抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExcelColumnBuilder.CreateColumns<NegativeWidthRow>());
    }

    /// <summary>
    /// 构建器列交出的是 getter 自己的异常型别，不包一层反射包装异常
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同一契约的两条列来源（手写 <c>ExcelColumn&lt;TRow&gt;</c> 与构建器产出的列）必须抛同一个型别：调用方按
    /// <c>IExcelExporter</c> 文档化的型别 <c>catch</c> 时，不该因为列是谁建的而接不到。反射 <c>GetValue</c>
    /// 会把 getter 的异常包成 <see cref="System.Reflection.TargetInvocationException"/>，那条型别不在任何
    /// 导出入口的 <c>&lt;exception&gt;</c> 清单里，调用方 <c>catch (MyDomainException)</c> 直接落空。
    /// </para>
    /// <para>
    /// 断 <c>InnerException</c> 为空是为的不让「包了一层但把原异常放在里面」蒙过去：包过的型别已经不是
    /// <see cref="FormatException"/>，而 <c>Assert.Throws&lt;T&gt;</c> 按精确型别判，派生型别同样收不下。
    /// </para>
    /// </remarks>
    [Fact]
    public void 构建器取值不包装getter异常()
    {
        var column = ExcelColumnBuilder.CreateColumns<ThrowingRow>().Single(c => c.Key == nameof(ThrowingRow.Bad));

        var failure = Assert.Throws<FormatException>(() => column.GetValue(new ThrowingRow()));

        Assert.Equal("取值失败", failure.Message);
        Assert.Null(failure.InnerException);
    }

    /// <summary>
    /// 同一个 getter 在手写列与构建器列上交出的异常型别与消息都一致
    /// </summary>
    [Fact]
    public void 手写列与构建器列的取值异常型别一致()
    {
        var row = new ThrowingRow();

        var built = ExcelColumnBuilder.CreateColumns<ThrowingRow>().Single(c => c.Key == nameof(ThrowingRow.Bad));
        var hand = new ExcelColumn<ThrowingRow>
        {
            Key = nameof(ThrowingRow.Bad),
            Header = "坏值",
            Value = r => r.Bad
        };

        var builtFailure = Assert.Throws<FormatException>(() => built.GetValue(row));
        var handFailure = Assert.Throws<FormatException>(() => hand.GetValue(row));

        Assert.Equal(handFailure.GetType(), builtFailure.GetType());
        Assert.Equal(handFailure.Message, builtFailure.Message);
    }

    /// <summary>
    /// 编译成强型别委托后照常取到属性值，含值类型的装箱结果
    /// </summary>
    [Fact]
    public void 构建器取值仍能读出各型别的属性值()
    {
        var columns = ExcelColumnBuilder.CreateColumns<ThrowingRow>();
        var row = new ThrowingRow { Good = 7 };

        Assert.Equal(7, Assert.IsType<int>(columns.Single(c => c.Key == nameof(ThrowingRow.Good)).GetValue(row)));
        Assert.Equal("7", columns.Single(c => c.Key == nameof(ThrowingRow.Text)).GetValue(row));
        Assert.Equal(3, columns.Count);
    }

    /// <summary>
    /// <c>ref</c> 返回的读取器照常成列并取到值：改成编译取值不得让这种形状在建列时就抛或静默少一栏
    /// </summary>
    /// <remarks>
    /// 表达式树没有「取引用所指的值」这个节点（<c>Expression.Convert(Int32&amp;, object)</c> 直接抛
    /// <see cref="InvalidOperationException"/>，探针 <c>t13-b3-probe-shapes2.txt</c> 实测），这类属性只能留在
    /// 反射路径上。本条钉的是形状没有被改判成「不成列」，值也仍与反射一致。
    /// </remarks>
    [Fact]
    public void ref返回的读取器照常成列且取到值()
    {
        var columns = ExcelColumnBuilder.CreateColumns<ByRefRow>();
        var row = new ByRefRow();

        Assert.Equal(2, columns.Count);
        Assert.Equal(7, Assert.IsType<int>(columns.Single(c => c.Key == nameof(ByRefRow.Counter)).GetValue(row)));
        Assert.Equal("AWB1", columns.Single(c => c.Key == nameof(ByRefRow.Name)).GetValue(row));
    }

    /// <summary>
    /// <c>new</c> 遮蔽的同名属性只留一列，且留下的是 <c>DeclaringType</c> 最深的那一个
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetProperties(Public | Instance)</c> 会把 <c>ShadowRow.Id</c>（<c>Int32</c>）与被它遮蔽的
    /// <c>ShadowBaseRow.Id</c>（<c>Object</c>）一起交出，不去重就是两列同键 <c>Id</c>。留哪一个按 CLR 可见成员
    /// 语义定：派生类那个才是 <c>ShadowRow.Id</c>，基类那个交出的是另一份背衬的旧值。
    /// </para>
    /// <para>
    /// 「留第一个」不够——反射交出的顺序不是承诺，两个候选的先后随运行时而变；必须比 <c>DeclaringType</c>。
    /// </para>
    /// </remarks>
    [Fact]
    public void 遮蔽属性只留派生类那一列并取到派生值()
    {
        var columns = ExcelColumnBuilder.CreateColumns<ShadowRow>();
        var row = new ShadowRow();

        Assert.Equal([nameof(ShadowRow.Id)], columns.Select(c => c.Key));
        Assert.Equal(7, Assert.IsType<int>(columns.Single().GetValue(row)));
        Assert.Equal(typeof(ShadowRow), columns.Single().RowType);
    }

    /// <summary>
    /// 没有遮蔽的行类型不因去重少列
    /// </summary>
    [Fact]
    public void 无遮蔽的行类型列数不变()
    {
        var columns = ExcelColumnBuilder.CreateColumns<PlainRowNoShadow>();

        Assert.Equal([nameof(PlainRowNoShadow.First), nameof(PlainRowNoShadow.Second)], columns.Select(c => c.Key));
        Assert.Equal([0, 1], columns.Select(c => c.Order));
    }

    /// <summary>
    /// 忽略标记标在遮蔽出来的派生属性上时整个键不成列，被盖住的基类属性不得顶上来
    /// </summary>
    /// <remarks>
    /// 判定顺序是「先去重、后判忽略」：同名之间代表这个键的是 CLR 看得见的 <c>ShadowIgnoredRow.Id</c>，
    /// 它标了忽略就该整键没有这一栏。反过来先判忽略的话，<c>ShadowIgnoredBaseRow.Id</c> 会顶上来，
    /// 交出一栏调用方明明标了不要、值还是基类那份过时的。
    /// </remarks>
    [Fact]
    public void 忽略标在遮蔽属性上时整个键不成列()
    {
        Assert.Empty(ExcelColumnBuilder.CreateColumns<ShadowIgnoredRow>());
    }

    /// <summary>
    /// 忽略标记标在被盖住的基类属性上时，派生那个照常成列
    /// </summary>
    [Fact]
    public void 忽略标在被盖住的基类属性上时派生列照常()
    {
        var columns = ExcelColumnBuilder.CreateColumns<ShadowBaseIgnoredDto>();
        var row = new ShadowBaseIgnoredDto();

        Assert.Equal([nameof(ShadowBaseIgnoredDto.Id)], columns.Select(c => c.Key));
        Assert.Equal(7, Assert.IsType<int>(columns.Single().GetValue(row)));
    }

    /// <summary>
    /// 遮蔽属性在同一份规格走三条导出路径时同判：都只落一栏 <c>Id</c>，取的都是派生类那个值
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一条不看构建器自己的清单，看落进档的结果：去重前流式路径被「重复列键」拒掉（抛），
    /// 而 xlsx 全量与文字档照常写出两个 <c>Id</c> 栏位、其中一栏是基类旧值——同一份规格三条路径三套表现。
    /// 去重之后「同判」是自然结果，不是另加的一致性补丁。
    /// </para>
    /// <para>
    /// 三条路径各自回读自己格式的产物：xlsx 走工作簿回读，文字档按正文字面比。流式那条同时证明
    /// 它不再走进 <c>EnsureDistinctKeys</c> 的拒绝分支。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 遮蔽属性在三条导出路径都只落一栏()
    {
        var columns = ExcelColumnBuilder.CreateColumns<ShadowRow>();
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(ShadowRow),
            Columns = columns,
            Rows = new[] { new ShadowRow() }
        };

        using var fullWorkbook = new XLWorkbook(await ExportWithClosedXmlAsync(spec));
        var fullSheet = fullWorkbook.Worksheet(1);

        Assert.Equal("Id", fullSheet.Cell(1, 1).GetString());
        Assert.Equal(7, fullSheet.Cell(2, 1).GetValue<int>());
        Assert.True(fullSheet.Cell(1, 2).IsEmpty());

        using var streamWorkbook = new XLWorkbook(await ExportWithMiniExcelStreamAsync(spec));
        var streamSheet = streamWorkbook.Worksheet(1);

        Assert.Equal("Id", streamSheet.Cell(1, 1).GetString());
        Assert.Equal(7, streamSheet.Cell(2, 1).GetValue<int>());
        Assert.True(streamSheet.Cell(1, 2).IsEmpty());

        var textStream = await ExportWithTextAsync(spec);

        Assert.Equal("Id" + "\r\n" + "7" + "\r\n", BodyOf(textStream));
    }

    /// <summary>
    /// 走 xlsx 全量路径写出一份档，回到起点交回可读的流
    /// </summary>
    private static async Task<MemoryStream> ExportWithClosedXmlAsync(ExcelSheetSpec spec)
    {
        var stream = new MemoryStream();

        await new ClosedXmlExporter(new XiHanExcelOptions()).ExportAsync(
            stream, spec, TestContext.Current.CancellationToken);

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// 走 xlsx 流式路径写出一份档，回到起点交回可读的流
    /// </summary>
    private static async Task<MemoryStream> ExportWithMiniExcelStreamAsync(ExcelSheetSpec spec)
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(stream, spec, TestContext.Current.CancellationToken);

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// 走文字档路径写出 csv（不带 BOM，正文按字面比）
    /// </summary>
    private static async Task<MemoryStream> ExportWithTextAsync(ExcelSheetSpec spec)
    {
        var stream = new MemoryStream();

        await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance).ExportAsync(
            stream, spec, ExcelFormat.Csv, new ExcelTextOptions { EncodingName = "utf-8" },
            TestContext.Current.CancellationToken);

        return stream;
    }

    /// <summary>
    /// 取文字档正文，剥掉前导 BOM
    /// </summary>
    private static string BodyOf(MemoryStream stream)
        => Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');

    /// <summary>
    /// 带抛出型计算属性与多种值型别的测试行类型
    /// </summary>
    /// <remarks>
    /// 只作为测试夹具，不进正式 API。<see cref="Bad"/> 的 getter 抛 <see cref="FormatException"/>，
    /// 用于比对「构建器列」与「手写列」的异常型别。
    /// </remarks>
    private class ThrowingRow
    {
        /// <summary>
        /// 数值属性，取值要走一次装箱
        /// </summary>
        public int Good { get; set; }

        /// <summary>
        /// 引用型别属性
        /// </summary>
        public string Text => "7";

        /// <summary>
        /// 计算属性，getter 直接抛出业务异常型别
        /// </summary>
        public string Bad => throw new FormatException("取值失败");
    }

    /// <summary>
    /// 带 <c>ref</c> 返回读取器的测试行类型
    /// </summary>
    /// <remarks>
    /// 只作为测试夹具。<see cref="Counter"/> 的读取器交出引用（<c>Int32&amp;</c>），反射读得出来而表达式树读不出来，
    /// 用于钉住「构建器改用编译取值」没有把这种形状弄坏。
    /// </remarks>
    private class ByRefRow
    {
        private int _counter = 7;

        /// <summary>
        /// <c>ref</c> 返回的计算属性
        /// </summary>
        public ref int Counter => ref _counter;

        /// <summary>
        /// 普通属性，作对照
        /// </summary>
        public string Name { get; set; } = "AWB1";
    }

    /// <summary>
    /// <c>new</c> 遮蔽的测试行类型：基类与派生类的同名属性各有背衬，值不同
    /// </summary>
    /// <remarks>
    /// <c>GetProperties(Public | Instance)</c> 对 <see cref="ShadowRow"/> 交出两个 <c>Id</c>
    /// （<c>decl=ShadowRow Int32</c> 与 <c>decl=ShadowBaseRow Object</c>，探针
    /// <c>t13-b3-probe-shapes2.txt</c> 实测），不去重就是两列同键。
    /// </remarks>
    private class ShadowBaseRow
    {
        /// <summary>
        /// 被遮蔽的基类属性，值与派生类那个不同，用来证明留下的是哪一个
        /// </summary>
        public object Id { get; set; } = "基类旧值";
    }

    /// <summary>
    /// 以 <c>new int Id</c> 遮蔽基类 <c>object Id</c> 的行类型
    /// </summary>
    private class ShadowRow : ShadowBaseRow
    {
        /// <summary>
        /// 遮蔽后的派生属性，CLR 可见成员语义下这才是 <c>ShadowRow.Id</c>
        /// </summary>
        public new int Id { get; set; } = 7;
    }

    /// <summary>
    /// 基类 <c>Id</c> 可读、派生 <c>Id</c> 遮蔽它并标了忽略的行类型
    /// </summary>
    private class ShadowIgnoredBaseRow
    {
        public object Id { get; set; } = "基类旧值";
    }

    /// <summary>
    /// 派生属性标 <see cref="ExcelIgnoreAttribute"/> 的遮蔽行类型，用于「忽略整键」那条
    /// </summary>
    private class ShadowIgnoredRow : ShadowIgnoredBaseRow
    {
        [ExcelIgnore]
        public new int Id { get; set; } = 7;
    }

    /// <summary>
    /// 基类 <c>Id</c> 标了忽略的行类型
    /// </summary>
    private class ShadowBaseIgnoredRow
    {
        [ExcelIgnore]
        public object Id { get; set; } = "基类旧值";
    }

    /// <summary>
    /// 遮蔽「被忽略的基类属性」的行类型，派生那个没标忽略，照常成列
    /// </summary>
    private class ShadowBaseIgnoredDto : ShadowBaseIgnoredRow
    {
        public new int Id { get; set; } = 7;
    }

    /// <summary>
    /// 接口行类型纳入继承接口声明的属性
    /// </summary>
    /// <remarks>
    /// <para>
    /// 类沿继承链给出属性，接口不给：<c>typeof(IOrder).GetProperties()</c> 只交本接口自己声明的 <c>No</c>，
    /// <c>IOrderBase.Id</c> 不在里面，于是接口行类型导出的档整栏少一栏且没有任何提示。
    /// </para>
    /// <para>
    /// 键的顺序按「基接口在前、本接口自己声明的在后」写进断言：多个基接口按继承深度由远到近排，
    /// 不依赖 <c>GetInterfaces()</c> 的返回顺序。取值要证到能落进单元格的形状，所以两列都取一遍值。
    /// </para>
    /// </remarks>
    [Fact]
    public void 接口行类型纳入继承接口的属性()
    {
        var columns = ExcelColumnBuilder.CreateColumns<IOrder>();

        Assert.Equal([nameof(IOrderBase.Id), nameof(IOrder.No)], columns.Select(c => c.Key));
        Assert.Equal([0, 1], columns.Select(c => c.Order));
        Assert.All(columns, column => Assert.Equal(typeof(IOrder), column.RowType));

        var row = new OrderRow();

        Assert.Equal(42, Assert.IsType<int>(columns.Single(c => c.Key == nameof(IOrderBase.Id)).GetValue(row)));
        Assert.Equal("AWB1", columns.Single(c => c.Key == nameof(IOrder.No)).GetValue(row));
    }

    /// <summary>
    /// 接口与基接口的同名属性同样只留一列，留派生接口那一个
    /// </summary>
    [Fact]
    public void 接口与基接口同名属性只留一列()
    {
        var columns = ExcelColumnBuilder.CreateColumns<IShadowOrder>();
        var row = new ShadowOrderRow();

        Assert.Equal([nameof(IShadowOrder.Id)], columns.Select(c => c.Key));
        Assert.Equal(7, Assert.IsType<int>(columns.Single().GetValue(row)));
    }

    /// <summary>
    /// 接口行类型夹具的基接口，只声明 <see cref="Id"/>
    /// </summary>
    private interface IOrderBase
    {
        /// <summary>
        /// 单号
        /// </summary>
        int Id { get; }
    }

    /// <summary>
    /// 接口行类型夹具：继承基接口再声明一个属性
    /// </summary>
    private interface IOrder : IOrderBase
    {
        /// <summary>
        /// 提单号
        /// </summary>
        string No { get; }
    }

    /// <summary>
    /// 基接口与派生接口同名属性（接口侧的 <c>new</c> 遮蔽）
    /// </summary>
    private interface IShadowBase
    {
        /// <summary>
        /// 基接口那个 <c>Id</c>
        /// </summary>
        object Id { get; }
    }

    /// <summary>
    /// 以 <c>new int Id</c> 遮蔽基接口 <c>object Id</c> 的接口行类型
    /// </summary>
    private interface IShadowOrder : IShadowBase
    {
        /// <summary>
        /// 遮蔽后的派生 <c>Id</c>
        /// </summary>
        new int Id { get; }
    }

    /// <summary>
    /// <see cref="IOrder"/> 的实现，供取值断言
    /// </summary>
    private sealed class OrderRow : IOrder
    {
        public int Id { get; set; } = 42;

        public string No { get; set; } = "AWB1";
    }

    /// <summary>
    /// <see cref="IShadowOrder"/> 的实现：派生属性与基接口的实现交出两个不同的值
    /// </summary>
    private sealed class ShadowOrderRow : IShadowOrder
    {
        public int Id { get; set; } = 7;

        object IShadowBase.Id => "基类旧值";
    }

    /// <summary>
    /// 完全不带导出特性的测试行类型，表头只能由描述信息回退得到
    /// </summary>
    private class PlainRowNoShadow
    {
        /// <summary>
        /// 一列
        /// </summary>
        public string First { get; set; } = string.Empty;

        /// <summary>
        /// 两列
        /// </summary>
        public string Second { get; set; } = string.Empty;
    }

    /// <summary>
    /// 特性上写了非法列宽的测试行类型
    /// </summary>
    /// <remarks>
    /// <c>double.NaN</c> 与无穷大不是合法的特性参数（编译期常量表达式，CS0182），只有负数能从特性侧抵达，
    /// 因此这里只钉负数这一条可达分支。
    /// </remarks>
    private class NegativeWidthRow
    {
        /// <summary>
        /// 列宽写成负数，不可能是合法列宽
        /// </summary>
        [ExcelColumn("甲", Width = -1.5)]
        public string Name { get; set; } = string.Empty;
    }
}
