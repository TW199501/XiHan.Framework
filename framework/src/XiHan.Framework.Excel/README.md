# XiHan.Framework.Excel

## 概述
XiHan.Framework.Excel 是 Excel 导入导出的实现包，落地 `XiHan.Framework.Excel.Abstractions` 定义的三个契约：导出 `IExcelExporter`、模板渲染 `IExcelTemplateRenderer`、导入 `IExcelImporter`。

导出侧按目标格式分派到三条写出路径（ClosedXML 全量工作簿、MiniExcel 流式工作簿、文字档），文字档内部再按布局分分隔符与固定宽度两条；导入侧按「有没有列定义」分派到容器／分隔符读取（ExcelDataReader）与固定宽度读取两条，全程惰性逐行，不物化整档。

配置节为 `XiHan:Excel`，模块类为 `XiHanExcelModule`。

## 核心能力
- **导出分派**：`ExcelExporter` 只判分派输入、选路径、原样带出结果，守卫一律长在各自写出器里，不抄第二份
- **xlsx 两条路径**：`ClosedXmlExporter` 全量建簿并承载全部排版；`MiniExcelStreamExporter` 边生成边写，只保留表头文案、列顺序、数字与日期格式串、表头冻结与筛选开关
- **文字档两条布局**：分隔符布局（引号策略、公式注入防护、改写留痕）与固定宽度布局（按字节补位、超宽抛出或截断）
- **模板渲染**：`MiniExcelTemplateRenderer` 按现成 xlsx 模板的 `{{键名}}` 占位符填数据，版面归模板档
- **导入**：`ExcelImporter` 门面 + `ExcelDataReaderImporter`（`.xls`／`.xlsx`／`.csv`／`.txt`）+ `FixedWidthTextImporter`（定长记录档），签章判格式、BOM 与编码自动判别、表头与前导行处理、空行跳过、行数上限
- **属性驱动的列构建**：`ExcelColumnBuilder.CreateColumns<TRow>()` 按公共实例属性与 `[ExcelColumn]` / `[ExcelIgnore]` 生成列清单并按行类型缓存

能力边界（三条限制，与抽象包同一口径）：
- **不导出 `.xls`**：导出只有 `ExcelFormat.Xlsx`／`Csv`／`Txt`，`ExportAsync` 对其它取值抛 `ArgumentOutOfRangeException`；也不承诺 `.xlsm` 的宏、数据透视表与图表。`.xls` 作为输入可读，但该读取路径的端到端行为在本组件中尚未经验证，接入前请用真实 `.xls` 档自行验证
- **流式模式丢样式**：xlsx 走流式时标题行与合并、表头加粗与底色、边框、逐格条件样式、列宽（含自适应列宽）、水平对齐与自动换行全部不写出，返回值恒为降级结果并逐项说明；文字档没有样式概念，写它不算降级
- **固定宽度按字节判定**：`ExcelColumn.FixedWidth` 与导入侧 `ExcelFixedWidthField.WidthBytes` 都是目标编码下的字节数，宽度必须与写档编码一致，否则从第二列起整体错位

## 依赖关系
- 依赖 `XiHan.Framework.Excel.Abstractions`（契约与模型）与 `XiHan.Framework.Core`（模块化与依赖注入基础）
- 模块类 `XiHanExcelModule` 在 `ConfigureServices` 中调用 `services.AddXiHanExcel(config)`；`Core` 自身没有模块类，因此本模块不写 `[DependsOn]`
- 第三方库：`ClosedXML`（全量工作簿）、`MiniExcel`（流式工作簿与模板渲染）、`ExcelDataReader`（多来源读取）
- 依赖方向由 `XiHan.Framework.Architecture.Tests` 按解决方案目录校验，本包属基础设施层

## 配置与约定
配置节 `XiHan:Excel`（`XiHanExcelOptions.SectionName`）四项：

| 选项 | 默认值 | 口径 |
| --- | --- | --- |
| `StreamingThreshold` | `50000` | xlsx 在 `ForceStreaming` 未表态时按 `ExpectedRowCount >= 阈值` 走流式。取 `0` 或负数等于「任何非负行数都达到阈值」，也就是一律走流式，这是合法表达，不当非法值拒掉；表规格显式表态 `true`／`false` 时本值不参与判定 |
| `AutoWidthSampleRows` | `500` | 全量工作簿的自适应列宽取样行数；`0` 表示只看表头行，负数抛出 |
| `DefaultEncodingName` | `"utf-8-bom"` | 编码名称常量在选项上的镜像默认值。当前实现不读这一项决定文字档编码：写出取 `ExcelTextOptions.EncodingName`（初始值即同名常量），导入取 `ExcelImportOptions.TextEncodingName`（默认 `null` 即自动判别） |
| `MaxImportRows` | `1000000` | 应用可用的导入行数上限，必须是正整数且不高于框架绝对上界 `ExcelConstants.DefaultMaxImportRows`（同为 1_000_000）；只能收紧不能放宽，配错在解析服务时抛出，不夹回上界。两条导入路径在构造时各取一次该值，配置经构造重载接入并在 DI 里生效 |

