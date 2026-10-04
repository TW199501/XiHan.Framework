// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Importing;

namespace XiHan.Framework.Excel.Importing;

/// <summary>
/// 按字节位置读入固定宽度文字档（<c>.txt</c> 一类的定长记录档）
/// </summary>
/// <remarks>
/// <para>
/// 逐行惰性交出 <see cref="ExcelImportRow"/>，不物化整档：行号、<see cref="ExcelImportOptions.MaxRowCount"/>
/// 与取消都落在行与行之间。单次枚举占用的内存不随档的大小增长，上限是「列宽总和一份 + 一块读取缓冲」，
/// 列宽总和本身另有硬上限 <see cref="ExcelConstants.MaxFixedRowWidthBytes"/>。
/// </para>
/// <para>
/// <b>流所有权在调用方。</b>本类只读取不关闭传入的流，枚举结束后调用方仍可复位重读。读取总是从流的<u>起点</u>
/// 开始（调用方预设的流位置不会被尊重），并在起点之后剥掉该编码自己的 BOM，因此「从流中间续读」不是本类的能力。
/// 输入流必须可读且可定位：编码判别与 BOM 比对都要回读档头，不可定位的流直接拒绝，不把缓冲实现的失败面当契约。
/// </para>
/// <para>
/// <b>分行在字节层做，只认 <c>\r\n</c>、<c>\n</c>、<c>\r</c> 三种行尾</b>，与
/// <see cref="Abstractions.Exporting.ExcelTextOptions.NewLine"/> 写出的序列同一口径。跨段落的 <c>\r\n</c>
/// 算一个行尾，不会多交出一条空行；档尾正好落在行尾上时不会多出空行，最后一个<u>没有行尾</u>的残段算一行。
/// 不用 <c>StringReader.ReadLine()</c> 或 <see cref="StreamReader"/> 切行，理由有三：本组件的行尾定义要自己说了算
/// （那两个 API 认哪些字符当行尾由运行时决定，与导出侧写的序列不是同一件事）；整行要先按字节切列再解码，
/// 用文本读取器就得把整行解码两次；解码必须走 <see cref="TextEncodingResolver"/> 给的那个带严格回退的编码，
/// 而 <see cref="StreamReader"/> 的探测与宽松回退会把「编码指错」这件事悄悄吃掉。
/// </para>
/// <para>
/// <b>切列也在字节层做</b>：按 <see cref="ExcelImportOptions.FixedColumns"/> 的累计字节宽度取子数组，各自解码。
/// 一个汉字在 Big5 下占 2 字节、在 UTF-8 下占 3 字节，先解码再按字符切会让该列之后的每一列整体错位，
/// 因此宽度一律按字节。列边界正好落在一个多字节字符中间时，取到的那段字节在该编码下解不开：
/// UTF-8 与带严格回退的 Big5 都抛 <see cref="DecoderFallbackException"/>，读档在这里停下，
/// 不交出半个字、替换字符或私有区字符当正常数据——那种档与列定义本来就不符。
/// </para>
/// <para>
/// 不支持一个字符里可能带 <c>0x0D</c>／<c>0x0A</c> 的宽字节编码（UTF-16／UTF-32）：按字节找行尾会把一档切碎，
/// 按字节切列会把字符切成半个，两者都是交出错位的档而不是报错。取到的编码属于这两类时抛
/// <see cref="InvalidOperationException"/> 并点名该编码，请改用单字节前缀的编码或先转档。
/// </para>
/// <para>
/// <see cref="ExcelImportOptions.HasHeader"/> 在本路径<u>一律按「无表头」处理</u>：<see cref="ExcelImportOptions.TrimHeaders"/>
/// 没有对象可处理，源档的每一行都是数据，键名恒取列定义里的 <see cref="ExcelFixedWidthField.Key"/>，
/// 第一条数据行的 <see cref="ExcelImportRow.RowNumber"/> 是 <c>1</c>。定宽档的列名来自开发者给的列定义而不是
/// 源档文案，把表头语义套上去会凭空吃掉一条记录，而行号要留给错误报表定位。
/// <see cref="ExcelImportOptions.HeaderRowIndex"/> 仍然解释：前导那么多行丢掉、不切列也不留痕，丢掉的行照样占行号。
/// <see cref="ExcelImportOptions.Format"/> 与 <see cref="ExcelImportOptions.Delimiter"/>、
/// <see cref="ExcelImportOptions.SheetName"/> 在本路径不解释，不报错也不生效。
/// </para>
/// <para>
/// 取值一律是 <see cref="string"/>（定宽档没有格型概念），空字段是空字串而不是 <c>null</c>。
/// 行字节数不足列宽总和时缺的列补空字串，超出列宽时多出的部分丢弃——这两种情况<u>各记一条 Debug 日志</u>，
/// 报出行号与实际字节数，因为两者都在改写调用方期望的数据形态。
/// <see cref="ExcelImportOptions.TrimValues"/> 与 <see cref="ExcelImportOptions.SkipEmptyRows"/>
/// 在本路径照常生效，判「整行皆空」的口径与另一条导入路径共用同一份实现。
/// </para>
/// <para>
/// 与导出侧的关系：固定宽度布局写出的档（<c>Layout = FixedWidth</c>）可以由本类逐字段读回原值，
/// 该布局不套公式注入前缀，也不做引号与分隔符转义；补位空格属于布局，要拿回原值请显式设
/// <see cref="ExcelImportOptions.TrimValues"/> 为 <c>true</c>。列宽必须与写档时的编码一致：
/// <see cref="ExcelImportOptions.TextEncodingName"/> 不指名时走自动判别（BOM → 严格 UTF-8 试探 → Big5 回退），
/// 自动判别只保证「按判出来的编码解不会撞到解码错误」，不保证那是原档真正的编码，已知来源编码请指名。
/// </para>
/// <para>
/// 行数上限取构造时算好的那一份：只给日志器的构造用框架默认硬上限，收 <see cref="XiHanExcelOptions"/> 的那个
/// 构造用 <see cref="XiHanExcelOptions.MaxImportRows"/> 收紧后的值。两个构造差在数字上，判定与报错文字同一份，
/// 与容器路径也同一份；配置越出框架硬上限或不是正整数时在构造点抛出，不等第一次取行，也不夹回上限。
/// </para>
/// </remarks>
public sealed class FixedWidthTextImporter : IExcelImporter
{
    /// <summary>
    /// 一次读入的最小缓冲字节数
    /// </summary>
    private const int MinimumChunkBytes = 4096;

