// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 固定版式模板渲染契约：按现成的 Excel 模板档填数据，产出保留版面的新档
/// </summary>
/// <remarks>
/// <para>
/// 模板渲染的版面由模板档决定（合并格、公式、打印区域、单元格样式），框架只把占位符换成值，
/// 不重新安排任何一格，也不接受列清单。
/// </para>
/// <para>
/// <see cref="RenderAsync(Stream, Stream, object, CancellationToken)" /> 接受 <see cref="object"/> 数据入参：模板里的占位符是字符串键
/// （单值写 <c>{{Company}}</c>，集合写 <c>{{Items.Name}}</c>），实现按名字取值；调用方交回匿名类型、具名类型或字典都可以。
/// </para>
/// <para>
/// 缺键不报错：数据里没有对应键时，实现把那一格留空，不猜值、不保留占位符原文，也不因此中断渲染。
/// </para>
/// <para>
/// <b>写出前校验：</b>实现在把数据交给渲染库之前走访一遍，命中下列判据的值一律拒写，不加前缀、不截断、不改写、
/// 不跳过，抛出时消息点名数据里的键路径（形如 <c>Items[3].Name</c>，集合元素的下标从 0 起），且输出流一个字节都没有：
/// <list type="bullet">
/// <item>公式防护——字串值以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符或回车起首，或以渲染库的
/// 公式指令前缀 <c>$=</c> 起首。</item>
/// <item>取值域——早于 1900-01-01 的 <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、
/// <c>NaN</c> 或 <c>±∞</c>、有效数字多于
/// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的 <c>long</c>／<c>ulong</c>／
/// <c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992（2 的 53 次方）的
/// <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过 <see cref="ExcelConstants.MaxCellTextLength"/>
/// 的字串。判据与两条 xlsx 导出路径相同，成因句逐字相同，点名位置换成键路径。</item>
/// <item>XML 1.0 非法字符——字串里含 <c>U+0000</c>–<c>U+0008</c>、<c>U+000B</c>、<c>U+000C</c> 或
/// <c>U+000E</c>–<c>U+001F</c> 中的任一个。只有模板路径拒这一类；制表符、换行与回车不在其中。</item>
/// </list>
/// </para>
/// <para>
/// 走访范围是数据的顶层成员与集合元素成员，深度与 <c>{{键}}</c>／<c>{{键.子键}}</c> 的可达深度一致；
/// 模板没有引用的键同样在校验范围内。走访只判取值，缺键照样留空。
/// </para>
/// </remarks>
public interface IExcelTemplateRenderer
{
    /// <summary>
    /// 按模板渲染一份档，把占位符换成数据里的值
    /// </summary>
    /// <param name="output">输出流，实现只写入不关闭，由调用方拥有；渲染后流位置停在末尾</param>
    /// <param name="template">模板流，必须是可重复定位的 xlsx 容器；所有权在实现侧，渲染后不要复用该流</param>
    /// <param name="data">填进模板的数据，按模板里的占位符键取值；写出之前会被走访一遍做取值检查，
    /// 调用方不得在校验与写出之间修改它</param>
    /// <param name="cancellationToken">取消令牌，取消时不再开始渲染；渲染已经动手才被观察到的取消同样抛出</param>
    /// <returns>异步任务</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output"/>、<paramref name="template"/> 或 <paramref name="data"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="template"/> 不可读、不可定位，或内容为空（模板档一个字节都没有）；
    /// <see cref="ArgumentException.ParamName"/> 为 <c>template</c>（内容为空时消息含「模板内容为空」）。
    /// 或 <paramref name="data"/> 里某个字串值命中公式防护（以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、
    /// 制表符、回车或 <c>$=</c> 起首），<see cref="ArgumentException.ParamName"/> 为 <c>data</c>，
    /// 消息点名键路径；输出流零字节</exception>
    /// <exception cref="InvalidOperationException"><paramref name="data"/> 里某个取值是工作簿装不下的
    /// （早于 1900-01-01 的 <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、<c>NaN</c> 或 <c>±∞</c>、
    /// 有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的 <c>long</c>／<c>ulong</c>／
    /// <c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992（2 的 53 次方）的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过 <see cref="ExcelConstants.MaxCellTextLength"/> 的字串），
    /// 或某个字串里含 XML 1.0 不允许出现在文本内容里的字符（<c>U+0000</c>–<c>U+0008</c>、<c>U+000B</c>、
    /// <c>U+000C</c>、<c>U+000E</c>–<c>U+001F</c>；制表符、换行与回车不在其中），
    /// 消息点名键路径并给出成因；输出流零字节</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消。取消落在动手之前时
    /// 输出流零字节；落在写出的中途或最后一段时输出流可能已有内容，实现交出异常而不是「渲染完成」，
    /// 调用方必须丢弃该流的内容</exception>
    /// <remarks>
    /// <para>
    /// 输入检查与数据侧的写出前校验全部排在调用渲染库之前，抛出时输出流零字节。
    /// </para>
    /// <para>
    /// 模板档本身不是 xlsx 容器时，容器异常原样透传；这类失败可能已经往输出流写过部分字节，调用方须丢弃输出流内容。
    /// </para>
    /// <para>
    /// 写出前校验只覆盖调用那一刻交回的数据，走访结束后到写出之间数据被改不在契约覆盖范围内。
    /// 集合成员会被完整枚举一遍且排在渲染库自己的枚举之前，只允许枚举一次的数据源不可用，惰性数据源会在这里提前求值。
    /// </para>
    /// </remarks>
    Task RenderAsync(
        Stream output,
        Stream template,
        object data,
        CancellationToken cancellationToken = default);
}