导出侧约定：
- **分派要求表态**：目标格式为 xlsx 而 `ForceStreaming` 与 `ExpectedRowCount` 两个都没给时抛 `InvalidOperationException`，不猜一个——猜过去会连带静默丢样式。`ExpectedRowCount < 0` 在**两个入口、所有目标格式**都被拒（`ArgumentOutOfRangeException`，`ParamName` 为 `ExpectedRowCount`，多表消息点名「第 N 张表」）；多表入口 `ExportAllAsync` 恒走全量工作簿，这个值在那条入口只校验成立与否、不参与分流；清单里任何一张表 `ForceStreaming = true` 即整个请求被拒，不做偷偷改走全量的改写。
- **一列都没有的表在路由之前被拒**：`Columns` 为 `null` 或空清单都抛 `ArgumentException`（`ParamName` 为 `Columns`），因为三条路径对这种输入各交回一种半成品。
- **列宽值域两处不一致，要知道落差**：`ExcelColumn.Width` 只接受大于 0 且不高于 255 的有限数，`<= 0`、`NaN`／无穷与 `> 255` 在 xlsx 全量导出时都是同一个 `ArgumentOutOfRangeException`；而 `[ExcelColumn(Width = ...)]` 允许填 `400`，构建器只在构建时拒负数与非有限值——特性侧填超宽要等到导出期才撞上守卫。`Width = 0` 在特性上表示「未指定／自动列宽」（特性命名参数不支持可空数值类型），由构建器映射为列上的 `null`。
- **`[ExcelColumn]` 不支持固定宽度布局**：特性不带字节宽度，`Layout = FixedWidth` 时每列必须由调用方构造 `ExcelColumn<TRow>` 并给出 `FixedWidth`，缺这道设置在写出任何字节之前抛 `InvalidOperationException` 并列出缺宽度的列键。构建器回写进 `ExcelColumn.Order` 的是**排定后的位置序号**，不是特性上声明的顺序值，不要拿它回读。
- **`NumberFormat` 只落在数值格与日期格**：它是 Excel 格式串，与 .NET 格式串不通用，文字路径不套用（文字档取值转文本用的是 `ExcelColumn.TextFormat`，它优先于 `NumberFormat`）——文字档与 xlsx 档同一列显示可以不同，这是刻意设计。
- **表名与颜色只在承载它们的路径判**：表名判据（31 个字符按 UTF-16 代码单元、不含 `: \ / ? * [ ]` 与 `U+0000`／`U+0003`、不以单引号开头或结尾、多表判重不区分大小写）两条 xlsx 路径共用；`HeaderFill` 不是合法十六进制颜色串只在全量工作簿路径抛，流式模式与文字档不承载该项，设置了也不报错。
- **`Title` 与 `SheetName` 的空白口径不同**：`Title` 仅含空白时按「未填」处理，不写标题行也不抛；`SheetName` 为 `null` 或空白在 `ExcelSheetSpec` 的 `init` 直接抛 `ArgumentException`。
- **行型逐笔判定**：行集合每一笔都按 `ExcelSheetSpec.RowType` 比对（`Type.IsInstanceOfType`，派生类型放行），不符抛 `InvalidOperationException` 并点名行位置与期望／实际类型全名；行集合始终只枚举一次，不物化。`RowType` 为 `null` 属非法声明，在写出第一格之前就被拒。
- **日期下限两条 xlsx 路径判得不对称**：早于 1899-12-30 的 `DateTime` 两条路径都拒；早于 1899-12-30 的 `DateOnly` 与 `DateTimeOffset` **只在流式路径拒**——这两个型别在流式路径落成日期格，在全量路径落成文本格、原值（含偏移量）留在文字里。不对称来自落格方式不同，不是两套标准。判据说的是政策：本组件不依赖写出库与表格软件各自的宽容度，早于该时刻的整段一起拒，不按取值远近划分哪一段安全。要保住这类日期就走全量路径，或让该列取成文本。
- **其它取值域**：非有限的 `double`／`float` 与长过 `ExcelConstants.MaxCellTextLength`（32_767 个字符）的字串在两条 xlsx 路径都抛 `InvalidOperationException`，不夹改也不截断。
- **文字档的非法选项组合在写出任何字节之前被拒**：`Delimiter = ' '` 配 `Quote = None` 抛 `ArgumentException`；`Delimiter` 取 `\r`、`\n` 或 `"` 同样被预写拒（`ParamName` 为 `textOptions`）。判据是「写出去就读不回来」，不是「不推荐」。
- **固定宽度布局下 `Delimiter`、`Quote`、`EscapeFormulaPrefix` 三项一律无效**：设置了不报错也不参与写出（该布局按字节位置切列，没有对应的解析位）；该布局同时**不接受值内换行**，取到含 `\r`／`\n` 的那行即抛，因为行集合是惰性游标，抛出时前面的行可能已落盘。补位字符 `PadChar` 必须是单字节字符且不得为 `\r`／`\n`，这一条与内容无关，排在写第一个字节之前判。
- **截断落在 .NET `StringInfo` 的文本元素边界**：超宽截断以文本元素为最小取舍单位，不会把一个多字节字符切成半个字节序列、也不留代理对的前半边；代价是结果可能比列宽少若干字节，差额由补位字符填满。这不是字素簇承诺，emoji 家族（ZWJ 序列、区域指示符）不保证原样往返。
- **编码不可映射字符抛而不降级**：目标编码收不下待写出的字符时抛 `EncoderFallbackException`，不会静默写 `?`。要写简体内容请选 `utf-8`／`utf-8-bom`／`gb18030`，Big5 收不下简体。
- **公式注入防护默认开启且表头同样受防护**：`EscapeFormulaPrefix` 默认 `true`，以 `=`／`+`／`-`／`@` 开头的字段值多出一个可见的 `'`；表头文案是调用方在运行时给出的，框架无法证明它出自开发者而不是终端使用者，因此不按来源豁免——以这些字符开头的**标题**同样会多出前缀。代价是负数经 `TextFormat` 得到 `-5.00` 后被写成 `'-5.00`，**不字节往返**，表格软件读回来是文本而不是数字。交给机器逐字段解析的 `.txt` 应把它关掉。整份文件的改写聚合成一条 Warning（报数量与首个触发的行列），不逐格刷日志。

