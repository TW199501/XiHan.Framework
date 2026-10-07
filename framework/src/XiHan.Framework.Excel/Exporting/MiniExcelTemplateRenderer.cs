// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;
using System.Reflection;
using MiniExcelLibs;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Text;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 固定版式模板渲染器，用 MiniExcel 把数据填进现成的 xlsx 模板
/// </summary>
/// <remarks>
/// <para>
/// 版面画在模板里：抬头、logo、合并格、公式、打印区域都原样保留，本类只把占位符换成值。
/// 占位符写成 <c>{{键名}}</c>，集合写成 <c>{{键名.子键名}}</c>；两者都区分大小写，也不接受花括号内侧带空格的写法
/// ——<c>{{ Company }}</c> 解析不到值，键名以 <c>Company</c> 给出才成立。
/// </para>
/// <para>
/// 集合占位从它所在的那一行原地起写：第一项落在占位行本身，后续项依次往下占行，模板里原本在它下方的行整体下移。
/// 同一行的多个集合占位（如 <c>{{Items.Name}}</c> 与 <c>{{Items.Qty}}</c>）按同一项并行展开。
/// </para>
/// <para>
/// 三个入参为 <c>null</c> 时抛 <see cref="ArgumentNullException"/>；模板不可读、不可定位或内容为空时抛
/// <see cref="ArgumentException"/>，其中内容为空的消息固定含「模板内容为空」。这些检查排在调用渲染库之前，
/// 不可定位的模板不会被拷进内存。
/// </para>
/// <para>
/// 模板档不是 xlsx 容器（随便一段字节、被截断的档、缺工作簿部件）时，容器异常原样透传；
/// 此时输出流可能已含部分字节，调用方必须丢弃输出内容。
/// </para>
/// <para>
/// 输出流的所有权在调用方：本类不对它调用 <c>Dispose</c>，渲染后位置停在末尾。
/// 模板流由渲染库读完后关闭，一份模板流只能渲染一次。
/// </para>
/// <para>
/// 取消令牌在调用渲染库之前（此时输出流零字节）与渲染返回之后各查一次。后一次抛出时输出流里可能已有内容、
/// 甚至已是一份完整的档，调用方必须丢弃它。
/// </para>
/// <para>
/// 数据在交给渲染库之前先被走访一遍：字串值以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符或回车起首，
/// 或以渲染库的公式指令前缀 <c>$=</c> 起首时抛 <see cref="ArgumentException"/>
/// （<see cref="ArgumentException.ParamName"/> 为 <c>data</c>）；取值是工作簿装不下的那一类时抛
/// <see cref="InvalidOperationException"/>。两者都点名键路径、都抛在写出第一个字节之前。
/// 命中公式判据的值被拒写，不加单引号前缀。
/// </para>
/// <para>
/// 字串含 XML 1.0 不允许出现在文本内容里的字符（<c>U+0000</c>–<c>U+0008</c>、<c>U+000B</c>、<c>U+000C</c>、
/// <c>U+000E</c>–<c>U+001F</c>）时同样拒写；制表符、换行与回车不在此列。
/// </para>
/// <para>
/// 校验只保证走访那一刻，调用方不得在走访与写出之间修改数据。走访会把集合成员完整枚举一遍，
/// 只允许枚举一次的数据源不可用，惰性数据源会提前求值。走访不解析模板，模板没有引用的键同样在校验范围内；
/// 缺键留空、不报错。
/// </para>
/// <para>
/// 数值落数值格，日期落文本格（与 <see cref="ClosedXmlExporter"/> 不同），<c>null</c> 值写空。
/// 不支持宏、数据透视表与图表，也不写 <c>.xls</c>。
/// </para>
/// </remarks>
public sealed class MiniExcelTemplateRenderer : IExcelTemplateRenderer
{
    /// <summary>
    /// 按模板渲染一份档，把占位符换成数据里的值
    /// </summary>
    /// <param name="output">输出流，本方法只写入不关闭，由调用方拥有；渲染后流位置停在末尾</param>
    /// <param name="template">模板流，必须是可定位的 xlsx 容器；渲染后由库关闭，不要复用该流</param>
    /// <param name="data">填进模板的数据，按模板里的占位符键取值，允许匿名类型、具名类型或字典；
    /// 写出之前会被走访一遍做取值检查，调用方不得在校验与写出之间修改它</param>
    /// <param name="cancellationToken">取消令牌，取消时不再开始渲染；渲染已经动手才被观察到的取消同样抛出</param>
    /// <returns>异步任务</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/>、<paramref name="template"/> 或
    /// <paramref name="data"/> 为 <c>null</c>，<see cref="ArgumentException.ParamName"/> 分别取参数名</exception>
    /// <exception cref="ArgumentException"><paramref name="template"/> 不可读、不可定位或内容为空（消息含
    /// 「模板内容为空」），三者 <see cref="ArgumentException.ParamName"/> 均为 <c>template</c>；这类失败在调用渲染库之前抛出，
    /// 输出流零字节。或 <paramref name="data"/> 里某个字串值命中公式防护（以 <c>=</c>、<c>+</c>、<c>-</c>、
    /// <c>@</c>、制表符、回车或 <c>$=</c> 起首），
    /// <see cref="ArgumentException.ParamName"/> 为 <c>data</c>，消息点名键路径；同样抛在调用渲染库之前，
    /// 输出流零字节</exception>
    /// <exception cref="InvalidOperationException"><paramref name="data"/> 里某个取值是工作簿装不下的
    /// （早于 1900-01-01 的 <see cref="DateTime"/>、
    /// <see cref="DateOnly"/> 或 <see cref="DateTimeOffset"/>、非有限的浮点数、有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值、绝对值超过双精度整数上限的
    /// <see cref="long"/>／<see cref="ulong"/>／<see cref="decimal"/>，长于
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 的字串，或字串里含 XML 1.0 不允许出现在文本内容里的字符
    /// ——<c>U+0000</c>–<c>U+0008</c>、<c>U+000B</c>、<c>U+000C</c>、<c>U+000E</c>–<c>U+001F</c>，
    /// 制表符、换行与回车不在其中），消息点名键路径并给出成因；前几类的成因句与两条 xlsx 导出路径逐字相同，
    /// 最后一类只有本路径会拒。抛在调用渲染库之前，输出流零字节</exception>
    /// <exception cref="InvalidDataException">模板档存在但不是可用的 xlsx 容器，由渲染库抛出并原样透传；
    /// 此时输出流可能已含部分字节</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消。取消落在动手之前时
    /// 输出流零字节；落在写出期间时输出流可能已有内容、甚至已是一份完整的档，调用方必须丢弃该流的内容</exception>
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

