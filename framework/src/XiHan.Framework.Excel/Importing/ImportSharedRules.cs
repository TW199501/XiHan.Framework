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
/// 容器路径与固定宽度路径共用这几条判据：<see cref="ExcelImportOptions.MaxRowCount"/> 的上界校验、
/// <see cref="XiHanExcelOptions.MaxImportBytes"/> 的档大小判定，以及
/// <see cref="ExcelImportOptions.SkipEmptyRows"/> 的「整行皆空」判定。
/// </para>
/// <para>
/// 行数上限有两层。<see cref="ExcelConstants.DefaultMaxImportRows"/> 是框架侧不可突破的绝对上界；
/// <see cref="XiHanExcelOptions.MaxImportRows"/> 是应用在这条界之内收紧的本次上限，
/// 由 <see cref="ResolveHardMaxRows"/> 校验并交回，两个导入器在构造时各取一次。
/// 请求的 <see cref="ExcelImportOptions.MaxRowCount"/> 只能落在本次上限之内，报错点名的也是本次上限。
/// </para>
/// <para>
/// 档大小上限只有一层：<see cref="XiHanExcelOptions.MaxImportBytes"/>，未接配置时取
/// <see cref="ExcelConstants.DefaultMaxImportBytes"/>。xlsx 的解压规模判定在容器导入器里。
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
    /// 在导入器构造点调用。越界一律抛出，不夹回上界。
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
    /// 判断撞到行数上限时该抛出还是该按上限截断
    /// </summary>
    /// <param name="requestedMaxRowCount">调用方给的上限，<c>null</c> 表示没指名</param>
    /// <param name="effectiveMaxRows">本次生效的上限，由 <see cref="ResolveMaxRowCount"/> 交回</param>
    /// <returns>没人指名过上限、且生效的正是框架硬上限时为 <c>true</c>（抛出）；否则为 <c>false</c>（截断）</returns>
    /// <remarks>
    /// <para>
    /// 「指名」包含调用端的 <see cref="ExcelImportOptions.MaxRowCount"/> 与配置端收紧过的
    /// <see cref="XiHanExcelOptions.MaxImportRows"/>。配置面的指名按「生效上限低于框架硬上限」认定，
    /// 配成与框架默认值相同的数视同未配置。
    /// </para>
    /// <para>
    /// 返回 <c>false</c> 时，导入器交够最后一行后立即停止读取。
    /// 返回 <c>true</c> 时，导入器继续读取至下一条非空数据行或档尾，以判定是否超过框架硬上限。
    /// </para>
    /// </remarks>
    internal static bool ThrowsWhenRowLimitHit(int? requestedMaxRowCount, int effectiveMaxRows)
        => requestedMaxRowCount is null && effectiveMaxRows == ExcelConstants.DefaultMaxImportRows;

    /// <summary>
    /// 造「撞上行数上限，且上限之后仍有数据行」的异常
    /// </summary>
    /// <param name="maxRows">本次生效的行数上限</param>
    /// <returns>点名下限值与成因的异常</returns>
    internal static InvalidOperationException RowLimitExceeded(int maxRows)
        => new(
            $"这份档的数据行超过本次导入的行数上限 {maxRows} 行：上限之后仍有数据行，导入在这里停下，" +
            "不把少了的行悄悄丢掉。本次没有人指名过上限，撞上的是框架侧的保护性硬上限 " +
            $"{nameof(ExcelConstants.DefaultMaxImportRows)} = {ExcelConstants.DefaultMaxImportRows} 行，" +
            "它不是请求的一部分，因此不按截断处理。要按上限截断请指名 " +
            $"{nameof(ExcelImportOptions.MaxRowCount)} 或 {nameof(XiHanExcelOptions.MaxImportRows)}" +
            "（指名之后撞到上限就停，不报错）；要读完整份档请在来源侧把它分批。");

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
    /// 本值不判越界，可由应用调高或调低。
    /// </remarks>
    internal static long ResolveMaxImportBytes(XiHanExcelOptions? options)
        => options?.MaxImportBytes ?? ExcelConstants.DefaultMaxImportBytes;

    /// <summary>
    /// 取本次生效的解压比上限（倍数）
    /// </summary>
    /// <param name="options">Excel 选项，传 <c>null</c> 表示不接配置、用框架默认上限</param>
    /// <returns>本次可用的解压比上限；<c>0</c> 或负数表示不判解压比</returns>
    /// <remarks>
    /// <c>&lt;= 0</c> 不夹回默认值。关掉解压比不影响
    /// <see cref="ExcelConstants.MaxImportEntryDecompressedBytes"/> 与
    /// <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 两道绝对上限。
    /// </remarks>
    internal static int ResolveMaxImportCompressionRatio(XiHanExcelOptions? options)
        => options?.MaxImportCompressionRatio ?? ExcelConstants.MaxImportCompressionRatio;

    /// <summary>
    /// 按本次生效的档大小上限判一份档，超限就拒收
    /// </summary>
    /// <param name="length">档的字节数，取自 <c>Stream.Length</c></param>
    /// <param name="maxImportBytes">本次生效的上限，由 <see cref="ResolveMaxImportBytes"/> 交回</param>
    /// <exception cref="InvalidOperationException">档的字节数超过上限</exception>
    /// <remarks>
    /// 判在读第一个字节之前。超限时拒收整份档，不做「读到上限为止」的截断。
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