导入侧约定：
- **不导出 `.xls`，读 `.xls` 未经验证**：`ExcelImportFormat.Xls` 是可声明的输入格式，容器路径按 OLE 签章认它，但端到端行为在本组件中尚未经验证；对外承诺的产出格式只有 `Xlsx`／`Csv`／`Txt`。
- **格式自动判别只对两个工作簿格式有意义**：只认 OLE 与 zip 两类档头签章，副档名不参与判断；文字档没有可靠签名，读 `.csv`／`.txt` 必须指名 `Format`，否则抛 `InvalidOperationException`。指名为 `Xls`／`Xlsx` 时不强判签章，读取器按内容自行选解析器，因此档名与内容不符可以正常读。
- **编码自动判别只保证「解不撞错误」，不保证判对**：无 BOM 时先按 32KB 窗口严格试解 UTF-8，失败回退 Big5。这条链挡不住全部乱码——Big5 的代码页解码器把某些配不上对的高位字节（例如 `0xFF 0xFF`）解成私有区字符 `U+F8F8` 而**不抛**（换成异常回退也一样），另一些非法序列（落单高位字节、非法前导序列）才抛 `DecoderFallbackException`；**无 BOM 的 UTF-16 会被判成 UTF-8**（其字节全 `<0x80` 且夹着 `0x00`，是合法 UTF-8），整份档解出一串夹 NUL 的字符而不报任何错。这类档必须显式给 `TextEncodingName`。编码名取名称或别名，不取代码页编号（`"950"` 解析不到），未知名抛 `ArgumentException` 并点名该名称，不退回 UTF-8。
- **行号口径**：`ExcelImportRow.RowNumber` 是 1 起始的行序号，二进制档等于物理行号；文字档在字段值含换行时是**记录序号**，不是编辑器行数（读取器不暴露物理行位，被引号包住的换行属于同一条记录）。两种情况都不随 `SkipEmptyRows` 跳行或 `HeaderRowIndex` 丢行重排。错误报表与文档不得承诺「按编辑器行数找到那一行」。
- **行数上限**：`ExcelImportOptions.MaxRowCount` 只能落在本次生效的上限之内，默认上限是 `ExcelConstants.DefaultMaxImportRows`（1_000_000），应用把 `XiHanExcelOptions.MaxImportRows` 配得更低时以配置值为准，两条导入路径同判；越界与非正整数抛 `ArgumentOutOfRangeException`，报出的也是本次生效的那道界。
- **固定宽度读取的口径**：`FixedColumns` 非 `null` 就走这条路径，`Format` 不参与分流。分行与切列都在字节层做，只认 `\r\n`／`\n`／`\r` 三种行尾；`HasHeader` 一律按「无表头」处理，键名恒取 `ExcelFixedWidthField.Key`；`HeaderRowIndex` 仍解释（丢掉前导那么多行，丢掉的行照样占行号）；`Delimiter`、`SheetName`、`TrimHeaders` 不解释，`TrimValues`、`SkipEmptyRows`、`TextEncodingName` 照常生效。列宽总和超过 `ExcelConstants.MaxFixedRowWidthBytes`（1 MiB）、键为空或重复、宽度非正整数都在首次取行时抛 `InvalidOperationException`；不支持一个字符里可能含 `0x0D`／`0x0A` 的宽字节编码（UTF-16／UTF-32）。往返闭合要求列宽与写档编码一致，补位空格属于布局，要拿回原值请显式设 `TrimValues = true`。
- **来源不做补偿**：合并单元格除左上角外的格位读回 `null`，公式只读回已缓存的值，样式、批注、图表与宏不解释，加密工作簿不支持。空格子在文字档里是空字串、在工作簿里是 `null`。
- **对外异常面收在框架类型上**：容器读不通（伪造档头、截断档、损坏簿）与「档头判不出格式」都落在 `InvalidOperationException`，库自己的异常留在内部异常里；一处残留是伪装的 OLE 档短到读不出目录时由 BCL 交回 `ArgumentException`，本包不把它一起收口（按 `ArgumentException` 收口会把「编码指错」这条正当失败一并吞掉）。

