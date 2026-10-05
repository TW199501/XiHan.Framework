// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using ExcelDataReader;
using ExcelDataReader.Exceptions;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Importing;

namespace XiHan.Framework.Excel.Importing;

/// <summary>
/// 用 ExcelDataReader 读入 <c>.xls</c>、<c>.xlsx</c>、<c>.csv</c> 与 <c>.txt</c>
/// </summary>
/// <remarks>
/// <para>
/// 逐行惰性交出 <see cref="ExcelImportRow"/>，不物化整档：行号、<see cref="ExcelImportOptions.MaxRowCount"/>
/// 与取消都落在行与行之间，因此百万行档不会先在内存里排一遍。
/// </para>
/// <para>
/// <b>流所有权在调用方。</b>底层读取器默认会连传入的流一起关掉（<c>LeaveOpen</c> 为 <c>false</c> 时
/// 读取器 <c>Dispose</c> 之后流不可再访问），本实现在所有路径上一律置 <c>LeaveOpen = true</c>，
/// 枚举结束后调用方仍可复位重读同一条流。读取总是从流的<u>起点</u>开始：调用方预设的流位置不会被尊重，
/// 本类在建立读取器之前先把它复位到 0，因此「从流中间续读」不是本类的能力。
/// </para>
/// <para>
/// 输入流必须可定位：签章嗅探要把位置复原，容器解析也要回到起点，不可定位的流做不到
/// （底层读取器在这种情况下抛 <see cref="NotSupportedException"/>）。本类在建立读取器之前先拒绝，
/// 不把库的失败面当契约。
/// </para>
/// <para>
/// <see cref="ExcelReaderConfiguration.TrimWhiteSpace"/> 显式置 <c>false</c>：该配置在 3.9.0 上<u>默认开启</u>，
/// 会把 CSV 字段首尾空白静默去掉，而且只作用于文字档、不作用于工作簿——同一个值在两种来源上取回不同结果是更糟的事，
/// 因此空白取舍统一由 <see cref="ExcelImportOptions.TrimHeaders"/> 与 <see cref="ExcelImportOptions.TrimValues"/>
/// 在两条路径上一致处理。
/// </para>
/// <para>
/// 文字档的解码编码见 <see cref="TextEncodingResolver"/>。读到的字能不能信，取决于两处库的现实：
/// <list type="bullet">
/// <item>读取器认 BOM，且 BOM 优先于 <c>FallbackEncoding</c>；无 BOM 时<u>不会</u>先按 UTF-8 试解再落回退，
/// 而是直接用 <c>FallbackEncoding</c>。因此判错编码就交回另一种语言的字，本类给出的回退编码只保证「解码不撞错误」，
/// 不保证判对。已知编码请指名 <see cref="ExcelImportOptions.TextEncodingName"/>。</item>
/// <item>无 BOM 又未按 UTF-8 成立的档回退 Big5 时，真正的非法 Big5 字节不会报错：<c>System.Text</c> 的 Big5
/// 解码器把无法配对的字节换成私有区字符 <c>U+F8F8</c>，两侧都换成异常回退也拦不住它。这属于代码页自身的口径，
/// 本类不额外判死，但读到的字里出现 <c>U+F8F8</c> 就表示源档那一处字节已经损坏。</item>
/// </list>
/// </para>
/// <para>
/// 来源不做补偿：合并单元格除左上角外的格位读回 <c>null</c>，空格子在文字档里是空字串、在工作簿里是 <c>null</c>，
/// 公式只读回已缓存的值。样式、批注、图表与宏不解释。加密工作簿不支持（本类不传
/// <see cref="ExcelReaderConfiguration.Password"/>）。
/// </para>
/// <para>
/// 显式 <see cref="ExcelImportOptions.Format"/> 为 <see cref="ExcelImportFormat.Xls"/> 或
/// <see cref="ExcelImportFormat.Xlsx"/> 时<u>不强判签章</u>：读取器按内容自行选容器解析器，因此档名与内容不符可以正常读。
/// 容器读不通时（伪造的档头、截断的 zip、读到一半崩掉的工作簿），库的 <c>ExcelReaderException</c> 家族与
/// <see cref="InvalidDataException"/> 由本类换成 <see cref="InvalidOperationException"/>，库原话留在内部异常里——
/// 抽象契约在抽象包里、不引用任何第三方库，库的异常型别不能算对外承诺。「档头判不出格式」与「判得出但容器读不通」
/// 因此落在同一个类型上。
/// </para>
/// <para>
/// 一处不收口的残留：对上 OLE 档头却短到读不出目录的伪装档，会由 BCL 交回 <see cref="ArgumentException"/>，
/// 本类<u>不</u>把它一起收掉——<see cref="DecoderFallbackException"/> 同样是 <see cref="ArgumentException"/> 的后代，
/// 按 <see cref="ArgumentException"/> 收口会把「编码指错」这条正当失败一并吞成容器异常。
/// 调用方按型别分流时要认这条现实。
/// </para>
/// <para>
/// 行数上限取构造时算好的那一份：无参构造用框架默认硬上限，收
/// <see cref="XiHanExcelOptions"/> 的那个重载用 <see cref="XiHanExcelOptions.MaxImportRows"/> 收紧后的值，
/// 两个构造的差别只在数字上，判定与报错文字同一份。配置值越出框架硬上限或不是正整数时在构造点抛出，
/// 不等第一次取行，也不夹回上限。
/// </para>
/// <para>
/// 档的规模有两道界，都判在建立读取器之前。<b>其一</b>是档大小：取构造时的
/// <see cref="XiHanExcelOptions.MaxImportBytes"/>（无参构造用 <see cref="ExcelConstants.DefaultMaxImportBytes"/>），
/// 超限拒收整份档，判据与固定宽度路径共用一份。<b>其二</b>只对 <c>xlsx</c> 生效，判的是解压后的规模：
/// 按 zip 中央目录里的元数据扫一遍各部件的解压后长度、总长与解压比，任一道越界就拒收，
/// 一个部件都不解压。第二道界是必需的，因为工作簿读取器开簿时就把 <c>xl/sharedStrings.xml</c>
/// 整份载进内存，那笔开销发生在读出第一行<u>之前</u>，行数上限与取消令牌都拦不住它：
/// 一份一百万字节的档可以把几百兆字节的共享字串塞进本进程。三道解压侧上限不可配置，
/// 取值见 <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 一族的说明。
/// </para>
/// <para>
/// 规模还有第三个维度是<u>宽度</u>：每一行的列数不得超过 <see cref="ExcelConstants.MaxImportColumns"/>，
/// 逐行判在建键之前，超限整份档拒收。列数与行数一样决定单次导入的成本——每列都要一个键名与一个字典项——
/// 而行数上限管不到它：一份两行的档也可以有十万列。
/// </para>
/// </remarks>
public sealed class ExcelDataReaderImporter : IExcelImporter
{
    /// <summary>
    /// 每隔多少行检查一次取消令牌
    /// </summary>
    private const int CancellationCheckIntervalRows = 64;

