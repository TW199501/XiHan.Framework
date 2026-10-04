// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using MiniExcelLibs;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 固定版式模板渲染器，用 MiniExcel 把数据填进现成的 xlsx 模板
/// </summary>
/// <remarks>
/// <para>
/// 适用场景是版面已经画在模板里的单据：抬头、 logo、合并格、公式、打印区域都原样保留，本类只把占位符换成值。
/// 占位符写成 <c>{{键名}}</c>，集合写成 <c>{{键名.子键名}}</c>；两者都区分大小写，也不接受花括号内侧带空格的写法
/// ——<c>{{ Company }}</c> 解析不到值，键名以 <c>Company</c> 给出才成立。
/// </para>
/// <para>
/// 集合占位从它所在的那一行原地起写：第一项落在占位行本身，后续项依次往下占行，模板里原本在它下方的行整体下移。
/// 同一行的多个集合占位（如 <c>{{Items.Name}}</c> 与 <c>{{Items.Qty}}</c>）按同一项并行展开。
/// </para>
/// <para>
/// 输入检查排在调用渲染库之前：三个入参为 <c>null</c> 时交回 <see cref="ArgumentNullException"/>——渲染库对
/// null 模板与 null 数据抛出的是 <c>NullReferenceException</c>，那不构成可依赖的契约，必须由本类前移；
/// 模板不可读、不可定位或内容为空时交回 <see cref="ArgumentException"/>，其中内容为空的消息固定含「模板内容为空」。
/// </para>
/// <para>
/// 渲染库要求模板可定位（<see cref="Stream.CanSeek"/>），不可定位时它自己抛英文 <see cref="ArgumentException"/>；
/// 本类把这条要求前移成带中文说明的 <see cref="ArgumentException"/>，不代为拷进内存——拷一份等于把模板规模变成
/// 固定的内存开销，是否要这种代价应由调用方决定。
/// </para>
/// <para>
/// 模板档不是 xlsx 容器（随便一段字节、被截断的档、缺工作簿部件）时，容器异常原样透传，本类不做二次解析；
/// 这种失败可能已经往输出流写过部分字节，调用方必须丢弃输出内容。空模板与只读不可定位的模板都在入口就拦下，
/// 因此那两种失败输出流零字节。
/// </para>
/// <para>
/// 输出流的所有权在调用方：本类绝不对它调用 <c>Dispose</c>，渲染后位置停在末尾，把位置回到 0 即可读回。
/// 模板流则相反——渲染库读完就把它关掉，所以一份模板流只能渲染一次，重复渲染要每次交回一份新流。
/// </para>
/// <para>
/// 取消令牌在本类查两次：调用渲染库之前一次（此时输出流零字节），渲染返回之后一次。渲染库自己只在动手前查，
/// 取消落在写出的中途或最后一段时没人再查，本类补上后一次，为的是不交出「渲染完成」这个假象；
/// 抛出时输出流里可能已经落了内容、甚至已是一份完整的档，调用方必须丢弃它，本类不承诺失败原子性。
/// </para>
/// <para>
/// 值类型按渲染库自己的形态落格：数值仍是数值格，日期落成文本格（与 <see cref="ClosedXmlExporter"/> 的日期格不同），
/// <c>null</c> 值写空。不承诺宏、数据透视表与图表，也不写 <c>.xls</c>。
/// </para>
/// </remarks>
public sealed class MiniExcelTemplateRenderer : IExcelTemplateRenderer
{
    /// <summary>
    /// 按模板渲染一份档，把占位符换成数据里的值
    /// </summary>
    /// <param name="output">输出流，本方法只写入不关闭，由调用方拥有；渲染后流位置停在末尾</param>
    /// <param name="template">模板流，必须是可定位的 xlsx 容器；渲染后由库关闭，不要复用该流</param>
    /// <param name="data">填进模板的数据，按模板里的占位符键取值，允许匿名类型、具名类型或字典</param>
    /// <param name="cancellationToken">取消令牌，取消时不再开始渲染；渲染已经动手才被观察到的取消同样抛出</param>
    /// <returns>异步任务</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/>、<paramref name="template"/> 或
    /// <paramref name="data"/> 为 <c>null</c>，<see cref="ArgumentException.ParamName"/> 分别取参数名</exception>
    /// <exception cref="ArgumentException"><paramref name="template"/> 不可读、不可定位或内容为空（消息含
    /// 「模板内容为空」），三者 <see cref="ArgumentException.ParamName"/> 均为 <c>template</c>；这类失败在调用渲染库之前抛出，
    /// 输出流零字节</exception>
    /// <exception cref="InvalidDataException">模板档存在但不是可用的 xlsx 容器，由渲染库抛出并原样透传；
    /// 此时输出流可能已含部分字节</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消。取消落在动手之前时
    /// 输出流零字节；落在写出的中途或最后一段时，输出流可能已有内容、甚至已是一份完整的档，但本方法交出的是异常，
    /// 不是「渲染完成」——调用方必须丢弃该流的内容，不承诺失败原子性</exception>
    public async Task RenderAsync(
        Stream output,
        Stream template,
        object data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(data);

        cancellationToken.ThrowIfCancellationRequested();

        if (!template.CanRead)
        {
            throw new ArgumentException(
                "模板流不可读（已被关闭或不允许读取）。渲染一次就消耗一份模板流，需要重复渲染请每次交回新的模板流。",
                nameof(template));
        }

        if (!template.CanSeek)
        {
            throw new ArgumentException(
                "模板流必须可定位（CanSeek 为 true）：渲染库要在模板内部前后跳转读部件，只进不退的流读不出版面。" +
                "请用 MemoryStream 或可随机访问的文件流传入。",
                nameof(template));
        }

        if (template.Length == 0)
        {
            throw new ArgumentException(
                "模板内容为空：模板流一个字节都没有，渲染只会交回一份没有版面的档。" +
                "请先给出写好占位符的 xlsx 模板，或在拿不到模板时不要调用渲染。",
                nameof(template));
        }

        await MiniExcel.SaveAsByTemplateAsync(output, template, data, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // 回传结果之前再查一次：渲染库只在动手前查令牌，取消落在最后一段写出期间时没人再查，
        // 本方法若就此返回，调用方读到的就是「渲染完成」，而产出的那份档未必收得下整个渲染
        cancellationToken.ThrowIfCancellationRequested();
    }
}