注册侧约定：
- **`AddXiHanExcel` 会执行 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`**。这是**进程级且不可逆的副作用**：它给整个进程装上代码页编码，不是只影响本模块，调用之后无法在本进程内撤消。文字档与定宽档需要的 Big5 等代码页全靠它。
- 注册一律走 `TryAddSingleton`：应用层先注册自己的实现就不覆盖它；三个契约各绑一个门面，分派器与门面需要的写出器／读实现另外各注册一条，实现收的是裸 `XiHanExcelOptions`，因此额外有一条从 `IOptions<T>` 取 `Value` 的转接。
- 本方法不注册日志提供器：`DelimitedTextExporter` 与 `FixedWidthTextImporter` 收 `ILogger<T>`，宿主没装日志时解析它们会抛。
- `DelimitedTextExporter` 这个类名**同时承载 `Delimited` 与 `FixedWidth` 两种布局**，名字与职责不再贴合，但不改名：计划文件结构与 DI 注册把它钉死在该类型名上。读代码时按「文字档导出器」理解它，两条布局共用一份行写出骨架，差异只留在「一格写出什么」与「格间插什么」。

规模与内存（十万行 × 3 列的 xlsx 导出对照）：本组件流式导出 708ms／峰值工作集 62.1MB／输出 3,175,561B／行集合枚举 1 次；同一数据裸调库 256ms／64.6MB；库开 `EnableAutoWidth` 270ms／103.6MB；同一数据走 ClosedXML 全量 3782ms／322.5MB。原始档 `t10-probe-memory.txt`。

## 使用方式
模块装配：

```csharp
[DependsOn(typeof(XiHanExcelModule))]
public class MyModule : XiHanModule
{
}
```

不走模块装配时手工注册（同样会注册代码页编码提供程序）：

```csharp
services.AddXiHanExcel(configuration);
```

按属性导出 xlsx（自适应列宽、样式落地）：

```csharp
public class InvoiceExcelService(IExcelExporter exporter)
{
    public async Task ExportAsync(Stream output, IEnumerable<InvoiceRow> rows, int? rowCount, CancellationToken ct)
    {
        var sheet = new ExcelSheetSpec
        {
            SheetName = "提單明細",
            Title = "本月提單明細",
            RowType = typeof(InvoiceRow),
            Columns = ExcelColumnBuilder.CreateColumns<InvoiceRow>(),
            Rows = rows,
            ExpectedRowCount = rowCount
        };

        // rowCount 未给且未表态时抛 InvalidOperationException：分派器不猜哪条路径
        var result = await exporter.ExportAsync(output, sheet, ExcelFormat.Xlsx, cancellationToken: ct);
    }
}
```

固定宽度文字档（列宽按字节，须用代码构造列）：

```csharp
var columns = new List<ExcelColumn>
{
    new ExcelColumn<InvoiceRow>
    {
        Key = "No",
        Header = "提單號",
        FixedWidth = 14,
        Padding = ExcelTextPadding.Left,
        PadChar = '0',
        Value = row => row.No
    },
    new ExcelColumn<InvoiceRow>
    {
        Key = "Party",
        Header = "客戶",
        FixedWidth = 30,
        Value = row => row.Party
    }
};

