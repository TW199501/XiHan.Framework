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
/// </remarks>
public interface IExcelTemplateRenderer
{
    /// <summary>
    /// 按模板渲染一份档，把占位符换成数据里的值
    /// </summary>
    /// <param name="output">输出流，实现只写入不关闭，由调用方拥有；渲染后流位置停在末尾</param>
    /// <param name="template">模板流，必须是可重复定位的 xlsx 容器；所有权在实现侧，渲染后不要复用该流</param>
    /// <param name="data">填进模板的数据，按模板里的占位符键取值</param>
    /// <param name="cancellationToken">取消令牌，取消时不再开始渲染；渲染已经动手才被观察到的取消同样抛出</param>
    /// <returns>异步任务</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output"/>、<paramref name="template"/> 或 <paramref name="data"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="template"/> 不可读、不可定位，或内容为空（模板档一个字节都没有）；
    /// <see cref="ArgumentException.ParamName"/> 为 <c>template</c>（内容为空时消息含「模板内容为空」）</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消。取消落在动手之前时
    /// 输出流零字节；落在写出的中途或最后一段时输出流可能已有内容，实现交出异常而不是「渲染完成」，
    /// 调用方必须丢弃该流的内容</exception>
    /// <remarks>
    /// <para>
    /// 输入检查全部排在调用渲染库之前：模板不可读、不可定位、内容为空这三类都由本契约的异常形态定义，
    /// 渲染库自身的容器异常不会被当成契约。
    /// </para>
    /// <para>
    /// 模板档本身不是 xlsx 容器时（例如随便一段字节或被截断的档），实现不做二次解析，容器异常原样透传，
    /// 调用方按「模板不可用」处理并丢弃输出流内容——这类失败可能已经往输出流写过部分字节。
    /// </para>
    /// </remarks>
    Task RenderAsync(
        Stream output,
        Stream template,
        object data,
        CancellationToken cancellationToken = default);
}
