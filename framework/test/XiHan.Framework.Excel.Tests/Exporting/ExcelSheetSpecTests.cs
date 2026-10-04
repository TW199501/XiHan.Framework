// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// 表规格与导出结果契约测试
/// </summary>
public class ExcelSheetSpecTests
{
    /// <summary>
    /// 行集合为 null 时在初始化阶段就拒绝
    /// </summary>
    [Fact]
    public void 表规格_拒绝空行集合()
    {
        Assert.Throws<ArgumentNullException>(() => new ExcelSheetSpec
        {
            SheetName = "S1", RowType = typeof(SampleRow), Columns = [], Rows = null!
        });
    }

    /// <summary>
    /// 表名为 null/空白时在初始化阶段就拒绝
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 表规格_拒绝空表名(string name)
    {
        Assert.Throws<ArgumentException>(() => new ExcelSheetSpec
        {
            SheetName = name, RowType = typeof(SampleRow), Columns = [], Rows = Array.Empty<SampleRow>()
        });
    }

    /// <summary>
    /// 排版开关默认全开，未表态的流式与行数预期保持为空
    /// </summary>
    [Fact]
    public void 表规格_默认排版开关全开()
    {
        var rows = new List<SampleRow> { new() { AwbNo = "AWB1" } };

        var spec = new ExcelSheetSpec
        {
            SheetName = "S1",
            RowType = typeof(SampleRow),
            Columns = [new ExcelColumn<SampleRow> { Key = "awb", Header = "提单号", Value = r => r.AwbNo }],
            Rows = rows
        };

        Assert.Equal("S1", spec.SheetName);
        Assert.Equal(typeof(SampleRow), spec.RowType);
        Assert.Single(spec.Columns);
        Assert.Same(rows, spec.Rows);
        Assert.True(spec.FreezeHeader);
        Assert.True(spec.AutoFilter);
        Assert.True(spec.HeaderBold);
        Assert.Equal("#D9E1F2", spec.HeaderFill);
        Assert.True(spec.Borders);
        Assert.Null(spec.Title);
        Assert.Null(spec.ExpectedRowCount);
        Assert.Null(spec.ForceStreaming);
    }

    /// <summary>
    /// 降级结果必须带样式跳过理由
    /// </summary>
    [Fact]
    public void 导出结果_降级必须带理由()
    {
        var degraded = ExcelExportResult.Degraded(ExcelFormat.Xlsx, ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "流式模式不支持条件样式");
        Assert.False(degraded.StylingApplied);
        Assert.NotNull(degraded.StylingSkipReason);
        Assert.True(ExcelExportResult.Styled(ExcelFormat.Csv, ".csv", "text/csv").StylingApplied);
    }

    /// <summary>
    /// 工厂产出的格式、扩展名与内容类型原样带回，带样式时不填理由
    /// </summary>
    [Fact]
    public void 导出结果_带样式不带理由()
    {
        var styled = ExcelExportResult.Styled(ExcelFormat.Xlsx, ExcelConstants.ExtensionXlsx, ExcelConstants.XlsxContentType);

        Assert.Equal(ExcelFormat.Xlsx, styled.Format);
        Assert.Equal(".xlsx", styled.FileExtension);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", styled.ContentType);
        Assert.Null(styled.StylingSkipReason);

        var degraded = ExcelExportResult.Degraded(ExcelFormat.Txt, ExcelConstants.ExtensionTxt, ExcelConstants.PlainTextContentType, "文字档不承载样式");
        Assert.Equal("文字档不承载样式", degraded.StylingSkipReason);
    }
}
