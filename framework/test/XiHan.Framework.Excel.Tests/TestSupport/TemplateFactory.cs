// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using ClosedXML.Excel;

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 现场生成模板档写进 <see cref="MemoryStream" />，供模板渲染测试使用
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。模板一律当场用 ClosedXML 造，每次调用交回一条新的流。
/// </remarks>
public static class TemplateFactory
{
    /// <summary>
    /// 单据模板：A1 是单值占位 <c>{{Company}}</c>，A2 是集合占位 <c>{{Items.Name}}</c>
    /// </summary>
    /// <returns>写有占位符的模板流，位置已回到起点</returns>
    public static MemoryStream BuildInvoiceTemplate()
        => Build("发票", ("A1", "{{Company}}"), ("A2", "{{Items.Name}}"));

    /// <summary>
    /// 带集合两列与一行静态表尾的模板：A1 单值、A2/B2 集合占位、A4 静态文案
    /// </summary>
    /// <returns>写有占位符的模板流，位置已回到起点</returns>
    public static MemoryStream BuildInvoiceWithFooterTemplate()
        => Build("发票", ("A1", "{{Company}}"), ("A2", "{{Items.Name}}"), ("B2", "{{Items.Qty}}"), ("A4", "合计"));

    /// <summary>
    /// 按给定格位与文案造一张单表模板
    /// </summary>
    /// <param name="cells">要写进模板的格位与文案</param>
    /// <returns>模板流，位置已回到起点</returns>
    public static MemoryStream Build(params (string Address, string Text)[] cells)
        => Build("发票", cells);

    /// <summary>
    /// 按给定表名、格位与文案造一张单表模板
    /// </summary>
    /// <param name="sheetName">模板的工作表名</param>
    /// <param name="cells">要写进模板的格位与文案</param>
    /// <returns>模板流，位置已回到起点</returns>
    public static MemoryStream Build(string sheetName, params (string Address, string Text)[] cells)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(sheetName);

        foreach (var (address, text) in cells)
        {
            worksheet.Cell(address).Value = text;
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        return stream;
    }
}
