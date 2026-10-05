// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions;

/// <summary>
/// Excel 导入导出常量定义
/// </summary>
/// <remarks>
/// 这里是框架侧的默认值与固定字面量，供选项默认值和各个提供程序共用，不随配置变化。
/// </remarks>
public static class ExcelConstants
{
    /// <summary>
    /// 默认流式写出阈值（行数），达到该规模改用流式导出
    /// </summary>
    public const int DefaultStreamingThreshold = 50_000;

    /// <summary>
    /// 默认自适应列宽采样行数
    /// </summary>
    public const int DefaultAutoWidthSampleRows = 500;

    /// <summary>
    /// 默认文本编码名称
    /// </summary>
    public const string DefaultEncodingName = "utf-8-bom";

    /// <summary>
    /// 默认导入行数硬上限
    /// </summary>
    public const int DefaultMaxImportRows = 1_000_000;

    /// <summary>
    /// 默认导入档大小上限（字节），即 256 MiB
    /// </summary>
    /// <remarks>
    /// 这是「一份外来档最多能有多大」的框架默认值，应用可以用 <see cref="XiHanExcelOptions.MaxImportBytes"/>
    /// 收紧或放宽。取 256 MiB 的依据是框架自己的行数上限：<see cref="DefaultMaxImportRows"/> 行、
    /// 每行若干列互不相同的文字，写成 xlsx 在百 MiB 量级，本值留出成倍余量，因此行数上限之内的正常档
    /// 不会被它挡住。两条导入路径都在读第一个字节之前按它判，超限<u>拒收整份档</u>，
    /// 不截断读取、也不「先读前面一段」——半份档交回的是看起来成功的数据损失。
    /// </remarks>
    public const long DefaultMaxImportBytes = 268_435_456;

    /// <summary>
    /// xlsx 容器解压后总长上限（字节），即 2 GiB
    /// </summary>
    /// <remarks>
    /// xlsx 是 zip 容器，档的大小说的是<u>压缩后</u>的字节数，读它要付出的内存与 I/O 由解压后的规模决定，
    /// 两者可以差出几百倍。本常量给「一个容器最多能展开成多少字节」定一道绝对界，与
    /// <see cref="MaxImportCompressionRatio"/> 配合使用：比值界挡住「小档展开成巨量内容」，
    /// 本界挡住「大档展开成更大量内容」，两者缺一都留口子。超限时导入器<u>拒收整份档</u>并点名解压后总长，
    /// 不解压、也不交给工作簿读取器。
    /// <para>
    /// <b>本常量界定的是解压后的<u>字节数</u>，不是本进程的托管占用。</b>解压后的内容进工作簿读取器还要
    /// 按字符与解析结构再展开一遍，实测一份解压后 387.6 MiB 的共享字串部件换来约 777.2 MiB 托管占用
    /// （约 2 倍；倍数不是常数，随部件内容与读取器的缓冲方式变动）。按本值与
    /// <see cref="MaxImportEntryDecompressedBytes"/> 当前的取值，最坏情形的托管占用是 <u>GiB 量级</u>而不是
    /// MiB 量级：这两道界挡的是「一份外来档把本进程的内存放大到不可收拾」，不是「导入最多只占几十兆」。
    /// 部署侧要按更低的内存预算工作，请用 <see cref="XiHanExcelOptions.MaxImportBytes"/> 把压缩后的档大小
    /// 收得更紧，或调低 <see cref="XiHanExcelOptions.MaxImportRows"/>。
    /// </para>
    /// </remarks>
    public const long MaxImportDecompressedBytes = 2_147_483_648;

    /// <summary>
    /// xlsx 容器里单个部件解压后长度上限（字节），即 1 GiB
    /// </summary>
    /// <remarks>
    /// 总长之外再给单个部件一道界：工作簿读取器把 <c>xl/sharedStrings.xml</c> 整份载入内存，
    /// 那一个部件的长度就直接换算成托管堆占用，而工作表部件是流式读过的，两者的内存代价并不相同。
    /// 本常量按「行数上限之内的正常工作簿里最大的那个部件」留出成倍余量取值，超限时导入器<u>拒收整份档</u>
    /// 并点名是哪个部件、它解压后有多长。
    /// <para>
    /// 「直接换算成托管堆占用」说的是<u>量级</u>而不是一比一：实测解压后 387.6 MiB 的共享字串部件
    /// 换来约 777.2 MiB 托管占用，约 2 倍，倍数随内容与读取器的缓冲方式变动。因此 1 GiB 这道界
    /// 意味着最坏情形下单个部件就能换来 GiB 量级的托管占用，取值细节见
    /// <see cref="MaxImportDecompressedBytes"/> 的说明。
    /// </para>
    /// </remarks>
    public const long MaxImportEntryDecompressedBytes = 1_073_741_824;