    /// <summary>
    /// 行尾字符：回车
    /// </summary>
    private const byte CarriageReturn = (byte)'\r';

    /// <summary>
    /// 行尾字符：换行
    /// </summary>
    private const byte LineFeed = (byte)'\n';

    private readonly ILogger<FixedWidthTextImporter> _logger;

    private readonly int _hardMaxRows;

    /// <summary>
    /// 用框架默认导入行数硬上限构造定宽读取器
    /// </summary>
    /// <param name="logger">本导入器的日志器，传 <c>null</c> 在构造时就抛</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> 为 <c>null</c></exception>
    public FixedWidthTextImporter(ILogger<FixedWidthTextImporter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hardMaxRows = ImportSharedRules.ResolveHardMaxRows(null);
    }

    /// <summary>
    /// 用配置里的导入行数硬上限构造定宽读取器
    /// </summary>
    /// <param name="options">Excel 选项，只取 <see cref="XiHanExcelOptions.MaxImportRows"/> 一项</param>
    /// <param name="logger">本导入器的日志器，传 <c>null</c> 在构造时就抛</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 或 <paramref name="logger"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="XiHanExcelOptions.MaxImportRows"/> 不是正整数，或高过框架硬上限
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/></exception>
    /// <remarks>
    /// 上限判据与容器路径共用一份，配置面也两边都接：只接一边就等于让「走哪条读取路径」决定能不能读到那么多行。
    /// </remarks>
    public FixedWidthTextImporter(XiHanExcelOptions options, ILogger<FixedWidthTextImporter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hardMaxRows = ImportSharedRules.ResolveHardMaxRows(options);
    }

