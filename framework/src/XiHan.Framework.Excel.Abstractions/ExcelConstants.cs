// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions;

/// <summary>
/// Excel 导入导出常量定义
/// </summary>
/// <remarks>
/// 这里是框架侧的默认值与固定字面量，供选项默认值和各个提供程序共用，不随配置变化。
/// </remarks>
public static class ExcelConstants
{
    /// <summary>
    /// 默认流式写出阈值（行数），达到该规模改用流式导出
    /// </summary>
    public const int DefaultStreamingThreshold = 50_000;

    /// <summary>
    /// 默认自适应列宽采样行数
    /// </summary>
    public const int DefaultAutoWidthSampleRows = 500;

    /// <summary>
    /// 默认文本编码名称
    /// </summary>
    public const string DefaultEncodingName = "utf-8-bom";

    /// <summary>
    /// 默认导入行数硬上限
    /// </summary>
    public const int DefaultMaxImportRows = 1_000_000;

    /// <summary>
    /// 默认导入档大小上限（字节），即 256 MiB
    /// </summary>
    /// <remarks>
    /// 生效值取 <see cref="XiHanExcelOptions.MaxImportBytes"/>。两条导入路径都在读第一个字节之前按它判，
    /// 超限<u>拒收整份档</u>，不截断读取。
    /// </remarks>
    public const long DefaultMaxImportBytes = 268_435_456;

    /// <summary>
    /// xlsx 容器解压后总长上限（字节），即 2 GiB
    /// </summary>
    /// <remarks>
    /// 与 <see cref="MaxImportCompressionRatio"/>、<see cref="MaxImportEntryDecompressedBytes"/> 配合使用。
    /// 超限时导入器<u>拒收整份档</u>并点名解压后总长，不解压、也不交给工作簿读取器。
    /// <para>
    /// 本常量界定的是解压后的<u>字节数</u>，不是本进程的托管占用；工作簿读取器载入后的托管占用可达 GiB 量级。
    /// </para>
    /// </remarks>
    public const long MaxImportDecompressedBytes = 2_147_483_648;

    /// <summary>
    /// xlsx 容器里单个部件解压后长度上限（字节），即 1 GiB
    /// </summary>
    /// <remarks>
    /// 超限时导入器<u>拒收整份档</u>，并点名是哪个部件、它解压后有多长。
    /// </remarks>
    public const long MaxImportEntryDecompressedBytes = 1_073_741_824;

    /// <summary>
    /// xlsx 容器里单个部件解压比上限的默认值（解压后长度 ÷ 压缩后长度的倍数）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只对解压后长度不小于 1 MiB 的部件判，超过时导入器<u>拒收整份档</u>并点名该部件、它的解压比与生效中的上限。
    /// </para>
    /// <para>
    /// 生效值取 <see cref="XiHanExcelOptions.MaxImportCompressionRatio"/>，未接配置时用本常量；
    /// 该选项取 <c>0</c> 或负数表示不判解压比。解压比是启发式判据，高度重复的合法档也可能越过本值。
    /// </para>
    /// <para>
    /// <see cref="MaxImportEntryDecompressedBytes"/> 与 <see cref="MaxImportDecompressedBytes"/>
    /// 两道绝对上限不可配置，也不因关掉解压比而失效。
    /// </para>
    /// </remarks>
    public const int MaxImportCompressionRatio = 200;

    /// <summary>
    /// 一个单元格能承载的字符数上限
    /// </summary>
    /// <remarks>
    /// xlsx 自身的硬界。两条 xlsx 写出路径都在取值阶段拒掉超长字串并回报本常量，不截断。
    /// 全量工作簿路径还在写出第一格之前按它预检 <see cref="Exporting.ExcelColumn.Header"/> 与
    /// <see cref="Exporting.ExcelSheetSpec.Title"/>。
    /// </remarks>
    public const int MaxCellTextLength = 32_767;

    /// <summary>
    /// xlsx 单张工作表的行数上限（标题行与表头行都算在内）
    /// </summary>
    /// <remarks>
    /// xlsx 自身的硬界。两条 xlsx 写出路径按「这一行数据要落到工作表的第几行」比对，超出即<u>抛出</u>，
    /// 不丢行、不截断。上限按<u>单张工作表</u>各自计，标题行与表头行计入所在工作表，不做整簿累计。
    /// 文字档路径没有这道上限。
    /// </remarks>
    public const int MaxSheetRows = 1_048_576;

    /// <summary>
    /// 导入时单行列数的硬上限
    /// </summary>
    /// <remarks>
    /// 取 xlsx 自身的列上限（16,384 列，最后一列是 <c>XFD</c>）。容器导入器在<u>建键之前</u>逐行判
    /// <c>FieldCount</c>，超限即抛出并点名实际列数，不截断列清单。
    /// 固定宽度路径不受这道界约束，单行占用由 <see cref="MaxFixedRowWidthBytes"/> 限制。
    /// </remarks>
    public const int MaxImportColumns = 16_384;

    /// <summary>
    /// 本组件对 xlsx 数值格承诺能原样落格的有效数字位数上限
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本组件自定的取值域档位：<see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>、
    /// <see cref="double"/>、<see cref="float"/> 五类型的取值，其不变文化文本形态的有效数字多于本常量时，
    /// 两条 xlsx 写出路径<u>一律抛出</u>，不写入、不降级，也不把值改写成较短的数。
    /// </para>
    /// <para>
    /// <see cref="float"/> 先展开成 <see cref="double"/> 再判（<c>0.1f</c> 就会被拒）。
    /// 需要完整精度的值由呼叫端转成字符串栏位。
    /// </para>
    /// </remarks>
    public const int MaxExactNumericSignificantDigits = 15;

    /// <summary>
    /// 固定宽度导入的单行列宽总和硬上限（字节）
    /// </summary>
    /// <remarks>
    /// 列宽总和超过它时导入器<u>抛出</u>该上限与实际总和，不夹改成较小的宽度、也不截断列清单。
    /// </remarks>
    public const int MaxFixedRowWidthBytes = 1_048_576;

    /// <summary>
    /// CSV 内容类型
    /// </summary>
    public const string CsvContentType = "text/csv";

    /// <summary>
    /// 纯文本内容类型
    /// </summary>
    public const string PlainTextContentType = "text/plain";

    /// <summary>
    /// xlsx 内容类型
    /// </summary>
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// CSV 文件扩展名
    /// </summary>
    public const string ExtensionCsv = ".csv";

    /// <summary>
    /// 纯文本文件扩展名
    /// </summary>
    public const string ExtensionTxt = ".txt";

    /// <summary>
    /// xlsx 文件扩展名
    /// </summary>
    public const string ExtensionXlsx = ".xlsx";
}