        // 写出前走访数据，命中判据即拒写
        EnsureWritableData(data, cancellationToken);

        await MiniExcel.SaveAsByTemplateAsync(output, template, data, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // 渲染返回之后再检查一次取消
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 写出前走访数据的深度预算：根对象一层、集合一层、集合元素再一层，
    /// 与模板占位符 <c>{{键}}</c> 和 <c>{{键.子键}}</c> 能点到的深度一致
    /// </summary>
    /// <remarks>
    /// 自引用的对象图同样受此深度限制。
    /// </remarks>
    private const int MaximumWalkDepth = 3;

    /// <summary>
    /// 取值检查抛出时使用的参数名
    /// </summary>
    private const string DataParameterName = "data";

    /// <summary>
    /// 渲染库自己的公式指令前缀：值以它起首时，渲染库把余下整段当公式写进格子，而不是当文字
    /// </summary>
    private const string TemplateFormulaDirective = "$=";

    /// <summary>
    /// 走访数据，逐值套用与两条 xlsx 导出路径同一份的公式注入判据与取值域判据
    /// </summary>
    /// <param name="data">调用方交回的渲染数据，本方法只读不改</param>
    /// <param name="cancellationToken">取消令牌，每走访一个字典项或集合元素之前查一次</param>
    /// <remarks>
    /// <para>
    /// 走访范围是顶层成员与 <see cref="IEnumerable"/> 元素成员，按成员名取值，属性与字段都走访。
    /// </para>
    /// <para>
    /// 走访不看模板，覆盖数据的全部顶层成员与集合元素成员；成员取值器抛出的异常原样透传。
    /// 型别到成员的映射在本次走访内缓存。
    /// </para>
    /// <para>
    /// 只拆「按键名取值」的容器，见 <see cref="IsKeyContainer"/>：日期、时距、<see cref="Guid"/>、
    /// <see cref="decimal"/> 与各种数值型别都当整份值判定，不按成员名往下拆。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">某个字串值以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符、回车起首，
    /// 或以 <see cref="TemplateFormulaDirective"/> 起首，消息点名键路径，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>data</c></exception>
    /// <exception cref="InvalidOperationException">某个取值落不进工作簿，消息点名键路径并给出成因</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消，此时输出流零字节</exception>
    private static void EnsureWritableData(object data, CancellationToken cancellationToken)
        => VisitNode(data, string.Empty, MaximumWalkDepth, cancellationToken, new Dictionary<Type, MemberInfo[]>());

    /// <summary>
    /// 走访一个节点：字串与取值域越界的值就地判定，字典、集合与具名对象按剩余深度往下拆
    /// </summary>
    /// <param name="node">当前节点，<c>null</c> 表示这一格留空，直接放过</param>
    /// <param name="path">当前节点的键路径，形如 <c>Items[3].Name</c>；根节点是空字串</param>
    /// <param name="depthRemaining">还能往下拆几层，见 <see cref="MaximumWalkDepth"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="memberCache">本次走访内的型别到成员映射缓存</param>
    private static void VisitNode(
        object? node,
        string path,
        int depthRemaining,
        CancellationToken cancellationToken,
        IDictionary<Type, MemberInfo[]> memberCache)
    {
        if (node is null)
        {
            return;
        }

        // 字串同时是 IEnumerable，先于集合分支判定
        if (node is string text)
        {
            EnsureWritableValue(text, path);
            return;
        }

        if (depthRemaining > 0)
        {
            if (node is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    VisitNode(
                        entry.Value,
                        BuildMemberPath(path, entry.Key?.ToString() ?? string.Empty),
                        depthRemaining - 1,
                        cancellationToken,
                        memberCache);
                }

                return;
            }

            if (node is IEnumerable enumerable)
            {
                var index = 0;

                foreach (var element in enumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    VisitNode(element, $"{path}[{index}]", depthRemaining - 1, cancellationToken, memberCache);

                    index++;
                }

                return;
            }

            var type = node.GetType();

            if (IsKeyContainer(type))
            {
                foreach (var member in GetMembers(type, memberCache))
                {
                    VisitNode(
                        ReadMember(member, node),
                        BuildMemberPath(path, member.Name),
                        depthRemaining - 1,
                        cancellationToken,
                        memberCache);
                }

                return;
            }
        }

        // 深度用尽或型别不按键名再拆时，整份值判定
        EnsureWritableValue(node, path);
    }