    /// <summary>
    /// xlsx 容器里单个部件解压比上限的默认值（解压后长度 ÷ 压缩后长度的倍数）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本判据认的是一种<u>形态</u>：压缩后很小、展开后很大的部件，读它要付的内存与它压缩后的体积不成比例，
    /// 而工作簿读取器把这份内存花在读出第一行<u>之前</u>，行数上限与取消令牌都拦不住它。
    /// <b>比值高本身推不出「这份档有问题」</b>——同一个比值可以由内容的重复度造成，也可以由产出这份档的
    /// 工具怎么写造成，两者在档的字节上分不开。因此本值是一个<u>可调的档位</u>，不是对档内容的事实判断；
    /// 超过它时导入器<u>拒收整份档</u>并点名该部件、它的解压比与生效中的上限。
    /// </para>
    /// <para>
    /// <b>本常量是默认值，不是不可动的界</b>：生效值取 <see cref="XiHanExcelOptions.MaxImportCompressionRatio"/>，
    /// 未接配置时用本常量。解压比是<u>启发式</u>——比值高低不只由内容决定，也由产出这份档的工具决定
    /// （工作表里 <c>row</c>／<c>c</c> 的 <c>r</c> 属性在规格上可选，不写 <c>r</c> 的产出者会让整段
    /// <c>sheetData</c> 逐字节重复，同样的数据比值可以差一个数量级），因此高度重复而完全合法的档
    /// 可能越过本值。该选项取 <c>0</c> 或负数表示不判解压比。
    /// </para>
    /// <para>
    /// 只对解压后长度不小于 1 MiB 的部件判：更小的部件即使比值难看，展开后也占不了多少内存，
    /// 而它们的总量另有 <see cref="MaxImportDecompressedBytes"/> 兜住。界定<u>解压后字节数</u>的是
    /// <see cref="MaxImportEntryDecompressedBytes"/> 与 <see cref="MaxImportDecompressedBytes"/>
    /// 两道绝对上限，它们不可配置，也不因关掉解压比而失效；换成托管占用还要再乘一个倍数，
    /// 换算关系与量级见 <see cref="MaxImportDecompressedBytes"/> 的说明。
    /// </para>
    /// </remarks>
    public const int MaxImportCompressionRatio = 100;

    /// <summary>
    /// 一个单元格能承载的字符数上限
    /// </summary>
    /// <remarks>
    /// 这是 xlsx 自身的硬界，不是本组件自定的档位：超出的字串在工作簿里没有对应形态。两条 xlsx 写出路径
    /// 都在取值阶段拒掉超长字串并回报本常量，不截断（截断会丢弃数据）、也不交给工作簿去抛它那句英文异常。
    /// 全量工作簿路径还用它做声明级预检：<see cref="Exporting.ExcelColumn.Header"/> 与
    /// <see cref="Exporting.ExcelSheetSpec.Title"/> 落的也是单元格，超长时在写出第一格之前就拒，
    /// 一个行元素都不取。
    /// </remarks>
    public const int MaxCellTextLength = 32_767;

    /// <summary>
    /// xlsx 单张工作表的行数上限（标题行与表头行都算在内）
    /// </summary>
    /// <remarks>
    /// 这是 xlsx 自身的硬界，不是本组件自定的档位：一张工作表只有这么多行，再往下的行在档里没有可落的位置。
    /// 两条 xlsx 写出路径都在既有的逐行循环里按「这一行数据要落到工作表的第几行」比对，超出即<u>抛出</u>，
    /// 不丢行、不截断，也不接着写出一份读不回来的档。上限按<u>单张工作表</u>各自计——多表路径每张表都有自己的
    /// 标题行与表头行，各自数各自的，不做整簿累计，因此两张各占六成的表能同时写进一个工作簿。
    /// 这道界与 <see cref="MaxFixedRowWidthBytes"/> 数值相同，但一个数的是行、一个数的是单行字节，
    /// 两道界互不相干，不得互相代用。文字档路径没有这道上限。
    /// </remarks>
    public const int MaxSheetRows = 1_048_576;

