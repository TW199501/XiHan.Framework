// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Importing;

/// <summary>
/// 导入契约：把一份档逐行读成 <see cref="ExcelImportRow"/> 序列
/// </summary>
/// <remarks>
/// <para>
/// 读是惰性的：<see cref="ReadAsync"/> 返回 <see cref="IAsyncEnumerable{T}"/>，不物化整档，
/// 每取一行才解析一行，因此 <see cref="ExcelImportOptions.MaxRowCount"/> 与取消都能在行与行之间生效。
/// 枚举器是一次性的，同一份读取不要重复枚举。
/// </para>
/// <para>
/// 流所有权在调用方：实现<u>绝不关闭</u>传入的流，枚举结束后调用方仍可复位重读。
/// 读取一律从流的<u>起点</u>开始，实现会在建立读取器之前把流位置复位到 0（容器解析本来也回到起点），
/// 因此「从流中间接着读」不是本契约的能力。
/// </para>
/// <para>
/// 本契约只承诺读出源档里已有的东西：合并单元格除左上角外的其余格位读回 <c>null</c>（Excel 自身的存储形态），
/// 公式只读回已缓存的值，样式、批注、图表与宏一律不解释。
/// </para>
/// </remarks>
public interface IExcelImporter
{
    /// <summary>
    /// 逐行读入一份档
    /// </summary>
    /// <param name="input">输入流，必须可读且可定位；所有权在调用方，实现只读取不关闭</param>
    /// <param name="options">导入选项，为 <c>null</c> 时使用 <see cref="ExcelImportOptions"/> 的默认值</param>
    /// <param name="cancellationToken">取消令牌，取消时停止继续取行</param>
    /// <returns>逐行数据的异步序列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> 不可读或不可定位</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ExcelImportOptions.MaxRowCount"/> 高于框架硬上限或不是正整数</exception>
    /// <exception cref="InvalidOperationException">无法从档头判定格式，或
    /// <see cref="ExcelImportOptions.SheetName"/> 在本工作簿里不存在</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 入参检查在<u>首次取行</u>时才执行，而不是在调用本方法时：异步迭代器的方法体要等
    /// <c>MoveNextAsync</c> 才运行。门面若要在开始枚举之前就拒绝非法选项（例如按配置算出的上限），
    /// 需要自己先判，不能指望这里抛。
    /// </para>
    /// <para>
    /// 编码名无法解析（<see cref="ExcelImportOptions.TextEncodingName"/>）与文字档解码失败抛的异常由实现定义，
    /// 见各实现的文档；本契约不把它翻译成新类型。
    /// </para>
    /// </remarks>
    IAsyncEnumerable<ExcelImportRow> ReadAsync(
        Stream input,
        ExcelImportOptions? options = null,
        CancellationToken cancellationToken = default);
}