    /// <summary>
    /// 解压比只对不小于这个解压后长度的部件判（1 MiB）
    /// </summary>
    /// <remarks>
    /// 更小的部件即使比值难看也占不了多少内存，而它们的总量另有
    /// <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 兜住；对小部件判比值只会把
    /// 「一小段重复度高的样式表」误判成炸弹。
    /// </remarks>
    private const long CompressionRatioFloorBytes = 1_048_576;

    private readonly int _hardMaxRows;

    private readonly long _maxImportBytes;

    /// <summary>
    /// 用框架默认导入行数硬上限构造读取器
    /// </summary>
    public ExcelDataReaderImporter()
    {
        _hardMaxRows = ImportSharedRules.ResolveHardMaxRows(null);
        _maxImportBytes = ImportSharedRules.ResolveMaxImportBytes(null);
    }

    /// <summary>
    /// 用配置里的导入上限构造读取器
    /// </summary>
    /// <param name="options">Excel 选项，取 <see cref="XiHanExcelOptions.MaxImportRows"/>
    /// 与 <see cref="XiHanExcelOptions.MaxImportBytes"/> 两项</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="XiHanExcelOptions.MaxImportRows"/> 不是正整数，或高过框架硬上限
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/></exception>
    public ExcelDataReaderImporter(XiHanExcelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _hardMaxRows = ImportSharedRules.ResolveHardMaxRows(options);
        _maxImportBytes = ImportSharedRules.ResolveMaxImportBytes(options);
    }

