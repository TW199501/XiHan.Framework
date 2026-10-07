// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 固定版式模板渲染契约：按现成的 Excel 模板档填数据，产出保留版面的新档
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ExcelSheetSpec"/> 那条路径的分工是「版面归谁」：工作簿导出器的版面由列清单与排版开关描述，
/// 由框架算出格位；模板渲染的版面画在模板档里（合并格、公式、打印区域、单元格样式都在里面），
/// 框架只把占位符换成值，不重新安排任何一格。因此模板渲染不接受列清单，也不承诺改写版式。
/// </para>
/// <para>
/// <see cref="RenderAsync(Stream, Stream, object, CancellationToken)" /> 是本组件里唯一接受 <see cref="object"/> 数据入参的入口：模板里的占位符是字符串键
/// （单值写 <c>{{Company}}</c>，集合写 <c>{{Items.Name}}</c>），键到值的对应关系由模板决定，而不是由 CLR 型别决定。
/// 用泛型参数约束在这里既约束不住任何东西（任何型别都可能正好缺一个键或多一个键），也会把「版面在 Excel 里」这件事
/// 伪装成「版面在类型里」。调用方交回匿名类型、具名类型或字典都可以，实现按名字取值。
/// </para>
/// <para>
/// 缺键不报错：数据里没有对应键时，实现把那一格留空，不猜值、不保留占位符原文，也不因此中断渲染。
/// </para>
/// <para>
/// <b>写出前校验：</b>实现在把数据交给渲染库之前走访一遍，逐值套用与两条 xlsx 导出路径同一份的判据。
/// 命中的值一律拒写，不加前缀、不截断、不改写、不跳过，抛出时消息点名数据里的键路径（形如
/// <c>Items[3].Name</c>，集合元素的下标从 0 起），且输出流一个字节都没有。拒写的取值分三类：
/// <list type="bullet">
/// <item>公式防护——字串值以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符或回车起首，或以渲染库自己的
/// 公式指令前缀 <c>$=</c> 起首；后者会被渲染库整段改写成公式写进格子，前六种由表格软件在读取时解释，
/// 其中制表符与回车还会被渲染库静默吃掉。</item>
/// <item>取值域——早于 1900-01-01 的 <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、
/// <c>NaN</c> 或 <c>±∞</c>、有效数字多于
/// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的 <c>long</c>／<c>ulong</c>／
/// <c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992（2 的 53 次方）的
/// <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过 <see cref="ExcelConstants.MaxCellTextLength"/>
/// 的字串。这几类与两条 xlsx 导出路径判得一样，成因句逐字相同，只有点名位置从行列换成键路径。</item>
/// <item>XML 1.0 非法字符——字串里含 <c>U+0000</c>–<c>U+0008</c>、<c>U+000B</c>、<c>U+000C</c> 或
/// <c>U+000E</c>–<c>U+001F</c> 中的任一个。这一类<u>只有模板路径拒</u>：模板把这类字符落成 XML 字符实体，
/// 而那份实体本身就不是合法的 XML 1.0 文本，产出的档任何读取器都打不开；两条 xlsx 导出路径把同一个字符
/// 转义成 <c>_xHHHH_</c>，档能开、值能逐字读回，因此那边照写。制表符、换行与回车是 XML 1.0 合法的文本内容，
/// 不在这一类里，多行单元格照写——回车被 XML 解析归一成换行是行尾处理，不是拒写。</item>
/// </list>
/// </para>
/// <para>
/// 判据相同，处置形态不同：分隔符文字导出对命中公式防护的字段加单引号前缀，模板路径拒写。
/// 加前缀等于改写业务数据，而模板没有留痕机制能交代改了什么，因此这边宁可多拒不静默改值。
/// </para>
/// <para>
/// 走访范围是数据的顶層成员与集合元素成员，深度与 <c>{{键}}</c>／<c>{{键.子键}}</c> 的可达深度一致；
/// 走访不解析模板，因此模板没有引用的键同样在校验范围内。校验发生在写出之前，
/// 调用方不得在校验与写出之间修改数据：两边看到的不是同一份数据时，本契约不作任何保证。
/// 走访只判取值，缺键照样留空，上面那条「缺键不报错」不受影响。
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
    /// 消息点名键路径；这类失败同样排在调用渲染库之前，输出流零字节</exception>
    /// <exception cref="InvalidOperationException"><paramref name="data"/> 里某个取值是工作簿装不下的
    /// （早于 1900-01-01 的 <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、<c>NaN</c> 或 <c>±∞</c>、
    /// 有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的 <c>long</c>／<c>ulong</c>／
    /// <c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992（2 的 53 次方）的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过 <see cref="ExcelConstants.MaxCellTextLength"/> 的字串），
    /// 或某个字串里含 XML 1.0 不允许出现在文本内容里的字符（<c>U+0000</c>–<c>U+0008</c>、<c>U+000B</c>、
    /// <c>U+000C</c>、<c>U+000E</c>–<c>U+001F</c>；制表符、换行与回车不在其中，这一类只有模板路径拒），
    /// 消息点名键路径并给出成因；这类失败排在调用渲染库之前，输出流零字节</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消。取消落在动手之前时
    /// 输出流零字节；落在写出的中途或最后一段时输出流可能已有内容，实现交出异常而不是「渲染完成」，
    /// 调用方必须丢弃该流的内容</exception>
    /// <remarks>
    /// <para>
    /// 输入检查全部排在调用渲染库之前：模板不可读、不可定位、内容为空这三类都由本契约的异常形态定义，
    /// 渲染库自身的容器异常不会被当成契约。数据侧的写出前校验也排在同一位置，因此它抛出时输出流零字节。
    /// </para>
    /// <para>
    /// 模板档本身不是 xlsx 容器时（例如随便一段字节或被截断的档），实现不做二次解析，容器异常原样透传，
    /// 调用方按「模板不可用」处理并丢弃输出流内容——这类失败可能已经往输出流写过部分字节。
    /// </para>
    /// <para>
    /// 写出前校验是<b>校验时点</b>的保证，不是全程保证：实现走访的是调用那一刻交回的数据，走访结束后到渲染库
    /// 写出之间数据被改（就地改字串、往集合里加项、换掉某个成员）不在契约覆盖范围内。调用方要么交回一份
    /// 不可变的数据，要么保证这段时间不去动它。集合成员会被走访完整枚举一遍，且排在渲染库自己的枚举之前，
    /// 因此只允许枚举一次的数据源用不了，惰性数据源会在这里提前求值。
    /// </para>
    /// </remarks>
    Task RenderAsync(
        Stream output,
        Stream template,
        object data,
        CancellationToken cancellationToken = default);
}
