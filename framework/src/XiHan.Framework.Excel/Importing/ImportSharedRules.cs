// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Importing;

namespace XiHan.Framework.Excel.Importing;

/// <summary>
/// 两条导入路径共用的行数上限收敛、档大小上限与「整行皆空」判定
/// </summary>
/// <remarks>
/// <para>
/// 这几条判据与来源无关：<see cref="ExcelImportOptions.MaxRowCount"/> 的上界校验在容器路径与固定宽度路径
/// 必须是同一个数、同一种报法，<see cref="XiHanExcelOptions.MaxImportBytes"/> 说的「这份档太大」也不能
/// 一个来源拒、另一个来源照读，<see cref="ExcelImportOptions.SkipEmptyRows"/> 说的「整行皆空」同样不能一个来源
/// 只看 <c>null</c>、另一个来源把空格也算空。抽在这里，两个导入器共用一份，不再各写一遍。
/// </para>
/// <para>
/// 行数上限有两层。<see cref="ExcelConstants.DefaultMaxImportRows"/> 是框架侧不可突破的绝对上界；
/// <see cref="XiHanExcelOptions.MaxImportRows"/> 是应用在这条界之内收紧的本次上限，
/// 由 <see cref="ResolveHardMaxRows"/> 校验并交回，两个导入器在构造时各取一次。
/// 请求的 <see cref="ExcelImportOptions.MaxRowCount"/> 只能落在本次上限之内，报出的也是本次上限——
/// 应用把上限配成 2 行却被告知「不能超过 1000000 行」等于把配置当成装饰。
/// </para>
/// <para>
/// 档大小上限只有一层：<see cref="XiHanExcelOptions.MaxImportBytes"/>，未接配置时取
/// <see cref="ExcelConstants.DefaultMaxImportBytes"/>。容器路径在这道界之外还要按 zip 元数据判解压规模，
/// 那部分只对 xlsx 有意义，因此留在容器导入器里，不在这里。
/// </para>
/// </remarks>
internal static class ImportSharedRules
{
    /// <summary>
    /// 取本次生效的导入行数硬上限
    /// </summary>
    /// <param name="options">Excel 选项，传 <c>null</c> 表示不接配置、用框架默认硬上限</param>
    /// <returns>本次可用的行数上限</returns>
    /// <exception cref="ArgumentOutOfRangeException">配置的上限不是正整数，或高过框架的绝对上界</exception>
    /// <remarks>
    /// 抛在构造点而不是等到取行：配置越界意味着这份读取器不可能按承诺工作，越早报越接近真正的成因
    /// （选项绑定的那一刻），也不会有人把「解析服务失败」当成档的问题。越界一律抛出，不夹回上界——
    /// 夹回等于把「配了 200 万行、实际得到 100 万行」这件事变成静默改写。
    /// </remarks>
    internal static int ResolveHardMaxRows(XiHanExcelOptions? options)
    {
        if (options is null)
        {
            return ExcelConstants.DefaultMaxImportRows;
        }

        var value = options.MaxImportRows;

        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(XiHanExcelOptions.MaxImportRows),
                value,
                $"{nameof(XiHanExcelOptions.MaxImportRows)} 必须是正整数：要按框架默认上限读就删掉这项配置，" +
                "不要写 0，0 不是「不限制」的另一种写法。");
        }

        if (value > ExcelConstants.DefaultMaxImportRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(XiHanExcelOptions.MaxImportRows),
                value,
                $"{nameof(XiHanExcelOptions.MaxImportRows)} 不能高于框架硬上限 {ExcelConstants.DefaultMaxImportRows} 行：" +
                "这道界挡住的是「把内存里的行数放大到无穷」，配置只能在界内收紧，不能放宽。");
        }

        return value;
    }

    /// <summary>
    /// 把单次导入的行数上限收敛到本次生效的上限之内
    /// </summary>
    /// <param name="requested">调用方给的上限，<c>null</c> 表示用本次上限</param>
    /// <param name="hardMaxRows">本次生效的上限，由 <see cref="ResolveHardMaxRows"/> 交回</param>
    /// <returns>本次实际可用的行数上限</returns>
    /// <exception cref="ArgumentOutOfRangeException">上限高于本次上限，或不是正整数</exception>
    internal static int ResolveMaxRowCount(int? requested, int hardMaxRows)
    {
        if (requested is null)
        {
            return hardMaxRows;
        }

        var value = requested.Value;

        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExcelImportOptions.MaxRowCount),
                value,
                "MaxRowCount 必须是正整数；要按框架默认上限读请传 null，不要写 0。");
        }

        if (value > hardMaxRows)
        {
            var cause = hardMaxRows == ExcelConstants.DefaultMaxImportRows
                ? $"MaxRowCount 不能高于框架硬上限 {ExcelConstants.DefaultMaxImportRows} 行：" +
                  "这道上限挡住的是「调用方把内存里的行数放大到无穷」，应用只能收紧、不能放宽。"
                : $"MaxRowCount 不能高于本次生效的行数上限 {hardMaxRows} 行：" +
                  $"该上限由 {nameof(XiHanExcelOptions.MaxImportRows)} 在框架硬上限之内收紧得到，" +
                  "单次请求只能继续往里收，不能越过它。";

            throw new ArgumentOutOfRangeException(
                nameof(ExcelImportOptions.MaxRowCount),
                value,
                cause);
        }

        return value;
    }

    /// <summary>
    /// 判断整行皆空
    /// </summary>
    /// <param name="values">本行取值</param>
    /// <remarks>
    /// 「空」只看 <c>null</c> 与空字串：<see cref="ExcelImportOptions.TrimValues"/> 为 <c>true</c> 时全空格行
    /// 已经剥成空字串，因此也算空行；为 <c>false</c> 时 <c>" "</c> 是实数据，不算空行。
    /// </remarks>
    internal static bool IsEmptyRow(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var value in values.Values)
        {
            if (value is not null && value is not "")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 取本次生效的导入档大小上限（字节）
    /// </summary>
    /// <param name="options">Excel 选项，传 <c>null</c> 表示不接配置、用框架默认上限</param>
    /// <returns>本次可用的档大小上限</returns>
    /// <remarks>
    /// 与行数上限不同，本值不在这里判越界：行数上限是框架承诺的取值域，只能收紧；
    /// 档大小上限是部署侧的取舍（本进程愿意为一份外来档读多少字节），两个方向都由应用自己定。
    /// </remarks>
    internal static long ResolveMaxImportBytes(XiHanExcelOptions? options)
        => options?.MaxImportBytes ?? ExcelConstants.DefaultMaxImportBytes;

    /// <summary>
    /// 按本次生效的档大小上限判一份档，超限就拒收
    /// </summary>
    /// <param name="length">档的字节数，取自 <c>Stream.Length</c></param>
    /// <param name="maxImportBytes">本次生效的上限，由 <see cref="ResolveMaxImportBytes"/> 交回</param>
    /// <exception cref="InvalidOperationException">档的字节数超过上限</exception>
    /// <remarks>
    /// 判在读第一个字节之前：超限的档连格式都不必判，也不该先付一遍解析成本再回头拒。
    /// 拒收的是<u>整份档</u>，不做「读到上限为止」的截断——那样交回的是一份看起来成功、
    /// 其实少了后半段的导入结果，调用方无从得知少了什么。
    /// </remarks>
    internal static void ValidateImportBytes(long length, long maxImportBytes)
    {
        if (length <= maxImportBytes)
        {
            return;
        }

        throw new InvalidOperationException(
            $"导入的档有 {length} 字节，超过本次生效的档大小上限 {maxImportBytes} 字节：整份档拒收，不读前面一段交回。" +
            $"上限取自 {nameof(XiHanExcelOptions.MaxImportBytes)}（框架默认 {ExcelConstants.DefaultMaxImportBytes} 字节），" +
            "挡的是「一份外来档决定本进程要读多少字节」；要读更大的档请调高该配置，或先在来源侧把档分批。");
    }
}