    /// <summary>
    /// <c>.csv</c> 的默认分隔符，与导出侧同一口径
    /// </summary>
    private const char CsvDefaultDelimiter = ',';

    /// <summary>
    /// <c>.txt</c> 的默认分隔符，与导出侧同一口径
    /// </summary>
    private const char TxtDefaultDelimiter = '\t';

    /// <summary>
    /// 无表头、表头空位与行变宽补列时的键名前缀
    /// </summary>
    private const string PositionalKeyPrefix = "Col";

    /// <summary>
    /// 逐行读入一份 Excel 或文字档
    /// </summary>
    /// <param name="input">输入流，必须可读且可定位；只读取不关闭，读取从流起点开始</param>
    /// <param name="options">导入选项，传 <c>null</c> 等同于使用 <see cref="ExcelImportOptions"/> 的默认值</param>
    /// <param name="cancellationToken">取消令牌，入口检查一次，之后每 <c>64</c> 行再检查一次</param>
    /// <returns>逐行数据的异步序列；零数据行交出空序列而不是抛</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> 不可读或不可定位；
    /// 或 <see cref="ExcelImportOptions.TextEncodingName"/> 无法解析（<c>ParamName</c> 为 <c>TextEncodingName</c>，
    /// 内层异常保留解析失败的原话）</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ExcelImportOptions.MaxRowCount"/> 高于本次生效的行数上限或不是正整数（<c>ParamName</c> 为 <c>MaxRowCount</c>；
    /// 上限由构造本类的选项决定，默认是框架硬上限 <see cref="ExcelConstants.DefaultMaxImportRows"/> 行）</exception>
    /// <exception cref="InvalidOperationException">
    /// <list type="bullet">
    /// <item>档的字节数超过本次生效的 <see cref="XiHanExcelOptions.MaxImportBytes"/>：消息写出档的实际大小与该上限，
    /// 判在格式判别之前，整份档拒收；</item>
    /// <item><c>xlsx</c> 容器的解压规模越界（单个部件解压后长度、解压后总长、单个部件的解压比，
    /// 三者任一）：消息点名越界的那个部件与越界的数字，判在建立工作簿读取器之前，一个部件都不解压；</item>
    /// <item><see cref="ExcelImportOptions.Format"/> 为 <c>null</c> 且档头判不出格式：消息写出档头字节的可读形式
    /// 并点名 HTML 表格／XML 表格这类伪装；</item>
    /// <item>格式给出或判出但容器读不通（伪造的档头、截断的档、损坏的簿）：消息点名该格式、附同一份可读档头，
    /// 并把库原话留在内部异常；</item>
    /// <item>某一行的列数超过 <see cref="ExcelConstants.MaxImportColumns"/>：消息写出实际列数与该上限，
    /// 逐行判在建键之前，整份档拒收，不截断列清单也不交出前若干列；</item>
    /// <item><see cref="ExcelImportOptions.SheetName"/> 在本工作簿里不存在：消息列出实际表名。</item>
    /// </list></exception>
    /// <exception cref="DecoderFallbackException">文字档的实际字节在所用编码下解不开（UTF-8 一支）。
    /// 库不会为解不开的字节产出替换字符当正常数据，读档在中途停下</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 方法体在<u>首次取行</u>时才运行（异步迭代器），上面这些检查因此都在第一次
    /// <c>MoveNextAsync</c> 时才抛出；调用 <see cref="ReadAsync"/> 本身不会抛。门面要提前拒绝非法选项得自己先判。
    /// </para>
    /// <para>
    /// <see cref="ExcelImportRow.RowNumber"/> 是本表内的 1 起始行号，含 <see cref="ExcelImportOptions.HeaderRowIndex"/>
    /// 丢掉的前导行与 <see cref="ExcelImportOptions.SkipEmptyRows"/> 跳过的空行，不随跳过动作重排。
    /// 一次导入只读一张表，行号以那张表自身的第一行起算。
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
                "导入要求输入流可定位：格式嗅探与底层容器解析都要把流位置回到起点，不可定位的流做不到。" +
                "请先交给可定位的流（例如把上传的档拷进 MemoryStream）。",
                nameof(input));
        }

        var effective = options ?? new ExcelImportOptions();

        // 顺序是刻意的：上限校验排在格式判别与建立读取器之前，非法的 MaxRowCount 在一条判别不出格式的垃圾档上
        // 也要报「上限越界」而不是报「判不出格式」——调用方放大上限是请求本身的问题，与档的内容无关。
        var maxRows = ImportSharedRules.ResolveMaxRowCount(effective.MaxRowCount, _hardMaxRows);

        cancellationToken.ThrowIfCancellationRequested();

        // 档大小先判：超限的档连格式都不必判。判据与固定宽度路径共用一份，两个来源对「这份档太大」不能各说一套。
        ImportSharedRules.ValidateImportBytes(input.Length, _maxImportBytes);

        // 读取一律从流起点开始：容器解析会复位到起点，嗅探也照同一个起点，
        // 否则「调用方把位置留在中间」时嗅探判的是中间那段，而解析器读的是整档，两者会各说各话。
        input.Position = 0;

        // 档头取一次就留着：判不出格式与容器读不通两条失败都要把同一份可读档头写进消息，
        // 不能在异常里再读一遍流（那时位置已被解析器动过）。
        var header = ExcelFormatProbe.ReadHeader(input, ExcelFormatProbe.HeaderByteCount);

        var format = effective.Format ?? DetectFormatOrThrow(header);
        var isText = format is ExcelImportFormat.Csv or ExcelImportFormat.Txt;

        // 解压规模判在建立读取器之前：工作簿读取器开簿时就把 sharedStrings 整份载进内存，
        // 那笔开销发生在读出第一行之前，行数上限与取消令牌都拦不住它。
        GuardDecompressedSize(input, format);

        // 建立读取器：文字路径的分隔符与编码在这里钉死，二进制路径不读 TextEncodingName 与 Delimiter
        var configuration = new ExcelReaderConfiguration
        {
            LeaveOpen = true,
            TrimWhiteSpace = false
        };

        if (isText)
        {
            configuration.FallbackEncoding = TextEncodingResolver.Resolve(effective.TextEncodingName, input);
            configuration.AutodetectSeparators =
            [
                effective.Delimiter ?? (format == ExcelImportFormat.Csv ? CsvDefaultDelimiter : TxtDefaultDelimiter)
            ];
        }

        using var reader = CreateReader(input, configuration, isText, format, header);

        if (!isText && effective.SheetName is not null)
        {
            SelectSheet(reader, effective.SheetName, format, header);
        }

        var rowNumber = 0;
        var emitted = 0;
        HeaderKeySet? keys = null;

        while (ReadNext(reader, format, header))
        {
            rowNumber++;

            if (rowNumber % CancellationCheckIntervalRows == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 列数上限判在建键之前：建键与每行取值都按列数放大，判晚了那笔成本已经付掉了
            GuardColumnCount(reader.FieldCount);

            // 前导行按行号丢掉，不参与表头与取值
            if (rowNumber <= effective.HeaderRowIndex)
            {
                continue;
            }

            if (keys is null)
            {
                if (effective.HasHeader)
                {
                    keys = HeaderKeySet.BuildKeys(reader, effective.TrimHeaders);
                    continue;
                }

                keys = HeaderKeySet.PositionalKeys(reader.FieldCount);
            }

            // 行比表头宽时补出 Col{n}，多出来的列保留而不丢弃
            keys.ExtendKeys(reader.FieldCount);

            var values = ReadValues(reader, keys.Keys, effective.TrimValues);

            if (effective.SkipEmptyRows && ImportSharedRules.IsEmptyRow(values))
            {
                continue;
            }

            yield return new ExcelImportRow(rowNumber, values);

            emitted++;
            if (emitted >= maxRows)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 自动判别格式，判不出来时抛出可定位的说明
    /// </summary>
    /// <param name="header">已取好的档头字节</param>
    /// <exception cref="InvalidOperationException">档头不是已知签名</exception>
    private static ExcelImportFormat DetectFormatOrThrow(byte[] header)
    {
        var detected = ExcelFormatProbe.Detect(header);

        if (detected is not null)
        {
            return detected.Value;
        }

        var markup = ExcelFormatProbe.DescribeMarkupOpening(header);

        var disguise = markup is null
            ? "它不是已知签名的 Excel 容器，也不是有签名可依的文字档"
            : $"它是以 {markup} 起始的标记语言文本，按 HTML 表格或 XML 表格伪装成 Excel 的样子";

        throw new InvalidOperationException(
            $"无法从档头判定导入格式：前 {header.Length} 字节 = {ExcelFormatProbe.DescribeHeader(header)}；{disguise}。" +
            $"请把 ExcelImportOptions.Format 显式指名：文字档用 {ExcelImportFormat.Csv} 或 {ExcelImportFormat.Txt}" +
            $"（同时给 Delimiter 与 TextEncodingName），{ExcelImportFormat.Xls}／{ExcelImportFormat.Xlsx} 只在档真是那种容器时给。" +
            "HTML 表格与 XML 表格本身没有可靠签名，本组件不读它们，请先转成 xlsx 或 csv。");
    }

    /// <summary>
    /// 按 zip 元数据判一个 xlsx 容器的解压规模，扫完把流位置归零
    /// </summary>
    /// <param name="input">输入流，可定位</param>
    /// <param name="format">本次要读的格式；不是 <see cref="ExcelImportFormat.Xlsx"/> 时什么都不做</param>
    /// <exception cref="InvalidOperationException">
    /// 单个部件解压后长度超过 <see cref="ExcelConstants.MaxImportEntryDecompressedBytes"/>，
    /// 或解压后总长超过 <see cref="ExcelConstants.MaxImportDecompressedBytes"/>，
    /// 或某个部件的解压比超过 <see cref="ExcelConstants.MaxImportCompressionRatio"/>
    /// </exception>
    /// <remarks>
    /// <para>
    /// 只读 zip 的中央目录，<u>不解压任何部件</u>：<see cref="ZipArchiveEntry.Length"/> 与
    /// <see cref="ZipArchiveEntry.CompressedLength"/> 都写在目录里，因此这道检查的成本与档的内容规模无关，
    /// 一份 1 MiB 的炸弹和一份 1 MiB 的正常档扫起来一样快。
    /// </para>
    /// <para>
    /// 以 <c>leaveOpen: true</c> 打开，流的所有权始终在调用方；<see cref="ZipArchive"/> 读完目录会把位置留在
    /// 档尾附近，因此无论判过还是判不过，退出前一律把 <see cref="Stream.Position"/> 归零。这是本类自己
    /// 对调用方的承诺——读取从流的起点开始、扫档不吃调用方的流——不建立在「底层读取器会不会自己回头定位」上；
    /// 判不过的时候，调用方拿回的也是一条停在起点、可以就地检查或另作处置的流。
    /// </para>
    /// <para>
    /// 档头是 zip 签名却打不开目录（截断的档、伪造的档头）时不在这里报错，交给
    /// <see cref="CreateReader"/> 报那条已经声明过的「按 Xlsx 读不通」：容器失败的消息只有一份口径，
    /// 这道检查不另立一套。
    /// </para>
    /// </remarks>
    private static void GuardDecompressedSize(Stream input, ExcelImportFormat format)
    {
        if (format != ExcelImportFormat.Xlsx)
        {
            return;
        }

        var total = 0L;

        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);

            foreach (var entry in zip.Entries)
            {
                // 单部件先判：任何离谱的声明长度都在累加之前被挡掉，总和因此不会溢出
                if (entry.Length > ExcelConstants.MaxImportEntryDecompressedBytes)
                {
                    throw new InvalidOperationException(
                        $"xlsx 容器里的部件「{entry.FullName}」解压后有 {entry.Length} 字节，" +
                        $"超过单个部件的上限 {ExcelConstants.MaxImportEntryDecompressedBytes} 字节：整份档拒收，不建立工作簿读取器。" +
                        "工作簿读取器开簿时把共享字串整份载进内存，单个部件的解压后长度直接换算成本进程的内存占用。");
                }

                total += entry.Length;

                if (total > ExcelConstants.MaxImportDecompressedBytes)
                {
                    throw new InvalidOperationException(
                        $"xlsx 容器解压后总长超过上限 {ExcelConstants.MaxImportDecompressedBytes} 字节：" +
                        $"累加到部件「{entry.FullName}」时已达 {total} 字节，整份档拒收，不建立工作簿读取器。" +
                        "档的字节数说的是压缩后的大小，读它要付的内存与 I/O 由解压后的规模决定。");
                }

                if (entry.Length >= CompressionRatioFloorBytes &&
                    entry.CompressedLength > 0 &&
                    entry.Length / entry.CompressedLength > ExcelConstants.MaxImportCompressionRatio)
                {
                    throw new InvalidOperationException(
                        $"xlsx 容器里的部件「{entry.FullName}」解压比过高：压缩后 {entry.CompressedLength} 字节，" +
                        $"解压后 {entry.Length} 字节，是 {entry.Length / entry.CompressedLength} 倍，" +
                        $"超过上限 {ExcelConstants.MaxImportCompressionRatio} 倍：整份档拒收，不建立工作簿读取器。" +
                        "承载数据的部件压缩比在数十倍量级，上百倍意味着这个部件里几乎没有信息量。");
                }
            }
        }
        catch (InvalidDataException)
        {
            // 档头是 zip 签名但目录读不出来：不在这里另立一套容器失败消息，交给 CreateReader 报「按 Xlsx 读不通」
        }
        finally
        {
            input.Position = 0;
        }
    }

    /// <summary>
    /// 建立读取器，容器读不通时换成框架声明过的类型
    /// </summary>
    /// <param name="input">输入流</param>
    /// <param name="configuration">读取器配置</param>
    /// <param name="isText">是否为文字档路径</param>
    /// <param name="format">本次要读的格式</param>
    /// <param name="header">档头字节，供消息使用</param>
    /// <exception cref="InvalidOperationException">格式给出或判出，但容器读不通</exception>
    private static IExcelDataReader CreateReader(
        Stream input,
        ExcelReaderConfiguration configuration,
        bool isText,
        ExcelImportFormat format,
        byte[] header)
    {
        try
        {
            return isText
                ? ExcelReaderFactory.CreateCsvReader(input, configuration)
                : ExcelReaderFactory.CreateReader(input, configuration);
        }
        catch (Exception ex) when (IsContainerFailure(ex))
        {
            throw ContainerFailure(ex, format, header);
        }
    }

    /// <summary>
    /// 取下一行，容器读到一半崩了也换成框架声明过的类型
    /// </summary>
    /// <param name="reader">读取器</param>
    /// <param name="format">本次要读的格式</param>
    /// <param name="header">档头字节，供消息使用</param>
    /// <exception cref="InvalidOperationException">容器读到一半崩了</exception>
    private static bool ReadNext(IExcelDataReader reader, ExcelImportFormat format, byte[] header)
    {
        try
        {
            return reader.Read();
        }
        catch (Exception ex) when (IsContainerFailure(ex))
        {
            throw ContainerFailure(ex, format, header);
        }
    }

    /// <summary>
    /// 跳到下一个工作表，容器读到一半崩了同样换成框架声明过的类型
    /// </summary>
    /// <param name="reader">读取器</param>
    /// <param name="format">本次要读的格式</param>
    /// <param name="header">档头字节，供消息使用</param>
    /// <exception cref="InvalidOperationException">容器读到一半崩了</exception>
    private static bool AdvanceResult(IExcelDataReader reader, ExcelImportFormat format, byte[] header)
    {
        try
        {
            return reader.NextResult();
        }
        catch (Exception ex) when (IsContainerFailure(ex))
        {
            throw ContainerFailure(ex, format, header);
        }
    }

    /// <summary>
    /// 判断异常属于不属于容器层的失败
    /// </summary>
    /// <param name="ex">待判断的异常</param>
    /// <remarks>
    /// 只认读取器自己的 <see cref="ExcelReaderException"/> 家族（伪造档头、坏目录、加密都从这三型里出来）与容器级的
    /// <see cref="InvalidDataException"/>。<u>绝不按 <see cref="ArgumentException"/> 收口</u>：
    /// <see cref="DecoderFallbackException"/> 是它的后代，那样收会把「编码指错」这条正当的解码失败
    /// 一起吞成容器异常，调用方再也看不出档是没解开还是读不通。
    /// </remarks>
    private static bool IsContainerFailure(Exception ex)
        => ex is ExcelReaderException or InvalidDataException;

    /// <summary>
    /// 把容器层失败换成 <see cref="InvalidOperationException"/>，库原话留在内部异常
    /// </summary>
    /// <param name="original">库或 BCL 交回的原始异常</param>
    /// <param name="format">本次要读的格式</param>
    /// <param name="header">档头字节</param>
    private static InvalidOperationException ContainerFailure(Exception original, ExcelImportFormat format, byte[] header)
        => new(
            $"按 {format} 读不通：档头 = {ExcelFormatProbe.DescribeHeader(header)}，内容不是可用的 {format} 容器" +
            "（伪造的档头、截断的档、加密或损坏的簿都会走到这里）。库报出「" + original.Message + "」，" +
            "原文留在内部异常里，定位容器问题要看它。",
            original);

    /// <summary>
    /// 跳到指定名称的工作表，找不到就抛并列出实际表名
    /// </summary>
    /// <param name="reader">已建立的读取器</param>
    /// <param name="sheetName">要读的工作表名</param>
    /// <param name="format">本次要读的格式</param>
    /// <param name="header">档头字节，供消息使用</param>
    /// <exception cref="InvalidOperationException">工作簿里没有这个名字的表，或容器读到一半崩了</exception>
    private static void SelectSheet(IExcelDataReader reader, string sheetName, ExcelImportFormat format, byte[] header)
    {
        var seen = new List<string>();

        while (true)
        {
            if (string.Equals(reader.Name, sheetName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            seen.Add(string.IsNullOrEmpty(reader.Name) ? "(未命名)" : reader.Name);

            if (!AdvanceResult(reader, format, header))
            {
                throw new InvalidOperationException(
                    $"工作簿里没有名为「{sheetName}」的工作表，实际有：{string.Join("、", seen)}。" +
                    "表名比对不区分大小写；一次导入只读一张表。");
            }
        }
    }

    /// <summary>
    /// 判一行的列数有没有超过导入列数上限
    /// </summary>
    /// <param name="fieldCount">本行的列数</param>
    /// <exception cref="InvalidOperationException">列数超过 <see cref="ExcelConstants.MaxImportColumns"/></exception>
    /// <remarks>
    /// 逐行判而不只在表头行判一次：分隔符档每行的列数可以不同，行变宽时补出 <c>Col{n}</c> 是既有语义，
    /// 因此「表头只有两列、第 900 行忽然十万列」这种档也要在补键之前被挡下。
    /// 超限时整份档拒收，不截断列清单、也不交出前若干列——半行数据交回的是看起来成功的错位结果。
    /// </remarks>
    private static void GuardColumnCount(int fieldCount)
    {
        if (fieldCount <= ExcelConstants.MaxImportColumns)
        {
            return;
        }

        throw new InvalidOperationException(
            $"这份档的一行有 {fieldCount} 列，超过导入列数上限 " +
            $"{nameof(ExcelConstants.MaxImportColumns)} = {ExcelConstants.MaxImportColumns} 列：" +
            "整份档拒收，不建键、也不交出任何一行。列数决定建键与每行取值的规模，" +
            "不设上界就是让一份档决定单次导入的内存与耗时；要读更宽的档请先在来源侧把列拆成多份。");
    }

    /// <summary>
    /// 一行表头换算出的键名清单，以及与它同步维护的判重集合
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判重集合必须与清单同生同长，因此收在一个类型里，不拆成调用方自己维持的两个局部变量：
    /// 只按清单线性扫（<c>List.Contains</c>）判重时，<c>n</c> 列的表头要扫 O(n²) 次，
    /// 一份十万列的档光是建键就要十几秒，而这段时间里取消令牌一次也不会被检查。
    /// 用 <see cref="HashSet{T}"/> 判重把它压回 O(n)。
    /// </para>
    /// <para>
    /// 比较器钉 <see cref="StringComparer.Ordinal"/>：键名是调用方按字面取值的标识，
    /// <c>"A"</c> 与 <c>"a"</c> 是两个键。换成大小写不敏感的比较器会把它们判成重名、给后一个加 <c>_2</c> 后缀，
    /// 同一份档在不同来源上取到不同键名，而这与 <see cref="ReadValues"/> 里取值字典用的比较器也会不一致。
    /// </para>
    /// </remarks>
    private sealed class HeaderKeySet
    {
        private readonly List<string> _keys;

        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private HeaderKeySet(int capacity)
        {
            _keys = new List<string>(capacity);
        }

        /// <summary>
        /// 键名清单，顺序就是列序
        /// </summary>
        internal IReadOnlyList<string> Keys => _keys;

        /// <summary>
        /// 把一行表头转成键名清单，重名加 <c>_n</c> 后缀（<c>n</c> 从 2 起）
        /// </summary>
        /// <param name="reader">停在表头行的读取器</param>
        /// <param name="trimHeaders">是否去掉表头文案的首尾空白</param>
        internal static HeaderKeySet BuildKeys(IExcelDataReader reader, bool trimHeaders)
        {
            var set = new HeaderKeySet(reader.FieldCount);

            for (var index = 0; index < reader.FieldCount; index++)
            {
                var raw = reader.GetValue(index);
                var text = ValueToHeaderKey(raw);

                if (trimHeaders)
                {
                    text = text.Trim();
                }

                // 空表头位用列序补名：表头缺字是常事，键名不能是空字串
                if (text.Length == 0)
                {
                    text = $"{PositionalKeyPrefix}{index + 1}";
                }

                set.Add(text);
            }

            return set;
        }

        /// <summary>
        /// 生成 <c>Col1</c>、<c>Col2</c>… 的位置键名
        /// </summary>
        /// <param name="fieldCount">本行的列数</param>
        internal static HeaderKeySet PositionalKeys(int fieldCount)
        {
            var set = new HeaderKeySet(fieldCount);

            for (var index = 0; index < fieldCount; index++)
            {
                set.Add($"{PositionalKeyPrefix}{index + 1}");
            }

            return set;
        }

        /// <summary>
        /// 行比现有键清单宽时补出位置键，窄时不动
        /// </summary>
        /// <param name="fieldCount">本行的列数</param>
        internal void ExtendKeys(int fieldCount)
        {
            for (var index = _keys.Count; index < fieldCount; index++)
            {
                Add($"{PositionalKeyPrefix}{index + 1}");
            }
        }

        /// <summary>
        /// 取一个不与已有键重名的键名，连同它一起记进清单与判重集合
        /// </summary>
        /// <param name="baseKey">候选键名</param>
        /// <remarks>
        /// 后缀从 <c>_2</c> 起，且要跳过「源档里本来就有 <c>重量_2</c>」这种撞名：撞了就继续加一号，
        /// 否则两份数据会落进同一个键、后写的盖掉先写的。
        /// </remarks>
        private void Add(string baseKey)
        {
            var key = UniqueKey(baseKey);

            _keys.Add(key);
            _seen.Add(key);
        }

        /// <summary>
        /// 在判重集合上取一个不重名的键名
        /// </summary>
        /// <param name="baseKey">候选键名</param>
        private string UniqueKey(string baseKey)
        {
            if (!_seen.Contains(baseKey))
            {
                return baseKey;
            }

            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{baseKey}_{suffix}";
                if (!_seen.Contains(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    /// <summary>
    /// 读本行的取值
    /// </summary>
    /// <param name="reader">停在数据行的读取器</param>
    /// <param name="keys">键清单</param>
    /// <param name="trimValues">是否去掉字串值的首尾空白</param>
    /// <remarks>
    /// 行比键清单窄时缺的列取 <c>null</c>，与合并单元格非左上角、空格子的形态一致；不做「当成空字串」的改写。
    /// </remarks>
    private static Dictionary<string, object?> ReadValues(
        IExcelDataReader reader,
        IReadOnlyList<string> keys,
        bool trimValues)
    {
        var values = new Dictionary<string, object?>(keys.Count, StringComparer.Ordinal);

        for (var index = 0; index < keys.Count; index++)
        {
            object? value = index < reader.FieldCount ? reader.GetValue(index) : null;

            if (trimValues && value is string text)
            {
                value = text.Trim();
            }

            values[keys[index]] = value;
        }

        return values;
    }

    /// <summary>
    /// 把表头格的值转成键名文本
    /// </summary>
    /// <param name="value">表头格的值，允许为 <c>null</c></param>
    /// <remarks>
    /// 数值表头（例如拿年份当列名）按不变文化转文本，不跟随当前区域设置改变小数点，
    /// 否则同一份档在不同机器上会得到不同键名。
    /// </remarks>
    private static string ValueToHeaderKey(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}