    /// <summary>
    /// 逐行读入一份固定宽度文字档
    /// </summary>
    /// <param name="input">输入流，必须可读且可定位；只读取不关闭，读取从流起点开始</param>
    /// <param name="options">导入选项，传 <c>null</c> 等同于使用 <see cref="ExcelImportOptions"/> 的默认值。
    /// 默认值的 <see cref="ExcelImportOptions.FixedColumns"/> 是 <c>null</c>，因此传 <c>null</c> 必然抛</param>
    /// <param name="cancellationToken">取消令牌，入口检查一次，之后每取一行再检查一次</param>
    /// <returns>逐行数据的异步序列；零数据行交出空序列而不是抛</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> 不可读或不可定位（<c>ParamName</c> 为 <c>input</c>）；
    /// 或 <see cref="ExcelImportOptions.TextEncodingName"/> 无法解析（<c>ParamName</c> 为 <c>TextEncodingName</c>，
    /// 内层异常保留解析失败的原话）</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ExcelImportOptions.MaxRowCount"/> 高于本次生效的行数上限或不是正整数（<c>ParamName</c> 为 <c>MaxRowCount</c>；
    /// 上限由构造本类的选项决定，默认是框架硬上限 <see cref="ExcelConstants.DefaultMaxImportRows"/> 行）</exception>
    /// <exception cref="InvalidOperationException">
    /// <list type="bullet">
    /// <item><see cref="ExcelImportOptions.FixedColumns"/> 是 <c>null</c> 或空集合：没有列位置就切不出列，
    /// 「整行当一列」不是可用的降级；</item>
    /// <item>列定义不成立：清单里有空项、键是空字串或仅含空白、宽度不是正整数、多列之间键重复、
    /// 列宽总和超过硬上限 <see cref="ExcelConstants.MaxFixedRowWidthBytes"/>。每类各报各的，
    /// 一次只抛最先命中的那一类，并点名是第几列或哪个键；</item>
    /// <item>解码用的编码是 UTF-16／UTF-32 这类宽字节编码：按字节分行与按字节切列都会错位。</item>
    /// </list></exception>
    /// <exception cref="DecoderFallbackException">档的实际字节在所用编码下解不开：编码指错，
    /// 或列边界落在一个多字节字符中间。读档在中途停下，不产出替换字符当正常数据</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 方法体在<u>首次取行</u>时才运行（异步迭代器），上面这些检查因此都在第一次
    /// <c>MoveNextAsync</c> 时才抛出；调用 <see cref="ReadAsync"/> 本身不会抛，门面也不替它提前判。
    /// </para>
    /// <para>
    /// 校验顺序固定为：流的可读可定位 → <see cref="ExcelImportOptions.MaxRowCount"/> →
    /// <see cref="ExcelImportOptions.FixedColumns"/> → 取消令牌 → 编码判别与宽字节拒收。
    /// 前两项排在读档之前，是因为它们是<u>请求本身</u>的问题：同一条档上，越界的上限要报「上限越界」而不是
    /// 「没给列定义」，缺列定义的请求要报「没给列定义」而不是「编码解不开」。
    /// </para>
    /// <para>
    /// <see cref="ExcelImportRow.RowNumber"/> 是本档内的 1 起始记录序号：表头语义在本路径不存在，
    /// 因此没有行被吃掉；<see cref="ExcelImportOptions.HeaderRowIndex"/> 丢掉的前导行与
    /// <see cref="ExcelImportOptions.SkipEmptyRows"/> 跳过的空行都照样占号，不随跳过动作重排。
    /// 定宽档一行就是一条记录，值里不可能有换行，所以这个序号同时也是档里的物理行号。
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ExcelImportRow> ReadAsync(
        Stream input,
        ExcelImportOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.CanRead)
        {
            throw new ArgumentException("输入流不可读，无法导入。", nameof(input));
        }

