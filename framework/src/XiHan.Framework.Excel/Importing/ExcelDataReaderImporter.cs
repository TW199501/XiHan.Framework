// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ExcelDataReader;
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
/// <see cref="ExcelReaderConfiguration.Password"/>），容器解不开时按库的异常形态交回。
/// </para>
/// <para>
/// 显式 <see cref="ExcelImportOptions.Format"/> 为 <see cref="ExcelImportFormat.Xls"/> 或
/// <see cref="ExcelImportFormat.Xlsx"/> 时<u>不强判签章</u>：读取器按内容自行选容器解析器，因此档名与内容不符可以正常读；
/// 内容是垃圾字节或截断容器时，抛的是库或 BCL 的异常（<c>HeaderException</c>、<see cref="InvalidDataException"/>、
/// <see cref="ArgumentException"/>），本类不翻译。只有自动判别路径的「判不出来」由本类换成
/// <see cref="InvalidOperationException"/>。
/// </para>
/// </remarks>
public sealed class ExcelDataReaderImporter : IExcelImporter
{
    /// <summary>
    /// 每隔多少行检查一次取消令牌
    /// </summary>
    private const int CancellationCheckIntervalRows = 64;

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
    /// <see cref="ExcelImportOptions.MaxRowCount"/> 高于框架硬上限或不是正整数（<c>ParamName</c> 为 <c>MaxRowCount</c>）</exception>
    /// <exception cref="InvalidOperationException"><see cref="ExcelImportOptions.Format"/> 为 <c>null</c> 且档头判不出格式
    /// （消息写出档头字节的可读形式并点名 HTML 表格／XML 表格这类伪装），或
    /// <see cref="ExcelImportOptions.SheetName"/> 在本工作簿里不存在（消息列出实际表名）</exception>
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
        var maxRows = ResolveMaxRowCount(effective.MaxRowCount);

        cancellationToken.ThrowIfCancellationRequested();

        // 读取一律从流起点开始：容器解析会复位到起点，嗅探也照同一个起点，
        // 否则「调用方把位置留在中间」时嗅探判的是中间那段，而解析器读的是整档，两者会各说各话。
        input.Position = 0;

        var format = effective.Format ?? DetectFormatOrThrow(input);
        var isText = format is ExcelImportFormat.Csv or ExcelImportFormat.Txt;

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

        using var reader = isText
            ? ExcelReaderFactory.CreateCsvReader(input, configuration)
            : ExcelReaderFactory.CreateReader(input, configuration);

        if (!isText && effective.SheetName is not null)
        {
            SelectSheet(reader, effective.SheetName);
        }

        var rowNumber = 0;
        var emitted = 0;
        List<string>? keys = null;

        while (reader.Read())
        {
            rowNumber++;

            if (rowNumber % CancellationCheckIntervalRows == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 前导行按行号丢掉，不参与表头与取值
            if (rowNumber <= effective.HeaderRowIndex)
            {
                continue;
            }

            if (keys is null)
            {
                if (effective.HasHeader)
                {
                    keys = BuildKeys(reader, effective.TrimHeaders);
                    continue;
                }

                keys = PositionalKeys(reader.FieldCount);
            }

            // 行比表头宽时补出 Col{n}，多出来的列保留而不丢弃
            ExtendKeys(keys, reader.FieldCount);

            var values = ReadValues(reader, keys, effective.TrimValues);

            if (effective.SkipEmptyRows && IsEmptyRow(values))
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
    /// 把行数上限收敛到框架硬上限之内
    /// </summary>
    /// <param name="requested">调用方给的上限，<c>null</c> 表示用框架硬上限</param>
    /// <remarks>
    /// 硬上限取 <see cref="ExcelConstants.DefaultMaxImportRows"/> 常量：<see cref="XiHanExcelOptions.MaxImportRows"/>
    /// 这条配置面要在分派器落地后才接得进来，本类按无参构造使用，拿不到裸选项对象。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">上限高于硬上限，或不是正整数</exception>
    private static int ResolveMaxRowCount(int? requested)
    {
        if (requested is null)
        {
            return ExcelConstants.DefaultMaxImportRows;
        }

        var value = requested.Value;

        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExcelImportOptions.MaxRowCount),
                value,
                "MaxRowCount 必须是正整数；要按框架默认上限读请传 null，不要写 0。");
        }

