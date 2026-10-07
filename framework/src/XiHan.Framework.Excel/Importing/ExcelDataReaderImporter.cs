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
/// 逐行交出 <see cref="ExcelImportRow"/>，不物化整档：行号、<see cref="ExcelImportOptions.MaxRowCount"/>
/// 与取消都落在行与行之间。
/// </para>
/// <para>
/// 文字档在交出第一行之前会把整档扫描一遍，以确定解码编码与整档的最大列数；
/// <see cref="ExcelImportOptions.MaxRowCount"/> 与取消令牌只在交出行阶段生效。
/// 这趟扫描只读不存，托管内存不随档的行数增长。
/// </para>
/// <para>
/// 流所有权在调用方：读取器一律以 <c>LeaveOpen = true</c> 建立，枚举结束后不关闭传入的流。
/// 读取总是从流的起点开始，建立读取器之前先把位置复位到 0。
/// </para>
/// <para>
/// 输入流必须可定位，不可定位的流在建立读取器之前即被拒绝。
/// </para>
/// <para>
/// <see cref="ExcelReaderConfiguration.TrimWhiteSpace"/> 显式置 <c>false</c>，首尾空白统一由
/// <see cref="ExcelImportOptions.TrimHeaders"/> 与 <see cref="ExcelImportOptions.TrimValues"/> 处理。
/// </para>
/// <para>
/// 文字档的解码编码见 <see cref="TextEncodingResolver"/>：
/// <list type="bullet">
/// <item>档带 BOM 时按 BOM 解码，优先于回退编码与 <see cref="ExcelImportOptions.TextEncodingName"/>。</item>
/// <item>无 BOM 且未指名编码时，读取器先按整档试解 UTF-8，失败才用严格 Big5 回退。</item>
/// <item>指名编码时，按指名编码把整档转码成无 BOM UTF-8 再交给读取器；转码结果整份放在内存里
/// （档大小上限为 <see cref="XiHanExcelOptions.MaxImportBytes"/>）。</item>
/// </list>
/// </para>
/// <para>
/// 回退 Big5 时，无法配对的字节解成私有区字符 <c>U+F8F8</c> 而不抛出；读到的字里出现 <c>U+F8F8</c>
/// 表示源档那一处字节已损坏。
/// </para>
/// <para>
/// 合并单元格除左上角外的格位读回 <c>null</c>，空格子在文字档里是空字串、在工作簿里是 <c>null</c>，
/// 公式只读回已缓存的值。样式、批注、图表与宏不解释。不支持加密工作簿。
/// </para>
/// <para>
/// 显式 <see cref="ExcelImportOptions.Format"/> 为 <see cref="ExcelImportFormat.Xls"/> 或
/// <see cref="ExcelImportFormat.Xlsx"/> 时不校验签章，读取器按内容自行选择容器解析器。
/// 容器读不通时，库的 <c>ExcelReaderException</c> 家族与 <see cref="InvalidDataException"/> 换成
/// <see cref="InvalidOperationException"/>，原异常留在内部异常里。
/// </para>
/// <para>
/// 对上 OLE 档头却短到读不出目录的档，BCL 抛出的 <see cref="ArgumentException"/> 不做转换，原样上抛。
/// </para>
/// <para>
/// 行数上限在构造时确定：无参构造用框架默认硬上限，收 <see cref="XiHanExcelOptions"/> 的重载用
/// <see cref="XiHanExcelOptions.MaxImportRows"/>。配置值越出框架硬上限或不是正整数时在构造点抛出。
/// </para>
/// <para>
/// 档的规模在建立读取器之前判两道界。其一是档大小：取 <see cref="XiHanExcelOptions.MaxImportBytes"/>
/// （无参构造用 <see cref="ExcelConstants.DefaultMaxImportBytes"/>），超限拒收整份档。其二只对 <c>xlsx</c> 生效：
/// 按 zip 中央目录的元数据判各部件解压后长度、解压后总长与解压比，任一越界即拒收，不解压任何部件。
/// 两道解压后长度上限不可配置，取值见 <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 一族的说明；
/// 解压比取 <see cref="XiHanExcelOptions.MaxImportCompressionRatio"/>
/// （无参构造用 <see cref="ExcelConstants.MaxImportCompressionRatio"/>），设成 <c>0</c> 或负数即不判比值。
/// </para>
/// <para>
/// 每一行的列数不得超过 <see cref="ExcelConstants.MaxImportColumns"/>，逐行判在建键之前，超限整份档拒收。
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
    private const long CompressionRatioFloorBytes = 1_048_576;

    private readonly int _hardMaxRows;

    private readonly long _maxImportBytes;

    private readonly int _maxImportCompressionRatio;

    /// <summary>
    /// 用框架默认导入行数硬上限构造读取器
    /// </summary>
    public ExcelDataReaderImporter()
    {
        _hardMaxRows = ImportSharedRules.ResolveHardMaxRows(null);
        _maxImportBytes = ImportSharedRules.ResolveMaxImportBytes(null);
        _maxImportCompressionRatio = ImportSharedRules.ResolveMaxImportCompressionRatio(null);
    }

    /// <summary>
    /// 用配置里的导入上限构造读取器
    /// </summary>
    /// <param name="options">Excel 选项，取 <see cref="XiHanExcelOptions.MaxImportRows"/>、
    /// <see cref="XiHanExcelOptions.MaxImportBytes"/> 与 <see cref="XiHanExcelOptions.MaxImportCompressionRatio"/> 三项</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="XiHanExcelOptions.MaxImportRows"/> 不是正整数，或高过框架硬上限
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/></exception>
    public ExcelDataReaderImporter(XiHanExcelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _hardMaxRows = ImportSharedRules.ResolveHardMaxRows(options);
        _maxImportBytes = ImportSharedRules.ResolveMaxImportBytes(options);
        _maxImportCompressionRatio = ImportSharedRules.ResolveMaxImportCompressionRatio(options);
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
    /// <item><c>xlsx</c> 容器的解压规模越界（单个部件解压后长度、解压后总长，或单个部件的解压比超过
    /// <see cref="XiHanExcelOptions.MaxImportCompressionRatio"/>）：消息点名越界的那个部件与越界的数字，
    /// 判在建立工作簿读取器之前，一个部件都不解压。解压比那条是启发式，该选项取 <c>0</c> 或负数即不判比值，
    /// 两道解压后长度的绝对上限不受它影响；</item>
    /// <item><see cref="ExcelImportOptions.Format"/> 为 <c>null</c> 且档头判不出格式：消息写出档头字节的可读形式
    /// 并点名 HTML 表格／XML 表格这类伪装；</item>
    /// <item>格式给出或判出但容器读不通（伪造的档头、截断的档、损坏的簿）：消息点名该格式、附同一份可读档头，
    /// 并把库原话留在内部异常；</item>
    /// <item>某一行的列数超过 <see cref="ExcelConstants.MaxImportColumns"/>：消息写出实际列数与该上限，
    /// 逐行判在建键之前，整份档拒收，不截断列清单也不交出前若干列；</item>
    /// <item>没人指名过行数上限（<see cref="ExcelImportOptions.MaxRowCount"/> 为 <c>null</c> 且生效的上限正是
    /// 框架硬上限 <see cref="ExcelConstants.DefaultMaxImportRows"/>），而档的数据行超过它、上限之后仍有数据行：
    /// 消息点名下限值，已经交出的行照旧交完，不静默少交行。指名过上限时不抛，按上限截断；</item>
    /// <item><see cref="ExcelImportOptions.SheetName"/> 在本工作簿里不存在：消息列出实际表名。</item>
    /// </list></exception>
    /// <exception cref="DecoderFallbackException">文字档的实际字节在所用编码下解不开：无 BOM 又未指名编码时来自
    /// UTF-8 试解之后的严格 Big5 回退，指名编码时来自指名编码的解码（在转码阶段就抛，读取器还没建立）。
    /// 解不开的字节不会被换成替换字符当正常数据交出，读档在中途停下</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 方法体在<u>首次取行</u>时才运行（异步迭代器），上面这些检查因此都在第一次
    /// <c>MoveNextAsync</c> 时才抛出；调用 <see cref="ReadAsync"/> 本身不会抛。
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

        // 上限校验排在格式判别与建立读取器之前
        var maxRows = ImportSharedRules.ResolveMaxRowCount(effective.MaxRowCount, _hardMaxRows);

        // 指名过上限（调用端或配置端）就截断，没指名而撞上框架硬上限就抛
        var throwsOnLimit = ImportSharedRules.ThrowsWhenRowLimitHit(effective.MaxRowCount, maxRows);

        cancellationToken.ThrowIfCancellationRequested();

        // 档大小判在格式判别之前
        ImportSharedRules.ValidateImportBytes(input.Length, _maxImportBytes);

        // 读取一律从流起点开始
        input.Position = 0;

        // 档头只取一次，判不出格式与容器读不通两条失败消息共用
        var header = ExcelFormatProbe.ReadHeader(input, ExcelFormatProbe.HeaderByteCount);

        var format = effective.Format ?? DetectFormatOrThrow(header);
        var isText = format is ExcelImportFormat.Csv or ExcelImportFormat.Txt;

        // 解压规模判在建立读取器之前
        GuardDecompressedSize(input, format, _maxImportCompressionRatio);

        // 建立读取器：文字路径的分隔符与编码在这里确定，二进制路径不读 TextEncodingName 与 Delimiter。
        // AnalyzeInitialCsvRows 保持默认 0：建立时扫完整档，最大列数与 UTF-8 试解都按整档判定。
        var configuration = new ExcelReaderConfiguration
        {
            LeaveOpen = true,
            TrimWhiteSpace = false
        };

        // 文字档的编码与是否转码见 TextEncodingResolver.ResolveForReader；
        // 转码那一支交出去的是新建的流，读完由本类释放
        var textSource = input;
        var ownsTextSource = false;

        if (isText)
        {
            var handoff = TextEncodingResolver.ResolveForReader(effective.TextEncodingName, input);

            textSource = handoff.Input;
            ownsTextSource = handoff.OwnsInput;
            configuration.FallbackEncoding = handoff.FallbackEncoding;
            configuration.AutodetectSeparators =
            [
                effective.Delimiter ?? (format == ExcelImportFormat.Csv ? CsvDefaultDelimiter : TxtDefaultDelimiter)
            ];
        }

        try
        {
            using var reader = CreateReader(textSource, configuration, isText, format, header);

            if (!isText && effective.SheetName is not null)
            {
                SelectSheet(reader, effective.SheetName, format, header);
            }

            var rowNumber = 0;
            var emitted = 0;
            var limitHit = false;
            HeaderKeySet? keys = null;

            while (ReadNext(reader, format, header))
            {
                rowNumber++;

                if (rowNumber % CancellationCheckIntervalRows == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // 列数上限判在建键之前
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

                // 未指名上限时，额外数据行触发框架硬上限异常。
                if (limitHit)
                {
                    throw ImportSharedRules.RowLimitExceeded(maxRows);
                }

                yield return new ExcelImportRow(rowNumber, values);

                emitted++;
                if (emitted >= maxRows)
                {
                    if (!throwsOnLimit)
                    {
                        // 指名行数上限时，交够即停止读取。
                        break;
                    }

                    limitHit = true;
                }
            }
        }
        finally
        {
            if (ownsTextSource)
            {
                textSource.Dispose();
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
    /// <param name="maxCompressionRatio">本次生效的解压比上限；<c>0</c> 或负数表示不判解压比</param>
    /// <exception cref="InvalidOperationException">
    /// 单个部件解压后长度超过 <see cref="ExcelConstants.MaxImportEntryDecompressedBytes"/>，
    /// 或解压后总长超过 <see cref="ExcelConstants.MaxImportDecompressedBytes"/>，
    /// 或某个部件的解压比超过 <paramref name="maxCompressionRatio"/>
    /// </exception>
    /// <remarks>
    /// <para>
    /// 只读 zip 的中央目录，不解压任何部件：<see cref="ZipArchiveEntry.Length"/> 与
    /// <see cref="ZipArchiveEntry.CompressedLength"/> 都取自目录，检查成本与档的内容规模无关。
    /// </para>
    /// <para>
    /// 本检查使用中央目录声明的长度，不验证实际解压内容。
    /// </para>
    /// <para>
    /// 只有解压比可以由 <paramref name="maxCompressionRatio"/> 关掉；两道绝对上限（单部件解压后长度、解压后总长）
    /// 不可配置，关掉解压比之后照常逐部件判。
    /// </para>
    /// <para>
    /// 以 <c>leaveOpen: true</c> 打开；无论判过与否，退出前一律把 <see cref="Stream.Position"/> 归零。
    /// </para>
    /// <para>
    /// 档头是 zip 签名却打不开目录（截断的档、伪造的档头）时不在这里报错，交给 <see cref="CreateReader"/> 报错。
    /// </para>
    /// </remarks>
    private static void GuardDecompressedSize(Stream input, ExcelImportFormat format, int maxCompressionRatio)
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
                // 单部件先判：声明长度在累加之前被挡掉，总和不会溢出
                if (entry.Length > ExcelConstants.MaxImportEntryDecompressedBytes)
                {
                    throw new InvalidOperationException(
                        $"xlsx 容器里的部件「{entry.FullName}」解压后有 {entry.Length} 字节，" +
                        $"超过单个部件的上限 {ExcelConstants.MaxImportEntryDecompressedBytes} 字节：整份档拒收，不建立工作簿读取器。" +
                        "工作簿读取器开簿时把共享字串整份载进内存，单个部件的解压后长度会按倍数换算成本进程的内存占用。");
                }

                total += entry.Length;

                if (total > ExcelConstants.MaxImportDecompressedBytes)
                {
                    throw new InvalidOperationException(
                        $"xlsx 容器解压后总长超过上限 {ExcelConstants.MaxImportDecompressedBytes} 字节：" +
                        $"累加到部件「{entry.FullName}」时已达 {total} 字节，整份档拒收，不建立工作簿读取器。" +
                        "档的字节数说的是压缩后的大小，读它要付的内存与 I/O 由解压后的规模决定。");
                }

                // maxCompressionRatio <= 0 表示不判解压比
                if (maxCompressionRatio > 0 &&
                    entry.Length >= CompressionRatioFloorBytes &&
                    entry.CompressedLength > 0 &&
                    entry.Length / entry.CompressedLength > maxCompressionRatio)
                {
                    throw new InvalidOperationException(
                        $"xlsx 容器里的部件「{entry.FullName}」解压比过高：压缩后 {entry.CompressedLength} 字节，" +
                        $"解压后 {entry.Length} 字节，是 {entry.Length / entry.CompressedLength} 倍，" +
                        $"超过上限 {maxCompressionRatio} 倍：整份档拒收，不建立工作簿读取器。" +
                        "压缩后很小、展开后很大的部件，读它要付的内存与压缩后的体积不成比例，" +
                        "而工作簿读取器把这份内存花在读出第一行之前。" +
                        $"这道判据是启发式：比值高低也由产出这份档的工具决定，高度重复而合法的档可能越过它，" +
                        $"那种档请调高 {nameof(XiHanExcelOptions.MaxImportCompressionRatio)}，或把它设成 0 关掉本判据" +
                        "（两道解压后长度的绝对上限不受该选项影响，照常生效）。");
                }
            }
        }
        catch (InvalidDataException)
        {
            // 档头是 zip 签名但目录读不出来：交给 CreateReader 报错
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
    /// 只认读取器的 <see cref="ExcelReaderException"/> 家族与容器级的 <see cref="InvalidDataException"/>；
    /// 不认 <see cref="ArgumentException"/>，因此 <see cref="DecoderFallbackException"/> 原样上抛。
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
    /// 逐行判，行变宽补出 <c>Col{n}</c> 之前也判。超限时整份档拒收，不截断列清单，也不交出前若干列。
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
    /// 判重集合与键名清单同步维护，判重用 <see cref="HashSet{T}"/>。
    /// </para>
    /// <para>
    /// 比较器为 <see cref="StringComparer.Ordinal"/>，与 <see cref="ReadValues"/> 里的取值字典一致：
    /// <c>"A"</c> 与 <c>"a"</c> 是两个键。
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

                // 空表头位用列序补名
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
        /// 后缀从 <c>_2</c> 起，与已有键（含源档本来就有的 <c>重量_2</c> 这类）撞名时继续加一号。
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
    /// 行比键清单窄时缺的列取 <c>null</c>。
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
    /// 数值表头（例如拿年份当列名）按不变文化转文本，不跟随当前区域设置。
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
