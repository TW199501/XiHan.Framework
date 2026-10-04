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
    /// 固定宽度导入的单行列宽总和硬上限（字节）
    /// </summary>
    /// <remarks>
    /// 定宽档一行要先整行落进缓冲才能按字节切列，不设上界就等于让某一份档的一行决定本进程的内存占用，
    /// 与「默认实现不得无界增长」冲突。取 1 MiB：常见定长记录档一行不超过数 KB，这道界挡的是列宽配错
    /// （或恶意写宽）的请求，不影响正常档。列宽总和超过它时导入器<u>抛出</u>该上限与实际总和，
    /// 不夹改成较小的宽度、也不截断列清单——静默改写列布局会交回一份列位错开的档。
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