var textOptions = new ExcelTextOptions
{
    Layout = ExcelTextLayout.FixedWidth,
    EncodingName = "big5",
    Overflow = ExcelTextOverflow.Throw
};

// ExcelSheetSpec 是类不是 record：换列清单就重建一份规格
var fixedWidthSheet = new ExcelSheetSpec
{
    SheetName = "提單明細",
    RowType = typeof(InvoiceRow),
    Columns = columns,
    Rows = rows
};

await exporter.ExportAsync(output, fixedWidthSheet, ExcelFormat.Txt, textOptions, ct);
```

`ExcelTextOptions` 与 `ExcelImportOptions` 都是 `record`，改一项用 `with` 派生副本，不要原地改共享实例。

逐行导入：

```csharp
public class ImportService(IExcelImporter importer)
{
    public async Task ReadAsync(Stream input, CancellationToken ct)
    {
        await foreach (var row in importer.ReadAsync(input, new ExcelImportOptions
        {
            Format = ExcelImportFormat.Csv,
            MaxRowCount = 100_000
        }, ct))
        {
            Console.WriteLine($"{row.RowNumber}: {row.Values["提單號"]}");
        }
    }
}
```

多表一个工作簿（恒走全量，排版全部落地）：

```csharp
await exporter.ExportAllAsync(output, [headerSheet, detailSheet], ct);
```

模板渲染（版面在模板档里，占位符 `{{Company}}`、集合 `{{Items.Name}}`，区分大小写且花括号内侧不能带空格）：

```csharp
await renderer.RenderAsync(output, templateStream, new { Company = "曦寒", Items = rows }, ct);
```

## 扩展点
- 替换写出或读取实现：应用层先注册 `IExcelExporter`／`IExcelImporter`／`IExcelTemplateRenderer` 的自定义实现即可，`AddXiHanExcel` 用 `TryAddSingleton` 不覆盖
- 自定义列来源：不调 `ExcelColumnBuilder` 也可以，直接构造 `ExcelColumn<TRow>` 清单；`ExcelColumnBuilder.ClearCache()` / `ClearCache(Type)` 用于测试或运行期改变特性后重建
- 单独用某条路径：`ClosedXmlExporter`、`MiniExcelStreamExporter`、`DelimitedTextExporter`、`ExcelDataReaderImporter`、`FixedWidthTextImporter` 都按具体类型注册在容器里，可以直接解析，绕过分派器
- 新增目标格式：先扩抽象包的 `ExcelFormat`，再在 `ExcelExporter` 加分派分支；本包不在默认路径提供 `.xls` 写出，也不打算承诺宏、数据透视表与图表

## 目录结构
```text
XiHan.Framework.Excel/
  README.md
  XiHanExcelModule.cs              模块入口，ConfigureServices 里调用 AddXiHanExcel
  Columns/
    ExcelColumnBuilder.cs        属性驱动的列清单构建与按行类型缓存
  Exporting/
    ExcelExporter.cs             导出分派器（IExcelExporter 实现）
    ClosedXmlExporter.cs         xlsx 全量工作簿（排版全落地）
    MiniExcelStreamExporter.cs   xlsx 流式工作簿（降级结果）
    DelimitedTextExporter.cs     文字档：Delimited 与 FixedWidth 两种布局
    ExcelWorkbookWriteGuard.cs   两条 xlsx 路径共用的表名与取值域判据
    ExcelRowTypeGuard.cs         两条路径共用的行型一致性判据
    MiniExcelTemplateRenderer.cs xlsx 模板渲染（IExcelTemplateRenderer 实现）
  Importing/
    ExcelImporter.cs             导入门面（IExcelImporter 实现，按列定义分流）
    ExcelDataReaderImporter.cs   .xls/.xlsx/.csv/.txt 读取
    FixedWidthTextImporter.cs    定长记录档按字节切列
    ExcelFormatProbe.cs          档头签章嗅探
    TextEncodingResolver.cs      BOM／严格 UTF-8 试探／Big5 回退
    ImportSharedRules.cs         行数上限收敛与「整行皆空」判定
  Text/
    TextWriterHelper.cs          编码解析、取值转文本、引号与公式前缀、按字节补位
  Extensions/DependencyInjection/
    XiHanExcelServiceCollectionExtensions.cs  AddXiHanExcel 注册
```
