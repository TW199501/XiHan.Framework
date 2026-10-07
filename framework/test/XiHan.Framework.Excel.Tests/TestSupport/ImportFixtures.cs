// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using ClosedXML.Excel;

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 导入侧夹具：当场造 xlsx 容器与文字档字节，仓库里不留二进制档
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="TemplateFactory" /> 的分工：那份只管模板渲染要的单值占位模板，本份管导入要读的容器形态
/// （合并格、重名表头、多表、只有表头）。都用 ClosedXML 现造。
/// </para>
/// <para>
/// 每个方法交回新流，测试之间不共用流。文字档一律 <c>Position = 0</c>；Big5 一支用严格回退编码造字节，
/// 收不下的字符在造夹具时就抛。
/// </para>
/// </remarks>
internal static class ImportFixtures
{
    /// <summary>
    /// 不带 BOM 的 UTF-8，测试里的文字档默认用它
    /// </summary>
    internal static readonly UTF8Encoding Utf8NoBom = new(false, true);

    /// <summary>
    /// 严格回退的 Big5，造繁体文字档字节用
    /// </summary>
    /// <remarks>
    /// 收不下的字符在编码时就抛。简体「单」(U+5355) 不在 Big5 里，繁体档夹具用「提單號」。
    /// </remarks>
    internal static readonly Encoding StrictBig5 =
        Encoding.GetEncoding("big5", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    /// <summary>
    /// 单列单行的 xlsx：A1 表头「提单号」，A2 值「AWB1」
    /// </summary>
    /// <returns>位置在起点的 xlsx 流</returns>
    internal static MemoryStream OneRowXlsx()
        => Xlsx([["提单号"], ["AWB1"]]);

    /// <summary>
    /// 只有表头、没有数据行的 xlsx
    /// </summary>
    /// <returns>位置在起点的 xlsx 流</returns>
    internal static MemoryStream HeaderOnlyXlsx()
        => Xlsx([["提单号", "重量"]]);

    /// <summary>
    /// 表头重名的 xlsx，数据行给两个可区分的值
    /// </summary>
    /// <param name="headers">表头文案，可以重复</param>
    /// <returns>位置在起点的 xlsx 流</returns>
    internal static MemoryStream DuplicateHeaderXlsx(params string[] headers)
    {
        var values = new string[headers.Length];

        for (var column = 0; column < headers.Length; column++)
        {
            values[column] = $"值{column + 1}";
        }

        return Xlsx([headers, values]);
    }

    /// <summary>
    /// 合并了 B2:C2 的 xlsx：A1/B1/C1 是表头，A2 是左上角值，B2（被合并吞掉的那格）没有值
    /// </summary>
    /// <returns>位置在起点的 xlsx 流</returns>
    internal static MemoryStream MergedCellXlsx()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("合并格");
        sheet.Cell("A1").Value = "第一列";
        sheet.Cell("B1").Value = "第二列";
        sheet.Cell("A2").Value = "左上角";
        sheet.Range("A2:B2").Merge();

        return Save(workbook);
    }

    /// <summary>
    /// 两张表的 xlsx：两张各自一列一行，值按表名区分，供「指名读哪一张」与「表名不存在」的用例
    /// </summary>
    /// <param name="firstName">第一张表名，值写成 <c>A-1</c></param>
    /// <param name="secondName">第二张表名，值写成 <c>B-2</c></param>
    /// <returns>位置在起点的 xlsx 流</returns>
    internal static MemoryStream TwoSheetXlsx(string firstName = "第一表", string secondName = "第二表")
    {
        using var workbook = new XLWorkbook();

        var first = workbook.Worksheets.Add(firstName);
        first.Cell("A1").Value = "单号";
        first.Cell("A2").Value = "A-1";

        var second = workbook.Worksheets.Add(secondName);
        second.Cell("A1").Value = "单号";
        second.Cell("A2").Value = "B-2";

        return Save(workbook);
    }

