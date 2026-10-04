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
/// 路由规则只有一条：<see cref="ExcelImportOptions.FixedColumns"/> 非 <c>null</c> 就走固定宽度，
/// 否则就走 ExcelDataReader。<b><see cref="ExcelImportOptions.Format"/> 不参与路由</b>——格式留空且列定义也留空时，
/// 档头判别该判什么由 <see cref="ExcelDataReaderImporter"/> 自己判并自己抛，门面不重复那道判定；
/// 列定义是空集合时仍然走固定宽度，由那条路径把「一列都没有」报出来，门面不悄悄改投另一条路径。
/// </para>
/// <para>
/// 门面<u>不做任何业务判断</u>：不嗅探签章、不解析编码、不预检选项、不代任何一边改写异常。
/// 这些能力各有一份实现，重复一遍就多出两套会各自漂动的口径。因此入参检查落在哪一格、
/// 什么时候落（<see cref="IExcelImporter.ReadAsync"/> 的方法体要到首次取行才运行），
/// 完全由被选中的那个实现决定；门面要在开始枚举之前就拒绝非法选项的调用方得自己先判。
/// </para>
/// <para>
/// 流所有权仍在调用方：本类型不碰传入的流（连复位位置都不做，那是被选中那条路径的事），也不关闭它。
/// 两个实现都是无状态的读取器，可以并发共用同一个门面实例；注册侧把它们各自按单例提供，本门面只持有引用。
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
    /// 或列定义不成立（缺列定义、空集合、键为空或重复、宽度非正、编码是宽字节）。
    /// 抛出的具体那一格由被选中的实现决定，两条路径的异常面各自写在它们的 <see cref="ReadAsync"/> 说明里</exception>
    /// <exception cref="System.Text.DecoderFallbackException">文字档的实际字节在所用编码下解不开</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// 这些异常都在<u>首次取行</u>时才抛出：本方法只是选一条路径并把它交回的异步序列原样给出，
    /// 自己不做任何会抛的检查（构造门面时除外——缺任一个读实现就在构造时抛）。
    /// </remarks>
    public IAsyncEnumerable<ExcelImportRow> ReadAsync(
        Stream input,
        ExcelImportOptions? options = null,
        CancellationToken cancellationToken = default)
        => options?.FixedColumns is null
            ? _excelDataReaderImporter.ReadAsync(input, options, cancellationToken)
            : _fixedWidthTextImporter.ReadAsync(input, options, cancellationToken);
}