        if (!input.CanSeek)
        {
            throw new ArgumentException(
                "固定宽度导入要求输入流可定位：编码判别与 BOM 比对都要回读档头，本类也在建立读取缓冲之前把位置复位到起点，" +
                "不可定位的流做不到。请先交给可定位的流（例如把上传的档拷进 MemoryStream）。",
                nameof(input));
        }

        var effective = options ?? new ExcelImportOptions();

        var maxRows = ImportSharedRules.ResolveMaxRowCount(effective.MaxRowCount, _hardMaxRows);
        var layout = FixedColumnLayout.Create(effective.FixedColumns);

        cancellationToken.ThrowIfCancellationRequested();

        input.Position = 0;

        // 编码判别与 BOM 剥除都用导入侧那一份实现，不在这里重抄一条链或一张前导字节表
        var encoding = TextEncodingResolver.Resolve(effective.TextEncodingName, input);
        RejectWideByteEncoding(encoding);
        input.Position = TextEncodingResolver.GetPreambleSkipBytes(encoding, input);

        var reader = new FixedWidthLineReader(input, layout.TotalWidthBytes);

        var rowNumber = 0;
        var emitted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await reader.MoveNextAsync(cancellationToken))
            {
                break;
            }

            rowNumber++;

            // 前导行按行号丢掉：不切列、不取值，也不为它记留痕
            if (rowNumber <= effective.HeaderRowIndex)
            {
                continue;
            }

            var row = layout.ReadRow(reader.Line, reader.LineBytes, encoding, effective.TrimValues);

            if (row.PartialKeys is not null)
            {
                _logger.LogDebug(
                    "定宽档第 {RowNumber} 行只有 {LineBytes} 字节，不足列宽总和 {TotalWidth} 字节：" +
                    "取不满的列 {Fields} 按空字串交出。",
                    rowNumber,
                    reader.LineBytes,
                    layout.TotalWidthBytes,
                    string.Join("、", row.PartialKeys));
            }

            if (reader.ExtraBytes > 0)
            {
                _logger.LogDebug(
                    "定宽档第 {RowNumber} 行有 {LineBytes} 字节，超出列宽总和 {TotalWidth} 字节：" +
                    "多出的 {ExtraBytes} 字节按列定义丢弃。",
                    rowNumber,
                    layout.TotalWidthBytes + reader.ExtraBytes,
                    layout.TotalWidthBytes,
                    reader.ExtraBytes);
            }

            if (effective.SkipEmptyRows && ImportSharedRules.IsEmptyRow(row.Values))
            {
                continue;
            }

            yield return new ExcelImportRow(rowNumber, row.Values);

            emitted++;
            if (emitted >= maxRows)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 拒收按字节切就会错位的宽字节编码
    /// </summary>
    /// <param name="encoding">本次用来解码的编码</param>
    /// <exception cref="InvalidOperationException">编码是 UTF-16 或 UTF-32 家族</exception>
    private static void RejectWideByteEncoding(Encoding encoding)
    {
        if (encoding is not (UnicodeEncoding or UTF32Encoding))
        {
            return;
        }

        throw new InvalidOperationException(
            $"固定宽度导入不支持编码「{encoding.WebName}」（代码页 {encoding.CodePage}）：" +
            "这种编码的字符里就可能带 0x0D 或 0x0A，按字节找行尾会把一档切成错位的许多行，按字节切列也会把字符切成半个。" +
            "请改用行尾字节唯一的编码（utf-8、big5、gb18030 一类的单字节前缀编码），或先把档转成那种编码再读。");
    }

    /// <summary>
    /// 列定义换算出的切列布局：每列的键、起始字节偏移与字节宽度
    /// </summary>
    private sealed class FixedColumnLayout
    {
        private FixedColumnLayout(string[] keys, int[] offsets, int[] widths, int totalWidthBytes)
        {
            Keys = keys;
            Offsets = offsets;
            Widths = widths;
            TotalWidthBytes = totalWidthBytes;
        }

        /// <summary>
        /// 每列取值用的键，顺序就是列定义的顺序
        /// </summary>
        private string[] Keys { get; }

        /// <summary>
        /// 每列在本行里的起始字节偏移
        /// </summary>
        private int[] Offsets { get; }

        /// <summary>
        /// 每列的字节宽度
        /// </summary>
        private int[] Widths { get; }

        /// <summary>
        /// 整行的字节宽度（各列宽度之和）
        /// </summary>
        internal int TotalWidthBytes { get; }

        /// <summary>
        /// 校验列定义并换算切列布局
        /// </summary>
        /// <param name="columns">列定义清单</param>
        /// <remarks>
        /// 这几类非法取值各报各的，一次只抛最先命中的那一类：调用方通常是一次只错一类，把几类堆进一条消息
        /// 反而看不清先改哪个。校验顺序与消息里的点名方式同导出侧的固定宽度预检一致。
        /// </remarks>
        /// <exception cref="InvalidOperationException">列定义为 <c>null</c> 或空集合，键为空、宽度非正、键重复，
        /// 或列宽总和超过硬上限 <see cref="ExcelConstants.MaxFixedRowWidthBytes"/></exception>
        internal static FixedColumnLayout Create(IReadOnlyList<ExcelFixedWidthField>? columns)
        {
            if (columns is null)
            {
                throw new InvalidOperationException(
                    $"固定宽度导入要求给出 {nameof(ExcelImportOptions.FixedColumns)} 的列定义（每列一个键与一个字节宽度）：" +
                    "没有列位置就切不出列，把整行当成一列交回不是可用的降级。读分隔符档请把该设置保持为 null。");
            }

            if (columns.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(ExcelImportOptions.FixedColumns)} 是空集合：一列都没有的定义切不出任何取值。" +
                    $"请至少给出一个 {nameof(ExcelFixedWidthField)}，读分隔符档则把该设置保持为 null。");
            }

            var keys = new string[columns.Count];
            var offsets = new int[columns.Count];
            var widths = new int[columns.Count];

            var blankKeys = new List<string>();
            var nonPositiveWidths = new List<string>();
            var duplicatedKeys = new List<string>();
            var nullColumns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0L;

            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];

                if (column is null)
                {
                    // 集合里塞了空项：让它在这里说清楚是第几项，不留到取键时撞一个 NullReferenceException
                    nullColumns.Add($"第 {index + 1} 项");
                    continue;
                }

                var key = column.Key;

                if (string.IsNullOrWhiteSpace(key))
                {
                    blankKeys.Add($"第 {index + 1} 列");
                }
                else if (!seen.Add(key))
                {
                    duplicatedKeys.Add(key);
                }

                if (column.WidthBytes < 1)
                {
                    nonPositiveWidths.Add($"{key}={column.WidthBytes}");
                }

                keys[index] = key;
                offsets[index] = (int)total;
                widths[index] = column.WidthBytes;
                total += column.WidthBytes;
            }

            if (nullColumns.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(ExcelImportOptions.FixedColumns)} 的列清单里有空项：{string.Join("、", nullColumns)}。" +
                    "空项没有键也没有宽度，不当成「这一列全空」的降级；请把每一项都写成具体的列定义。");
            }

            if (blankKeys.Count > 0)
            {
                throw new InvalidOperationException(
                    $"固定宽度导入的列定义里有列没有可用的键：{string.Join("、", blankKeys)}。" +
                    $"键是空字串或仅含空白时，取值再也取不到这一列；请给每列一个非空的 {nameof(ExcelFixedWidthField.Key)}。");
            }

            if (nonPositiveWidths.Count > 0)
            {
                throw new InvalidOperationException(
                    $"固定宽度导入的列宽必须是正整数字节，以下列非法：{string.Join("、", nonPositiveWidths)}。" +
                    "0 宽列会让它之后的列位整体前移，负数会让累计偏移倒退。");
            }

            if (duplicatedKeys.Count > 0)
            {
                throw new InvalidOperationException(
                    $"固定宽度导入的列定义里有重复键：{string.Join("、", duplicatedKeys)}。" +
                    $"同名的两列会让后一列盖掉前一列的取值，再也分不开；请给每列唯一的 {nameof(ExcelFixedWidthField.Key)}。" +
                    "分隔符路径那种加 _n 后缀的做法在这里不成立——定宽列名是开发者给的结构，不是源档文案。");
            }

            if (total > ExcelConstants.MaxFixedRowWidthBytes)
            {
                throw new InvalidOperationException(
                    $"固定宽度导入的列宽总和是 {total} 字节，超过单行缓冲上限 " +
                    $"{nameof(ExcelConstants.MaxFixedRowWidthBytes)} = {ExcelConstants.MaxFixedRowWidthBytes} 字节：" +
                    "一行要先整行读进缓冲才能按字节切列，不设上界就是让档的大小决定内存占用。" +
                    "请收紧列宽，或改用分隔符布局的档。");
            }

            return new FixedColumnLayout(keys, offsets, widths, (int)total);
        }

        /// <summary>
        /// 把本行的字节切成各列取值
        /// </summary>
        /// <param name="line">本行前 <see cref="TotalWidthBytes"/> 个字节</param>
        /// <param name="lineBytes">本行实际读到的字节数</param>
        /// <param name="encoding">解码用的编码，带严格回退</param>
        /// <param name="trimValues">是否去掉字串取值的首尾空白</param>
        /// <returns>本行取值集合，以及取不满的列清单（整行凑到列宽总和时为空）</returns>
        /// <remarks>
        /// 取不满的列包含两种情况：一列完全没有字节进来，以及一列只进来一部分字节（行尾来得比列宽早）。
        /// 两种都交空字串或该段的实际内容，并在留痕消息里一起点名，因为它们都是「档比列定义短」这一个原因。
        /// </remarks>
        internal RowReadResult ReadRow(byte[] line, int lineBytes, Encoding encoding, bool trimValues)
        {
            var values = new Dictionary<string, object?>(Keys.Length, StringComparer.Ordinal);
            List<string>? partialKeys = null;

            for (var index = 0; index < Keys.Length; index++)
            {
                var start = Offsets[index];
                var available = lineBytes - start;
                var take = available <= 0 ? 0 : Math.Min(Widths[index], available);

                if (take < Widths[index])
                {
                    (partialKeys ??= []).Add(Keys[index]);
                }

                var text = take > 0 ? encoding.GetString(line, start, take) : string.Empty;
                values[Keys[index]] = trimValues ? text.Trim() : text;
            }

            return new RowReadResult(values, partialKeys);
        }
    }

    /// <summary>
    /// 一行定宽数据的取值与取不满的列清单
    /// </summary>
    /// <param name="Values">本行取值，键序就是列定义顺序</param>
    /// <param name="PartialKeys">取不满的列清单，整行凑到列宽总和时为 <c>null</c></param>
    private readonly record struct RowReadResult(
        Dictionary<string, object?> Values,
        List<string>? PartialKeys);

    /// <summary>
    /// 字节层切行器：按块读入，只在 <c>\r\n</c>／<c>\n</c>／<c>\r</c> 处分行，每行只留下列宽总和那么多字节
    /// </summary>
    /// <remarks>
    /// 读入的字节先落在一块固定大小的缓冲上：列范围内的字节存进行缓冲，超出列宽总和的字节只计数不保存
    /// （那些字节本来就要丢弃，留着只是让一行的畸形长度换成一次内存增长）。行尾 <c>\r\n</c> 跨在两块缓冲边界上时
    /// 由 <c>_pendingLineFeed</c> 认成同一个行尾，因此不会多交出一条空行。
    /// </remarks>
    private sealed class FixedWidthLineReader(Stream input, int totalWidthBytes)
    {
        private readonly byte[] _chunk = new byte[Math.Max(totalWidthBytes + 2, MinimumChunkBytes)];
        private readonly byte[] _line = new byte[totalWidthBytes];
        private int _chunkFilled;
        private int _chunkPosition;
        private int _lineBytes;
        private int _extraBytes;
        private bool _pendingLineFeed;
        private bool _endOfStream;

        /// <summary>
        /// 本行的字节缓冲，长度是列宽总和；只有 <see cref="LineBytes" /> 那么多个字节属于本行
        /// </summary>
        internal byte[] Line => _line;

        /// <summary>
        /// 本行落在列宽范围内的字节数，不超过列宽总和
        /// </summary>
        internal int LineBytes => _lineBytes;

        /// <summary>
        /// 本行超出列宽总和而被丢弃的字节数
        /// </summary>
        internal int ExtraBytes => _extraBytes;

        /// <summary>
        /// 推进到下一行
        /// </summary>
        /// <param name="cancellationToken">取消令牌，交给流的读取</param>
        /// <returns>本行有内容时为 <c>true</c>；档读完交出 <c>false</c>。空行（零字节）也是 <c>true</c></returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
        internal async Task<bool> MoveNextAsync(CancellationToken cancellationToken)
        {
            _lineBytes = 0;
            _extraBytes = 0;

            // 上一行以 \r 收尾时，紧跟其后的 \n 属于那个行尾，先吃掉再开始本行；
            // 不这么做就会把 "\r\n" 拆成「一行 + 一个空行」，多造一条谁也没写过的记录。
            if (_pendingLineFeed)
            {
                _pendingLineFeed = false;

                if (await PeekByteAsync(cancellationToken) == LineFeed)
                {
                    _chunkPosition++;
                }
            }

            while (true)
            {
                if (_chunkPosition == _chunkFilled)
                {
                    if (_endOfStream)
                    {
                        return _lineBytes > 0 || _extraBytes > 0;
                    }

                    _chunkFilled = await input.ReadAsync(_chunk.AsMemory(), cancellationToken);
                    _chunkPosition = 0;

                    if (_chunkFilled == 0)
                    {
                        _endOfStream = true;

                        return _lineBytes > 0 || _extraBytes > 0;
                    }

                    continue;
                }

                var value = _chunk[_chunkPosition++];

                if (value == CarriageReturn)
                {
                    _pendingLineFeed = true;

                    return true;
                }

                if (value == LineFeed)
                {
                    return true;
                }

                if (_lineBytes < _line.Length)
                {
                    _line[_lineBytes++] = value;
                }
                else
                {
                    _extraBytes++;
                }
            }
        }

        /// <summary>
        /// 看下一个字节但不消耗它，必要时补一次读
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>下一个字节，档已读完时返回 <c>-1</c></returns>
        private async Task<int> PeekByteAsync(CancellationToken cancellationToken)
        {
            while (_chunkPosition == _chunkFilled)
            {
                if (_endOfStream)
                {
                    return -1;
                }

                _chunkFilled = await input.ReadAsync(_chunk.AsMemory(), cancellationToken);
                _chunkPosition = 0;

                if (_chunkFilled == 0)
                {
                    _endOfStream = true;

                    return -1;
                }
            }

            return _chunk[_chunkPosition];
        }
    }
}
