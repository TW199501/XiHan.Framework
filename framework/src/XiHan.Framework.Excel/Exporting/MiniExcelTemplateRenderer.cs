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
/// 数据在交给渲染库之前先被走访一遍，逐值套用与两条 xlsx 导出路径同一份的判据：字串值以 <c>=</c>、<c>+</c>、
/// <c>-</c>、<c>@</c>、制表符或回车起首，或以渲染库自己的公式指令前缀 <c>$=</c>
/// 起首时拒写（<see cref="ArgumentException"/>，<see cref="ArgumentException.ParamName"/> 为 <c>data</c>）；
/// 取值是工作簿装不下的那一类时同样拒写（<see cref="InvalidOperationException"/>）。两者都点名键路径、
/// 都抛在写出第一个字节之前，因此输出流零字节。处置是拒写而不是加单引号前缀：加前缀等于改写业务数据，
/// 而模板没有留痕机制能交代改了什么。判据与分隔符文字导出相同，处置形态不同，这条不对称是刻意的。
/// </para>
/// <para>
/// 写出前校验只保证校验那一刻：走访结束后到渲染库写出之间数据被改不在本类的覆盖范围内，
/// 调用方不得在这段时间修改数据。走访会把集合成员完整枚举一遍，排在渲染库自己那两遍之前，
/// 因此只允许枚举一次的数据源用不了，惰性数据源会提前求值。走访不解析模板，
/// 模板没有引用的键同样在校验范围内；缺键仍然留空、不报错。
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
    /// <see cref="long"/>／<see cref="ulong"/>／<see cref="decimal"/>，或长于
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 的字串），消息点名键路径并给出与两条 xlsx 导出路径
    /// 逐字相同的成因句；抛在调用渲染库之前，输出流零字节</exception>
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

        // 写出前走访：判据与两条 xlsx 导出路径同一份，但处置是拒写而不是加前缀，详见方法与类注释
        EnsureWritableData(data, cancellationToken);

        await MiniExcel.SaveAsByTemplateAsync(output, template, data, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // 回传结果之前再查一次：渲染库只在动手前查令牌，取消落在最后一段写出期间时没人再查，
        // 本方法若就此返回，调用方读到的就是「渲染完成」，而产出的那份档未必收得下整个渲染
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 写出前走访数据的深度预算：根对象一层、集合一层、集合元素再一层，
    /// 与模板占位符 <c>{{键}}</c> 和 <c>{{键.子键}}</c> 能点到的深度一致
    /// </summary>
    /// <remarks>
    /// 深度是有意的硬界而不是「走到没有成员为止」：模板占位符最多两级，再深的值根本落不进任何一格，
    /// 走访下去只会让一份渲染不出问题的数据被拒；同时这道界也让自引用的对象图不可能把走访拉成无限递归。
    /// </remarks>
    private const int MaximumWalkDepth = 3;

    /// <summary>
    /// 取值检查抛出时点名的参数名：出事的值来自 <c>data</c>，与模板那三条 <c>ArgumentException</c> 的 <c>template</c> 分开
    /// </summary>
    private const string DataParameterName = "data";

    /// <summary>
    /// 渲染库自己的公式指令前缀：值以它起首时，渲染库把余下整段当公式写进格子，而不是当文字
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一道与 <see cref="TextWriterHelper.NeedsFormulaEscape"/> 量的不是同一件事：那一份量的是「表格软件读到
    /// 这一格文字时会不会自己当公式执行」，这一道量的是「渲染库在落格之前会不会先把这段文字改成公式」，
    /// 因此两道各判各的，不是把同一判据抄成两份。
    /// </para>
    /// <para>
    /// 触发条件实测只有恰好在值起首的 <c>$=</c>：<c>"$=1+1"</c> 落成公式元素，<c>"$=HYPERLINK(...)"</c> 与
    /// <c>"$=WEBSERVICE(...)"</c> 同样落成真公式；<c>"$"</c>、<c>"$$=1+1"</c>、<c>"$ =1+1"</c> 与
    /// <c>"合计$=1+1"</c> 都落文字格。占位符后面还跟着模板文案时（形如 <c>{{V}}元</c>）照样成公式，
    /// 渲染库把模板文案一并算进公式体，因此不能靠「占位符是不是占满整格」缩小这道检查。
    /// </para>
    /// </remarks>
    private const string TemplateFormulaDirective = "$=";

    /// <summary>
    /// 走访数据，逐值套用与两条 xlsx 导出路径同一份的公式注入判据与取值域判据
    /// </summary>
    /// <param name="data">调用方交回的渲染数据，本方法只读不改</param>
    /// <param name="cancellationToken">取消令牌，每走访一个字典项或集合元素之前查一次</param>
    /// <remarks>
    /// <para>
    /// 走访范围是顶層成员与 <see cref="IEnumerable"/> 元素成员，按成员名取值，不区分属性与字段：
    /// 渲染库在顶層两种成员都取值，在集合元素只取属性（对只有字段的元素它自己抛
    /// <c>NullReferenceException</c>），这里按更宽的那一份走访，宁可多拒不放过。
    /// </para>
    /// <para>
    /// 走访不看模板，因此覆盖的是数据的全部顶層成员与集合元素成员，不限于模板真正引用的那些键——
    /// 模板是一条流，渲染库读完就关掉，为挑出被引用的键先解析一遍模板等于把版面读两次。
    /// 代价是数据里带着一个模板没引用的越界值时同样被拒，以及成员取值器自己抛出时原样透传；
    /// 两者都不做静默跳过，跳过就是把「哪一份数据能渲染」交给模板里恰好写了哪些占位符决定。
    /// </para>
    /// <para>
    /// 集合成员会被完整枚举一遍，且这一遍排在渲染库自己那两遍之前（实测渲染库对同一集合枚举两次），
    /// 因此惰性数据源会提前求值、只读一次的数据源不能用；走访与渲染之间数据被改属调用方违约，见类注释。
    /// 型别到成员的映射按本次走访就地缓存，缓存的寿命只到走访结束，不是常驻表。
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

        // 字串先判：它同时是 IEnumerable，落到集合分支会被拆成一个个字符
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

        // 走到这里的都是会整份落进格子的那一个值：可能是深度已经用尽，也可能是这个型别本来就不按键名再拆
        EnsureWritableValue(node, path);
    }

    /// <summary>
    /// 判断一个型别是不是「按键名取值」的容器：只有调用方自己的数据类型算，框架与执行期的值型别不算
    /// </summary>
    /// <param name="type">节点的运行期型别</param>
    /// <returns>应当继续按成员名往下拆时为 <c>true</c></returns>
    /// <remarks>
    /// <para>
    /// 模板占位符只认三种容器：字典按键查、集合按下标展开、调用方自己的数据类型按成员名查。其余型别在渲染库
    /// 那边一律按整份值落格，不会按键名再拆，因此走访也不拆。判据取命名空间：<c>System</c> 与其子命名空间下的
    /// 型别（日期与时距、<see cref="Guid"/>、<see cref="decimal"/>、各种数值型别、枚举，以及执行期类型如
    /// <see cref="System.Threading.CancellationToken"/>）都当值处理；匿名类型没有命名空间，
    /// 而它正是模板数据最常见的一种形态，因此归到容器那一侧。
    /// </para>
    /// <para>
    /// 这一道不是洁癖：<see cref="DateTime"/> 有自己的公开属性，照着成员名拆下去会走到 <c>Ticks</c>
    /// 一类内部表示，那是一份十九位的整数，会把完全合法的日期判成越界。
    /// </para>
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
    /// <para>
    /// 起首字符那一道复用 <see cref="TextWriterHelper.NeedsFormulaEscape"/> 那一份，不在这里另抄一张字符表：
    /// 模板路径要与一般导出套用相同的公式防护，「相同」指的是判据相同。要复用的是这个<b>判定</b>，
    /// 不是它旁边的 <see cref="TextWriterHelper.EscapeFormula"/>——那一个会给值加单引号前缀，等于改写业务数据，
    /// 而模板没有留痕机制能交代改了什么，所以模板路径的处置是拒写。
    /// </para>
    /// <para>
    /// 渲染库自己的公式指令前缀另判一道，见 <see cref="TemplateFormulaDirective"/>：它命中的值会被渲染库改成
    /// 公式元素写进档里，那已经不是「表格软件怎么读这格文字」的问题，六个起首字符覆盖不到它。
    /// </para>
    /// <para>
    /// 取值域判据复用 <see cref="ExcelWorkbookWriteGuard.DescribeUnwritable"/> 那一份，成因文字原样带出，
    /// 只把行位置换成键路径：模板没有行列表头，能点名的是数据里的键。
    /// </para>
    /// <para>
    /// 两道判据的异常型别沿用导出侧既有分工——公式注入是调用方给错了参数，走 <see cref="ArgumentException"/>；
    /// 取值域是数据装不进工作簿，走 <see cref="InvalidOperationException"/>。这里不新造型别。
    /// </para>
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

        if (ExcelWorkbookWriteGuard.DescribeUnwritable(value) is { } reason)
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
    /// 索引器不算成员：它要参数才取得到值，模板占位符也点不到它。
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