        if (value > ExcelConstants.DefaultMaxImportRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExcelImportOptions.MaxRowCount),
                value,
                $"MaxRowCount 不能高于框架硬上限 {ExcelConstants.DefaultMaxImportRows} 行：" +
                $"这道上限挡住的是「调用方把内存里的行数放大到无穷」，应用只能收紧、不能放宽。");
        }

        return value;
    }

    /// <summary>
    /// 自动判别格式，判不出来时抛出可定位的说明
    /// </summary>
    /// <param name="input">输入流</param>
    /// <exception cref="InvalidOperationException">档头不是已知签名</exception>
    private static ExcelImportFormat DetectFormatOrThrow(Stream input)
    {
        var header = ExcelFormatProbe.ReadHeader(input, ExcelFormatProbe.HeaderByteCount);
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
    /// 跳到指定名称的工作表，找不到就抛并列出实际表名
    /// </summary>
    /// <param name="reader">已建立的读取器</param>
    /// <param name="sheetName">要读的工作表名</param>
    /// <exception cref="InvalidOperationException">工作簿里没有这个名字的表</exception>
    private static void SelectSheet(IExcelDataReader reader, string sheetName)
    {
        var seen = new List<string>();

        while (true)
        {
            if (string.Equals(reader.Name, sheetName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            seen.Add(string.IsNullOrEmpty(reader.Name) ? "(未命名)" : reader.Name);

            if (!reader.NextResult())
            {
                throw new InvalidOperationException(
                    $"工作簿里没有名为「{sheetName}」的工作表，实际有：{string.Join("、", seen)}。" +
                    "表名比对不区分大小写；一次导入只读一张表。");
            }
        }
    }

    /// <summary>
    /// 把一行表头转成键名清单，重名加 <c>_n</c> 后缀（<c>n</c> 从 2 起）
    /// </summary>
    /// <param name="reader">停在表头行的读取器</param>
    /// <param name="trimHeaders">是否去掉表头文案的首尾空白</param>
    private static List<string> BuildKeys(IExcelDataReader reader, bool trimHeaders)
    {
        var keys = new List<string>(reader.FieldCount);

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

            keys.Add(UniqueKey(keys, text));
        }

        return keys;
    }

    /// <summary>
    /// 生成 <c>Col1</c>、<c>Col2</c>… 的位置键名
    /// </summary>
    /// <param name="fieldCount">本行的列数</param>
    private static List<string> PositionalKeys(int fieldCount)
    {
        var keys = new List<string>(fieldCount);

        for (var index = 0; index < fieldCount; index++)
        {
            keys.Add($"{PositionalKeyPrefix}{index + 1}");
        }

        return keys;
    }

    /// <summary>
    /// 行比现有键清单宽时补出位置键，窄时不动
    /// </summary>
    /// <param name="keys">现有键清单</param>
    /// <param name="fieldCount">本行的列数</param>
    private static void ExtendKeys(List<string> keys, int fieldCount)
    {
        for (var index = keys.Count; index < fieldCount; index++)
        {
            keys.Add(UniqueKey(keys, $"{PositionalKeyPrefix}{index + 1}"));
        }
    }

    /// <summary>
    /// 取一个不与已有键重名的键名
    /// </summary>
    /// <param name="keys">已有键名</param>
    /// <param name="baseKey">候选键名</param>
    /// <remarks>
    /// 后缀从 <c>_2</c> 起，且要跳过「源档里本来就有 <c>重量_2</c>」这种撞名：撞了就继续加一号，
    /// 否则两份数据会落进同一个键、后写的盖掉先写的。
    /// </remarks>
    private static string UniqueKey(IReadOnlyList<string> keys, string baseKey)
    {
        if (!keys.Contains(baseKey))
        {
            return baseKey;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseKey}_{suffix}";
            if (!keys.Contains(candidate))
            {
                return candidate;
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
    /// 判断整行皆空
    /// </summary>
    /// <param name="values">本行取值</param>
    /// <remarks>
    /// 「空」只看 <c>null</c> 与空字串：<see cref="ExcelImportOptions.TrimValues"/> 为 <c>true</c> 时全空格行
    /// 已经剥成空字串，因此也算空行；为 <c>false</c> 时 <c>" "</c> 是实数据，不算空行。
    /// </remarks>
    private static bool IsEmptyRow(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var value in values.Values)
        {
            if (value is not null && value is not "")
            {
                return false;
            }
        }

        return true;
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