    /// <summary>
    /// 判断一个型别是不是「按键名取值」的容器：只有调用方自己的数据类型算，框架与执行期的值型别不算
    /// </summary>
    /// <param name="type">节点的运行期型别</param>
    /// <returns>应当继续按成员名往下拆时为 <c>true</c></returns>
    /// <remarks>
    /// 字典、集合与调用方自己的数据类型按键名取值。<c>System</c> 与其子命名空间下的型别当值处理；
    /// 匿名类型没有命名空间，归为容器。
    /// </remarks>
    private static bool IsKeyContainer(Type type)
    {
        var namespaceName = type.Namespace;

        return namespaceName is null
            || !(namespaceName.Equals("System", StringComparison.Ordinal)
                || namespaceName.StartsWith("System.", StringComparison.Ordinal));
    }

    /// <summary>
    /// 判定一个值能不能写进模板：先套两道公式判据，再套与两条 xlsx 导出路径同一份的取值域判据
    /// </summary>
    /// <param name="value">刚走访到的值，非 <c>null</c></param>
    /// <param name="path">这个值的键路径，写进消息用</param>
    /// <remarks>
    /// 起首字符判据复用 <see cref="TextWriterHelper.NeedsFormulaEscape"/>，另判渲染库的公式指令前缀
    /// <see cref="TemplateFormulaDirective"/>，命中即抛 <see cref="ArgumentException"/>。
    /// 取值域使用 <see cref="ExcelWorkbookWriteGuard.DescribeTemplateUnwritable"/>，越界抛
    /// <see cref="InvalidOperationException"/>，消息保留成因并以键路径定位数据。
    /// </remarks>
    private static void EnsureWritableValue(object value, string path)
    {
        if (value is string text && NeedsFormulaRejection(text, out var hit))
        {
            throw new ArgumentException(
                $"模板渲染无法完成：数据里键路径「{DisplayPath(path)}」的字串值以「{hit}」起首，命中本组件的公式防护。" +
                "模板路径对命中值一律拒写、不加单引号前缀——加前缀等于改写业务数据，而模板没有留痕机制能交代改了什么；" +
                "分隔符文字导出对同一判据的处置是加前缀，判据只有一份，处置形态按路径而定。" +
                "请由呼叫端改写这个值再渲染。",
                DataParameterName);
        }

        if (ExcelWorkbookWriteGuard.DescribeTemplateUnwritable(value) is { } reason)
        {
            throw new InvalidOperationException(
                $"模板渲染无法完成：数据里键路径「{DisplayPath(path)}」的取值写不进工作簿。{reason}");
        }
    }