    /// <summary>
    /// 按行文本造 xlsx，第一行当表头写，格值一律以文本落格
    /// </summary>
    /// <param name="rows">行内容，逐格写入</param>
    /// <param name="sheetName">工作表名</param>
    /// <returns>位置在起点的 xlsx 流</returns>
    internal static MemoryStream Xlsx(IReadOnlyList<string[]> rows, string sheetName = "导入")
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column].Length > 0)
                {
                    sheet.Cell(row + 1, column + 1).Value = rows[row][column];
                }
            }
        }

        return Save(workbook);
    }

    /// <summary>
    /// 按给定格位与值造 xlsx，数值与日期按原 CLR 型别落格
    /// </summary>
    /// <param name="cells">格位与值</param>
    /// <returns>位置在起点的 xlsx 流</returns>
    internal static MemoryStream XlsxCells(params (string Address, object Value)[] cells)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("导入");

        foreach (var (address, value) in cells)
        {
            WriteCell(sheet.Cell(address), value);
        }

        return Save(workbook);
    }

    /// <summary>
    /// 按 CLR 型别把值落进格子
    /// </summary>
    /// <param name="cell">目标格</param>
    /// <param name="value">要落的值，数值一律按 double 落格（ClosedXML 的数值格就是这个形态）</param>
    /// <remarks>
    /// 按 <c>object</c> 的实际型别分派成 <see cref="XLCellValue"/>；分不到已知型别时按字串落格。
    /// </remarks>
    private static void WriteCell(IXLCell cell, object value)
    {
        switch (value)
        {
            case string text:
                cell.Value = text;
                break;

            case double number:
                cell.Value = number;
                break;

            case decimal number:
                cell.Value = Convert.ToDouble(number);
                break;

            case DateTime date:
                cell.Value = date;
                break;

            case bool flag:
                cell.Value = flag;
                break;

            default:
                cell.Value = value.ToString() ?? string.Empty;
                break;
        }
    }

    /// <summary>
    /// 表头加若干数据行的 CSV 文本（UTF-8，无 BOM）
    /// </summary>
    /// <param name="header">表头文案</param>
    /// <param name="dataRows">数据行数</param>
    /// <returns>位置在起点的流</returns>
    internal static MemoryStream Csv(string header, int dataRows)
    {
        var builder = new StringBuilder();
        builder.Append(header).Append("\r\n");

        for (var row = 1; row <= dataRows; row++)
        {
            builder.Append("AWB").Append(row.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        }

        return Text(builder.ToString());
    }

    /// <summary>
    /// 单列「重量」加若干数值行的 CSV（UTF-8，无 BOM）
    /// </summary>
    /// <param name="weights">每行的重量文本</param>
    /// <returns>位置在起点的流</returns>
    internal static MemoryStream CsvWeights(params string[] weights)
    {
        var builder = new StringBuilder("重量\r\n");

        foreach (var weight in weights)
        {
            builder.Append(weight).Append("\r\n");
        }

        return Text(builder.ToString());
    }

    /// <summary>
    /// 一段文字（UTF-8，无 BOM）
    /// </summary>
    /// <param name="text">文件内容</param>
    /// <returns>位置在起点的流</returns>
    internal static MemoryStream Text(string text) => new(Utf8NoBom.GetBytes(text));

    /// <summary>
    /// 一段带 UTF-8 BOM 的文字
    /// </summary>
    /// <param name="text">文件内容，不含 BOM，由本方法加上</param>
    /// <returns>位置在起点的流</returns>
    internal static MemoryStream TextWithUtf8Bom(string text)
        => new([.. new UTF8Encoding(true).GetPreamble(), .. Utf8NoBom.GetBytes(text)]);

    /// <summary>
    /// 指定编码的文字档字节（严格回退，收不下的字符在这里就抛）
    /// </summary>
    /// <param name="encoding">目标编码</param>
    /// <param name="text">文件内容</param>
    /// <returns>编码后的字节</returns>
    internal static byte[] Encode(Encoding encoding, string text) => encoding.GetBytes(text);

    /// <summary>
    /// 无 BOM 的 Big5 文字档
    /// </summary>
    /// <param name="text">文件内容，必须全部落在 Big5 字符集内</param>
    /// <returns>位置在起点的流</returns>
    internal static MemoryStream Big5Text(string text) => new(Encode(StrictBig5, text));

    /// <summary>
    /// 伪装成 Excel 的 HTML 表格档
    /// </summary>
    /// <returns>位置在起点的流</returns>
    internal static MemoryStream HtmlTable()
        => Text("<html><body><table><tr><td>提单号</td></tr></table></body></html>");

    /// <summary>
    /// 不是任何已知容器、也没有文字档形态的二进制垃圾
    /// </summary>
    /// <returns>位置在起点的流</returns>
    internal static MemoryStream BinaryGarbage() => new([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

    private static MemoryStream Save(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        return stream;
    }
}