    /// <summary>
    /// 导入时单行列数的硬上限
    /// </summary>
    /// <remarks>
    /// 取 xlsx 自身的列上限（16,384 列，最后一列是 <c>XFD</c>）：正常档到不了这道界，越过它的通常是
    /// 从别处拼出来的分隔符档或手工构造的容器。列数决定建键与每行取值的规模——每一列都要一个键名与
    /// 一个字典项，不设上界就是让一份档决定单次导入的内存与耗时，而建键那段时间里取消令牌一次也不会被检查。
    /// 容器导入器在<u>建键之前</u>逐行判 <c>FieldCount</c>，超限即抛出并点名实际列数：不截断列清单、
    /// 也不交出前若干列，半行数据交回的是看起来成功的错位结果。固定宽度路径没有这道界——
    /// 那里的列数来自调用方给的列定义而不是档，单行占用另有 <see cref="MaxFixedRowWidthBytes"/> 兜住。
    /// </remarks>
    public const int MaxImportColumns = 16_384;

    /// <summary>
    /// 本组件对 xlsx 数值格承诺能原样落格的有效数字位数上限
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是本组件自定的取值域档位，判据只有一份：<see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>、
    /// <see cref="double"/>、<see cref="float"/> 五类型的取值，其不变文化文本形态的有效数字多于本常量时，
    /// 两条 xlsx 写出路径<u>一律抛出</u>，不写入、不降级，也不把值改写成较短的数。
    /// 之所以按「有效数字」而不是「双精度能不能装下」判：整数值 2^53 以内双精度装得下，
    /// 但落进档里的位数由写出库自己的格式决定，两条路径对同一取值给出的结果并不相同，
    /// 因此本组件把承诺收到一条两条路径都判得出的界上。
    /// </para>
    /// <para>
    /// <see cref="float"/> 不在这五类型的最初清单里，是本组件按同一判据扩用进来的：单精度值落数值格时先展开成
    /// <see cref="double"/>，展开后的形态多于本常量即拒（<c>0.1f</c> 就属于这一类）。
    /// 需要完整精度的值由呼叫端转成字符串栏位——字符串格保住逐字内容，数值格保住本常量以内的有效数字，
    /// 两者不互相代打。
    /// </para>
    /// </remarks>
    public const int MaxExactNumericSignificantDigits = 15;

    /// <summary>
    /// 固定宽度导入的单行列宽总和硬上限（字节）
    /// </summary>
    /// <remarks>
    /// 定宽档一行要先整行落进缓冲才能按字节切列，不设上界就等于让某一份档的一行决定本进程的内存占用，
    /// 与「默认实现不得无界增长」冲突。取 1 MiB：常见定长记录档一行不超过数 KB，这道界挡的是列宽配错
    /// （或恶意写宽）的请求，不影响正常档。列宽总和超过它时导入器<u>抛出</u>该上限与实际总和，
    /// 不夹改成较小的宽度、也不截断列清单——静默改写列布局会交回一份列位错开的档。
    /// </remarks>
    public const int MaxFixedRowWidthBytes = 1_048_576;

    /// <summary>
    /// CSV 内容类型
    /// </summary>
    public const string CsvContentType = "text/csv";

    /// <summary>
    /// 纯文本内容类型
    /// </summary>
    public const string PlainTextContentType = "text/plain";

    /// <summary>
    /// xlsx 内容类型
    /// </summary>
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// CSV 文件扩展名
    /// </summary>
    public const string ExtensionCsv = ".csv";

    /// <summary>
    /// 纯文本文件扩展名
    /// </summary>
    public const string ExtensionTxt = ".txt";

    /// <summary>
    /// xlsx 文件扩展名
    /// </summary>
    public const string ExtensionXlsx = ".xlsx";
}
