// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Importing;

namespace XiHan.Framework.Excel.Importing;

/// <summary>
/// 导入门面：按选项里有没有列定义，把一次导入交给 <see cref="ExcelDataReaderImporter" /> 或
/// <see cref="FixedWidthTextImporter" />
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExcelImportOptions.FixedColumns"/> 非 <c>null</c> 就走固定宽度，否则走 ExcelDataReader；
/// <b><see cref="ExcelImportOptions.Format"/> 不参与路由</b>。列定义是空集合时仍然走固定宽度。
/// </para>
/// <para>
/// 门面不嗅探签章、不解析编码、不预检选项、不改写异常；入参检查由被选中的实现在首次取行时执行。
/// </para>
/// <para>
/// 本类型不碰传入的流，也不关闭它。两个实现都是无状态的读取器，可以并发共用同一个门面实例。
/// </para>
/// </remarks>
/// <param name="excelDataReaderImporter">容器与分隔符路径的读取器，<see cref="ExcelImportOptions.FixedColumns"/> 为 <c>null</c> 时使用</param>
/// <param name="fixedWidthTextImporter">固定宽度路径的读取器，给了列定义时使用</param>
public sealed class ExcelImporter(
    ExcelDataReaderImporter excelDataReaderImporter,
    FixedWidthTextImporter fixedWidthTextImporter) : IExcelImporter
{
    private readonly ExcelDataReaderImporter _excelDataReaderImporter =
        excelDataReaderImporter ?? throw new ArgumentNullException(nameof(excelDataReaderImporter));

    private readonly FixedWidthTextImporter _fixedWidthTextImporter =
        fixedWidthTextImporter ?? throw new ArgumentNullException(nameof(fixedWidthTextImporter));

    /// <summary>
    /// 逐行读入一份档，按列定义选一条读取路径
    /// </summary>
    /// <param name="input">输入流，必须可读且可定位；所有权在调用方，两条路径都只读取不关闭</param>
    /// <param name="options">导入选项，传 <c>null</c> 等同于使用 <see cref="ExcelImportOptions"/> 的默认值，
    /// 也就是不给出列定义、交给容器路径</param>
    /// <param name="cancellationToken">取消令牌，原样转发给被选中的那条路径</param>
    /// <returns>被选中那条路径交出的逐行异步序列；门面不加任何包装</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> 不可读或不可定位，或
    /// <see cref="ExcelImportOptions.TextEncodingName"/> 无法解析</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ExcelImportOptions.MaxRowCount"/> 高于本次生效的行数上限或不是正整数——上限默认是框架硬上限
    /// <see cref="XiHan.Framework.Excel.Abstractions.ExcelConstants.DefaultMaxImportRows"/>，
    /// 应用把 <see cref="XiHan.Framework.Excel.Abstractions.XiHanExcelOptions.MaxImportRows"/> 配得更低时以配置值为准，
    /// 两条读取路径同判</exception>
    /// <exception cref="InvalidOperationException">档头判不出格式、容器读不通、表名不存在，
    /// 或列定义不成立（缺列定义、空集合、键为空或重复、宽度非正、编码是宽字节）；
    /// 具体由被选中的实现决定，见各实现的 <see cref="ReadAsync"/> 说明</exception>
    /// <exception cref="System.Text.DecoderFallbackException">文字档的实际字节在所用编码下解不开</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// 这些异常都在<u>首次取行</u>时才抛出；本方法本身不做会抛的检查。
    /// </remarks>
    public IAsyncEnumerable<ExcelImportRow> ReadAsync(
        Stream input,
        ExcelImportOptions? options = null,
        CancellationToken cancellationToken = default)
        => options?.FixedColumns is null
            ? _excelDataReaderImporter.ReadAsync(input, options, cancellationToken)
            : _fixedWidthTextImporter.ReadAsync(input, options, cancellationToken);
}