    /// <summary>
    /// 判断一个字串值是否命中模板路径的公式防护，命中时交回写进消息的起首形态
    /// </summary>
    /// <param name="text">刚走访到的字串值</param>
    /// <param name="hit">命中时是消息里用来点名起首的那一段文字；未命中时是 <c>null</c></param>
    /// <returns>命中任一道公式判据时为 <c>true</c></returns>
    private static bool NeedsFormulaRejection(string text, out string? hit)
    {
        if (text.StartsWith(TemplateFormulaDirective, StringComparison.Ordinal))
        {
            hit = TemplateFormulaDirective;
            return true;
        }

        if (TextWriterHelper.NeedsFormulaEscape(text))
        {
            hit = DescribeLeadingCharacter(text[0]);
            return true;
        }

        hit = null;

        return false;
    }

    /// <summary>
    /// 取一个型别的可走访成员：公开实例属性与公开实例字段，属性优先，同名只留一份
    /// </summary>
    /// <param name="type">节点型别</param>
    /// <param name="cache">本次走访内的缓存，就地读写</param>
    /// <remarks>
    /// 索引器不算成员。
    /// </remarks>
    private static MemberInfo[] GetMembers(Type type, IDictionary<Type, MemberInfo[]> cache)
    {
        if (cache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<MemberInfo>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && names.Add(property.Name))
            {
                members.Add(property);
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (names.Add(field.Name))
            {
                members.Add(field);
            }
        }

        var resolved = members.ToArray();

        cache[type] = resolved;

        return resolved;
    }

    /// <summary>
    /// 读一个成员的值，属性与字段同一口径
    /// </summary>
    private static object? ReadMember(MemberInfo member, object instance)
        => member is PropertyInfo property
            ? property.GetValue(instance)
            : ((FieldInfo)member).GetValue(instance);

    /// <summary>
    /// 拼出子成员的键路径，根节点（空字串）之下不带前导点号
    /// </summary>
    private static string BuildMemberPath(string parent, string name)
        => parent.Length == 0 ? name : string.Concat(parent, ".", name);

    /// <summary>
    /// 键路径写进消息的形态：根节点自己没有键名，用固定文案代替空的引号
    /// </summary>
    private static string DisplayPath(string path)
        => path.Length == 0 ? "(数据本身)" : path;

    /// <summary>
    /// 描述起首字符，让制表符与回车这两个控制字符在消息里也可读
    /// </summary>
    private static string DescribeLeadingCharacter(char character)
        => char.IsControl(character)
            ? $"U+{(int)character:X4}（控制字符，消息里显示为空格）"
            : character.ToString();
}
