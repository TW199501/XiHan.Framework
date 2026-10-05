# XiHan.Framework.Excel

> 表格数据导入导出：把「一份表格数据」写成 `.xlsx`／`.csv`／`.txt`，或把 `.xlsx`／`.xls`／`.csv`／`.txt` 与固定宽度定长档逐行读回，并可用现成 xlsx 模板渲染固定版式单据。

- **NuGet**：`XiHan.Framework.Excel`（契约在 `XiHan.Framework.Excel.Abstractions`，引用实现包会自动带上）
- **模块类**：`XiHanExcelModule`（抽象包不含模块类）
- **所在层**：基础设施层
- **关键依赖**：`ClosedXML`（全量工作簿）、`MiniExcel`（流式工作簿与模板渲染）、`ExcelDataReader`（多来源读取）；框架内部依赖 `XiHan.Framework.Core` 与 `XiHan.Framework.Excel.Abstractions`

## 概述

XiHan.Framework.Excel 面向「表格数据的进出」：一侧是你手上的行集合（`IEnumerable`，可以是惰性游标），另一侧是文件字节。它不替你把数据存到哪里、怎么命名、怎么鉴权——那些是文件层面的事。

导出侧由 `ExcelExporter` 做分派：`.csv`／`.txt` 交文字档写出器，`.xlsx` 按「流式表态 + 预期行数」在两条路径之间分流——`ClosedXmlExporter` 全量建簿、排版全部落地，`MiniExcelStreamExporter` 边生成边写、只保留少数排版项并在返回值上如实说明丢了什么。文字档写出器内部再按布局分两条：分隔符布局（引号、公式注入防护）与固定宽度布局（按字节补位）。

导入侧由 `ExcelImporter` 门面按「有没有列定义」分流：`ExcelDataReaderImporter` 读工作簿与分隔符文字档，`FixedWidthTextImporter` 按字节位置切定长记录档。两条路径都惰性逐行交出 `ExcelImportRow`，不物化整档。

`IExcelTemplateRenderer` 是第三条产出路径：版面画在现成的 xlsx 模板里，框架只把 <code v-pre>{{占位符}}</code> 换成值。

## 何时使用

- 要把查询结果导成 Excel 给业务方，或要把用户上传的 `.xlsx`／`.csv` 读成行集合。
- 要产机器逐字段解析的 `.txt`／`.csv`（分隔符或定长记录），或要把这类老档导进系统。
- 要打印固定版式单据：抬头、合并格、打印区域都在模板里，只填数据。
- 行数可能到十万级，需要在「排版全落地」与「不把整档建在内存里」之间自己选，而不是让框架替你猜。
- 与 [XiHan.Framework.VirtualFileSystem](./virtual-file-system)、[XiHan.Framework.ObjectStorage](./object-storage) 的区别：那两个包管**文件本身**（虚拟路径下的只读访问、对象存储的上传下载与生命周期），本包管**表格数据与档格式之间的转换**。写出器只往你给的 `Stream` 里写字节，既不落地文件也不上传云端——把导出的结果交给 VirtualFileSystem 落盘或 ObjectStorage 上传，是两件独立的事。
- 模板渲染与 [XiHan.Framework.Templating](./templating) 的区别：Templating 渲染**文本模板**（字符串占位替换、Scriban），产出是文字；本包的模板渲染吃 xlsx 模板容器，产出仍是带版面的工作簿。

## 安装与启用

```bash
dotnet add package XiHan.Framework.Excel
```

```csharp
[DependsOn(typeof(XiHanExcelModule))]
public class MyModule : XiHanModule { }
```

模块在 `ConfigureServices` 中调用 `services.AddXiHanExcel(config)`，据此完成：

- 绑定配置节 `XiHan:Excel` 到 `XiHanExcelOptions`，并额外注册一条从 `IOptions<XiHanExcelOptions>` 取 `Value` 的转接（各实现收的是裸选项对象）。
- `TryAddSingleton` 把 `IExcelExporter` 绑到 `ExcelExporter`、`IExcelImporter` 绑到 `ExcelImporter`、`IExcelTemplateRenderer` 绑到 `MiniExcelTemplateRenderer`，并各自注册分派器与门面需要的写出器／读实现。`TryAdd` 语义意味着应用层先注册的实现不会被覆盖。
- 执行 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`。**这是进程级且不可逆的副作用**：它给整个进程装上代码页编码（Big5、gb18030 等），不是只影响本模块，调用之后无法在本进程内撤消。文字档与定宽档需要这些编码全靠它。
- 不注册日志提供器。`DelimitedTextExporter` 与 `FixedWidthTextImporter` 需要 `ILogger<T>`，宿主没装日志时解析它们会抛——装配日志是宿主的事。

::: warning 配置里的行数上限会在解析服务时抛
`XiHanExcelOptions.MaxImportRows` 经构造参数进两个读实现。配成 0、负数或高于框架绝对上界时，**在解析这些服务的这一刻抛出** `ArgumentOutOfRangeException`，而不是被夹回上界。配置写错是装配期就该发现的问题，不会等到第一次导入才炸。
::

## 工作原理

- **导出分派**（`ExcelExporter`）：只做三件事——判分派输入、选一条路径、原样带出该路径的结果。守卫不抄第二份（行型一致性、列宽值域、颜色失败面、表名判据、取值可写性、文字档的分隔符禁令都长在各自写出器里）。`Csv`／`Txt` 一律交文字档写出器；`Xlsx` 先看 `ForceStreaming`：`true` 走流式、`false` 走全量（显式表态优先于阈值，预期行数再大也不改判）；`ForceStreaming` 为 `null` 时拿 `ExpectedRowCount` 与 `StreamingThreshold` 比，`>=` 即达阈值走流式。**两个都没给时抛出而不是猜一个**——「达到阈值自动切流式」要求先知道行数，而行数不可预知时切过去会连带静默丢样式。
- **多表入口恒走全量**：`ExportAllAsync` 没有流式形态（若干张表要共用一个工作簿、共用表名判重与落位顺序），清单里任何一张表 `ForceStreaming = true` 即整个请求被拒，不做「这张表改成全量」的改写。
- **文字档两条布局共用一份行写出骨架**：行序、每行写前查取消令牌、行尾拼接、flush 与聚合留痕的时机只有一份实现，差异只留在「一格写出什么」与「格间插什么」上。注意 `DelimitedTextExporter` 这个**类名同时承载 `Delimited` 与 `FixedWidth` 两种布局**，名字与职责不再贴合但不改名（文件结构与 DI 注册钉死了它），读代码时按「文字档导出器」理解。
- **导入分流只看一条规则**：`ExcelImportOptions.FixedColumns` 非 `null` 走固定宽度，否则走 ExcelDataReader。`Format` 不参与路由；列定义是空集合时仍然走固定宽度，由那条路径把「一列都没有」报出来，门面不悄悄改投另一条。门面不做签章嗅探、不解析编码、不预检选项、不代任何一边改写异常。
- **异常收口**：抽象契约在抽象包里、不引用任何第三方库，因此容器读不通、档头判不出格式、表名不存在都落在 `InvalidOperationException` 上，库原话留在内部异常里，不把库的异常型别当对外承诺。

## 核心能力

| 能力 | 承载 |
| --- | --- |
| 单表导出为 xlsx／csv／txt | `IExcelExporter.ExportAsync` |
| 多表导出为同一个 xlsx 工作簿 | `IExcelExporter.ExportAllAsync` |
| 全量工作簿排版：标题行与合并、冻结表头、自动筛选、表头加粗与底色、边框、列宽与自适应列宽、水平对齐、自动换行、逐格条件样式、Excel 数字／日期格式 | `ClosedXmlExporter` |
| 流式工作簿：只保留表头文案、列顺序、数字与日期格式串、表头冻结与自动筛选两个开关 | `MiniExcelStreamExporter` |
| 文字档：编码与 BOM、行尾序列、分隔符、引号策略、公式注入防护、固定宽度按字节补位 | `DelimitedTextExporter` |
| 模板渲染：<code v-pre>{{键名}}</code> 与 <code v-pre>{{键名.子键名}}</code> 集合展开 | `IExcelTemplateRenderer` / `MiniExcelTemplateRenderer` |
| 导入：xlsx／xls／csv／txt 与固定宽度定长档，惰性逐行、表头位置、空白处理、空行跳过、行数上限 | `IExcelImporter` |
| 属性驱动的列清单：按公共实例属性与 `[ExcelColumn]` / `[ExcelIgnore]` 生成并缓存 | `ExcelColumnBuilder.CreateColumns<TRow>()` |

**边界（三条，不藏）**：

1. **不导出 `.xls`**。导出目标只有 `Xlsx`／`Csv`／`Txt`，其它取值抛 `ArgumentOutOfRangeException`；`.xls` 只作为导入侧可识别的输入格式存在，而该读取路径的端到端行为**尚未经验证**（框架不产出这种档，也没有可参照的真实档），接入前请用手上的真实 `.xls` 档自行验证。也不承诺 `.xlsm` 的宏、数据透视表与图表。
2. **流式模式丢样式**。xlsx 走流式时承载不了整份排版，返回值必须是 `StylingApplied = false` 并附逐项说明的 `StylingSkipReason`；底色与列宽不是库里没有开关，而是打开它们要连着引入库的预设表头样式与整档缓冲的宽度计算，与本路径取向相反，因此按不承载处理并如实说出来。
3. **固定宽度按字节判定**。`ExcelColumn.FixedWidth` 与 `ExcelFixedWidthField.WidthBytes` 的单位都是目标编码下的**字节数**，不是字符数：一个汉字在 Big5 下占 2 字节、在 UTF-8 下占 3 字节。宽度必须与写档用的编码一致，否则从第二列起整体错位。

## 主要 API / 类型

### 契约

| 类型 | 说明 |
| --- | --- |
| `IExcelExporter` | `ExportAsync(Stream, ExcelSheetSpec, ExcelFormat, ExcelTextOptions?, CancellationToken)` 与 `ExportAllAsync(Stream, IReadOnlyList<ExcelSheetSpec>, CancellationToken)`；输出流所有权在调用方，只写入不关闭、不复位流位置 |
| `IExcelImporter` | `ReadAsync(Stream, ExcelImportOptions?, CancellationToken)` 返回 `IAsyncEnumerable<ExcelImportRow>`；输入流必须可读且可定位，读取一律从流起点开始 |
| `IExcelTemplateRenderer` | `RenderAsync(Stream output, Stream template, object data, CancellationToken)`；本组件唯一接受 `object` 数据入参的入口——占位符到值的对应关系由模板决定，不由 CLR 型别决定 |
| `ExcelExporter` / `ExcelImporter` / `MiniExcelTemplateRenderer` | 三契约的默认实现（分派器／门面／MiniExcel 渲染器） |

### 导出模型

| 类型 | 关键成员 |
| --- | --- |
| `ExcelSheetSpec` | `SheetName`（必填，`null` 或空白在 `init` 抛）、`Columns`、`Rows`（非泛型 `IEnumerable`，允许惰性）、`RowType`、`Title?`、`FreezeHeader=true`、`AutoFilter=true`、`HeaderBold=true`、`HeaderFill="#D9E1F2"`、`Borders=true`、`ExpectedRowCount?`、`ForceStreaming?` |
| `ExcelColumn` / `ExcelColumn<TRow>` | `Key`、`Header`、`Order`、`Width?`、`NumberFormat?`、`TextFormat?`、`Alignment`、`Wrap`、`FixedWidth?`、`Padding`、`PadChar`、`CellStyle?`、`GetValue(object?)`；泛型派生类多一个 `Value` 取值委托 |
| `ExcelTextOptions`（`record`） | `Layout`、`Delimiter?`、`EncodingName`、`NewLine`、`IncludeHeader`、`Quote`、`Overflow`、`EscapeFormulaPrefix` |
| `ExcelTextStyle`（`record`） | `FontColor?`、`Fill?`（十六进制串）、`Bold` |
| `ExcelExportResult`（`record`） | `Format`、`FileExtension`、`ContentType`、`StylingApplied`、`StylingSkipReason`；经 `Styled(...)`／`Degraded(..., reason)` 两个工厂构造，后者当场拒掉空理由 |
| `ExcelFormat` | `Xlsx`／`Csv`／`Txt` |
| `ExcelTextLayout` / `ExcelTextQuote` / `ExcelTextOverflow` / `ExcelTextPadding` / `ExcelAlignment` | `Delimited`/`FixedWidth`；`Minimal`/`All`/`None`；`Throw`/`Truncate`；`Left`/`Right`；`Auto`/`Left`/`Center`/`Right` |
| `ExcelColumnBuilder` | `CreateColumns<TRow>()`（按行类型缓存，同类型重复调用返回同一引用）、`ClearCache()` / `ClearCache(Type)` |

### 导入模型

| 类型 | 关键成员 |
| --- | --- |
| `ExcelImportOptions`（`record`） | `Format?`、`SheetName?`、`HeaderRowIndex`、`HasHeader`、`TrimHeaders`、`TrimValues`、`SkipEmptyRows`、`TextEncodingName?`、`Delimiter?`、`FixedColumns?`、`MaxRowCount?` |
| `ExcelImportRow`（`record`） | `RowNumber`（1 起始行序号）、`Values`（`IReadOnlyDictionary<string, object?>`，枚举顺序即源文件列顺序） |
| `ExcelFixedWidthField`（`record`） | `Key`、`WidthBytes`（字节宽度，正整数） |
| `ExcelImportFormat` | `Xls`／`Xlsx`／`Csv`／`Txt`——比 `ExcelFormat` 多出 `Xls`：框架能读不能写 |

### 常量

`ExcelConstants`：`DefaultStreamingThreshold=50_000`、`DefaultAutoWidthSampleRows=500`、`DefaultEncodingName="utf-8-bom"`、`DefaultMaxImportRows=1_000_000`、`MaxCellTextLength=32_767`（单元格字符上限）、`MaxFixedRowWidthBytes=1_048_576`（定宽导入单行列宽总和上限）、三个内容类型与三个扩展名常量。

## 配置

配置节 `XiHan:Excel`（`XiHanExcelOptions.SectionName`）：

| 字段 | 类型 | 默认值 | 含义 |
| --- | --- | --- | --- |
| `StreamingThreshold` | `int` | `50000` | xlsx 分流阈值（行数）。`ForceStreaming` 未表态时，`ExpectedRowCount >= 本值` 走流式。`0` 或负数等于「任何非负行数都达到阈值」，也就是**一律走流式**，这是合法表达、不当非法值拒掉；表规格显式表态 `true`／`false` 时本值不参与判定 |
| `AutoWidthSampleRows` | `int` | `500` | 全量工作簿的自适应列宽取样行数。`0` 表示只看表头行，负数抛出 |
| `DefaultEncodingName` | `string` | `"utf-8-bom"` | 编码名称常量 `ExcelConstants.DefaultEncodingName` 在选项上的镜像默认值。⚠ 当前实现**不读这一项**来决定文字档的编码：文字档写出用的是 `ExcelTextOptions.EncodingName`（其初始值就是同名常量），导入用的是 `ExcelImportOptions.TextEncodingName`（默认 `null` 即自动判别）。改这一项不会改变任何一档的编解码结果 |
| `MaxImportRows` | `int` | `1000000` | 应用可用的导入行数硬上限。必须是正整数且不高于框架绝对上界 `ExcelConstants.DefaultMaxImportRows`（同为 1_000_000）：**只能收紧不能放宽**，越界在解析相关服务时抛 `ArgumentOutOfRangeException`，不夹回上界；两条导入路径在构造时各取一次该值 |

```json
{
  "XiHan": {
    "Excel": {
      "StreamingThreshold": 50000,
      "AutoWidthSampleRows": 500,
      "DefaultEncodingName": "utf-8-bom",
      "MaxImportRows": 1000000
    }
  }
}
```

> 单次请求的行数上限另有其位：`ExcelImportOptions.MaxRowCount` 只能落在**本次生效的那一道**界之内（默认是框架硬上限，应用配得更低时以配置值为准），越界抛出的也是生效中的那道界——把上限配成 2 行时，报出的是 2 而不是 1000000。

## 使用示例

### 属性驱动导出 xlsx（排版全部落地）

```csharp
using XiHan.Framework.Excel;
using XiHan.Framework.Excel.Columns;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;

public class InvoiceExportService(IExcelExporter exporter)
{
    public async Task<ExcelExportResult> ExportAsync(Stream output, IEnumerable<InvoiceRow> rows, int? rowCount, CancellationToken ct)
    {
        var sheet = new ExcelSheetSpec
        {
            SheetName = "提單明細",
            Title = "本月提單明細",
            RowType = typeof(InvoiceRow),
            Columns = ExcelColumnBuilder.CreateColumns<InvoiceRow>(),
            Rows = rows,                 // 允许惰性序列，导出侧不物化
            ExpectedRowCount = rowCount  // 不给且未表态 ForceStreaming 时抛出：分派器不猜
        };

        // 行数可能达阈值 => 可能降级。要排版全落地就 ForceStreaming = false，或换文字档
        return await exporter.ExportAsync(output, sheet, ExcelFormat.Xlsx, cancellationToken: ct);
    }
}
```

```csharp
public class InvoiceRow
{
    [ExcelColumn("提單號", Order = 1)]
    public string No { get; set; } = string.Empty;

    [ExcelColumn("金額", Width = 14, NumberFormat = "#,##0.00")]
    public decimal Amount { get; set; }

    [ExcelColumn("開票日期", NumberFormat = "yyyy-MM-dd")]
    public DateTime InvoiceDate { get; set; }

    [ExcelIgnore]
    public string InternalTraceId { get; set; } = string.Empty;
}
```

### 检查降级结果

```csharp
var result = await exporter.ExportAsync(output, sheet, ExcelFormat.Xlsx, cancellationToken: ct);

if (!result.StylingApplied)
{
    // 流式模式：标题行、表头加粗与底色、边框、逐格样式、列宽、对齐与换行都没写出
    logger.LogWarning("导出降级：{Reason}", result.StylingSkipReason);
}
```

### 固定宽度文字档（列宽按字节，必须代码构造列）

```csharp
var columns = new List<ExcelColumn>
{
    new ExcelColumn<InvoiceRow>
    {
        Key = "No",
        Header = "提單號",
        FixedWidth = 14,                  // Big5 下 14 字节
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
    Overflow = ExcelTextOverflow.Throw    // 超宽默认抛出；Truncate 会丢弃超出部分并留一条聚合 Warning
};

// ExcelSheetSpec 是类不是 record，换列清单就重建一份规格（改选项才用 with）
var fixedWidthSheet = new ExcelSheetSpec
{
    SheetName = "提單明細",
    RowType = typeof(InvoiceRow),
    Columns = columns,
    Rows = rows
};

await exporter.ExportAsync(output, fixedWidthSheet, ExcelFormat.Txt, textOptions, ct);
```

### 逐行导入

```csharp
public class ImportService(IExcelImporter importer)
{
    public async Task ReadAsync(Stream input, CancellationToken ct)
    {
        var options = new ExcelImportOptions
        {
            Format = ExcelImportFormat.Csv,   // 文字档必须指名：它们没有可靠签名，留空判不出格式
            HeaderRowIndex = 2,               // 前导两行丢掉，第三行作表头
            TrimValues = true
        };

        await foreach (var row in importer.ReadAsync(input, options, ct))
        {
            // row.RowNumber 是记录序号；值含换行时它不等于编辑器里的行数
            var no = row.Values["提單號"];
        }
    }
}
```

### 定长记录档导入

```csharp
var fixedColumns = new ExcelFixedWidthField[]
{
    new("No", 14),
    new("Party", 30)
};

await foreach (var row in importer.ReadAsync(input, new ExcelImportOptions
{
    FixedColumns = fixedColumns,
    TextEncodingName = "big5",   // 宽度单位是字节，必须与写档编码一致
    TrimValues = true            // 补位空格属于布局，要拿回原值请显式剥
}, ct))
{
}
```

### 多表与模板渲染

```csharp
// 多表恒走全量工作簿：排版全部落地，清单里任何一张表要求流式即整个请求被拒
await exporter.ExportAllAsync(output, [headerSheet, detailSheet], ct);

// 模板渲染：版面在模板档里，占位符 {{Company}}、集合 {{Items.Name}}（区分大小写，花括号内侧不能有空格）
await renderer.RenderAsync(output, templateStream, new { Company = "曦寒", Items = rows }, ct);
```

## 注意事项与最佳实践

### 列特性与列模型

- **`[ExcelColumn]` 不支持固定宽度布局**：特性不带字节宽度。`Layout = FixedWidth` 时每列必须由调用方构造 `ExcelColumn<TRow>` 并给出 `FixedWidth`，缺这道设置的列在写出任何字节之前抛 `InvalidOperationException`，消息列出缺宽度的列键并说明「列特性不带字节宽度」。
- **特性上的 `Width = 0` 是「未指定／自动列宽」的标记**：特性命名参数不支持可空数值类型，所以用 `double` 并以 `0` 作缺省标记，由构建器映射为列上的 `null`。
- **`ExcelColumn.Order` 不要拿来回读**：它是列构建器**排定后回写的位置序号**，不是你在特性上声明的顺序值。特性 `Order` 的负数（默认 `-1`）一律按「未指定」处理，排在显式顺序的列之后。写出顺序以列清单给出的顺序为准。
- **列宽的值域在两侧不一致**：`ExcelColumn.Width` 必须是大于 0 且不高于 255 的有限数——`<= 0`、`NaN`／无穷与 `> 255` 在 xlsx 全量导出时被同一个 `ArgumentOutOfRangeException` 拒掉；而 `[ExcelColumn(Width = 400)]` 能通过构建（构建器只拒负数与非有限值），**要到导出期才撞上这道守卫**。要固定列宽就按值域给，要自适应就给 `null`（特性上写 `0`）。
- **`NumberFormat` 只在数值格与日期格生效**：它是 Excel 格式串，与 .NET 格式串不通用，文字路径不套用——文字档与 xlsx 档同一列显示可以不同，这是刻意设计。文字档上控制取值转文本的是 `ExcelColumn.TextFormat`（优先于 `NumberFormat`）。
- **`Title` 与 `SheetName` 的空白口径不同**：`Title` 仅含空白时按「未填」处理，不写标题行也不抛；`SheetName` 为 `null` 或仅含空白在 `ExcelSheetSpec` 的 `init` 直接抛 `ArgumentException`。
- **表名与底色的守卫只在承载它们的路径判**：表名判据（≤31 个 UTF-16 代码单元、不含 `: \ / ? * [ ]` 与 `U+0000`／`U+0003`、不以单引号开头或结尾、多表判重不区分大小写）两条 xlsx 路径共用；`HeaderFill` 不是合法十六进制串只在全量工作簿路径抛，流式模式不写底色、文字档没有底色，那两种场合这个值不参与写出也不报错。
- **`ExpectedRowCount` 两个入口都校验**：负数不是合法的行数声明，`ExportAsync` 对 Csv／Txt／Xlsx 一律拒（`ArgumentOutOfRangeException`，`ParamName` 为 `ExpectedRowCount`），`ExportAllAsync` 同样校验但**不用它分流**（多表恒走全量），消息点名「第 N 张表」。
- **取值装不下就抛出，不改写**：非有限的 `double`／`float`、长过 `MaxCellTextLength`（32_767）的字串在两条 xlsx 路径都抛 `InvalidOperationException`；行集合里有异型行时抛并点名行位置与期望／实际类型全名（逐笔判定，不抽样）。
- **取消之后必须丢弃这条流**：取消被观察到时一律抛 `OperationCanceledException`、绝不交出结果，但**不承诺失败原子性**——落点决定流里剩下什么：入口被观察到时零字节；中途被观察到时可能是前半部分行或一份没收尾的档；收尾之后才被观察到时可能已是一份完整的档。重试请换一条新流。

### 文字档：编码、布局与改写

- **不可映射字符抛而不降级**：目标编码收不下待写出的字符时抛 `EncoderFallbackException`，不会静默写成 `?` 再交回「成功」的坏档。要写简体内容请选 `utf-8`／`utf-8-bom`／`gb18030`——**Big5 收不下简体字**。
- **非法选项组合在写出任何字节之前被拒**（`ArgumentException`，`ParamName` 为 `textOptions`）：`Delimiter = ' '` 配 `Quote = None` 是非法组合；`Delimiter` 取 `\r`、`\n` 或 `"` 同样被预写拒。判据是「写出去就读不回来」，不是「不推荐」。
- **固定宽度布局下 `Delimiter`、`Quote`、`EscapeFormulaPrefix` 三项一律无效**：设置了不报错也不参与写出（定宽档按字节位置切列，这三项没有对应的解析位，前缀还会吃掉一格字节宽度让后续列位整体错位）。该布局同时**不接受值内换行**：`\r`／`\n` 没有可以包住它们的引号，写出会让一档被读成错行的两档，因此取到该行即抛——而行集合是惰性游标，抛出时前面的行可能已经落盘。含换行的数据请用分隔符布局配引号策略写出。
- **`PadChar` 必须是单字节字符且不得为 `\r`／`\n`**：这一条与内容无关，在写出第一个字节之前按列判完。
- **截断落在 .NET `StringInfo` 的文本元素边界**：`Overflow = Truncate` 以文本元素为最小取舍单位，不会把一个多字节字符切成半个字节序列、也不留下代理对的前半边，代价是结果可能比列宽少若干字节、差额由补位字符填满。**这不是字素簇承诺**：emoji 家族（ZWJ 序列、区域指示符）不保证原样往返。截断会丢弃数据，所以整份文件聚合成一条 Warning，报出数量与首个触发的行列。
- **公式注入防护默认开启，且表头与数据同样受防护**：`EscapeFormulaPrefix` 默认 `true`，以 `=`／`+`／`-`／`@` 开头的字段值多出一个可见的 `'`。表头文案是调用方在运行时给出的 `required string`，框架无从证明它出自开发者而不是终端使用者，因此不按来源豁免——**以这些字符开头的标题也会多出前缀**（早期版本豁免表头，已改）。代价要说清楚：默认配置下负数列 `-5.00` 会被写成 `'-5.00`，**不字节往返**，表格软件读回来是文本而不是数字。交给机器逐字段解析的 `.txt` 请关掉它；改写聚合成一条 Warning（数量 + 首个触发的行列），不逐格刷日志。

### 导入：格式判别与编码

- **`Format` 留空只对两个工作簿格式有意义**：自动判别只认两类档头签章——OLE 复合文件（旧版 `.xls` 容器）与 zip（`.xlsx` 容器）。副档名不参与判断，档名叫 `.xls` 而内容是 `.xlsx` 判为 `Xlsx`。文字档没有可靠签名，HTML 表格、XML 表格这类「伪装成 Excel 的文本」也没有任何一段固定字节可以当身份依据，因此读 `.csv`／`.txt` **必须指名**，否则抛 `InvalidOperationException`。指名为 `Xls`／`Xlsx` 时不强判签章，读取器按内容自行选解析器；指名为 `Csv`／`Txt` 则强制走文字解析器，内容是二进制工作簿时按字串解析、不报错但取不到有意义的列。
- **编码自动判别只保证「解不撞错误」，不保证判对**：无 BOM 时先按 32KB 窗口严格试解 UTF-8，失败回退 Big5。这条链的真实边界要按样子说：
  - UTF-8 一支挡得住——非法 UTF-8 序列抛 `DecoderFallbackException`，读档停下而不是交出替换字符；
  - Big5 一支挡不住——该代码页的解码器把某些配不上对的高位字节（例如 `0xFF 0xFF`）解成私有区字符 `U+F8F8` 而**不抛**，换成异常回退也一样；另一些非法序列（落单高位字节、非法前导序列）才抛 `DecoderFallbackException`。读到的字里出现 `U+F8F8` 就代表源档那一处字节已经损坏，要不要当错误由调用方决定。
  - **无 BOM 的 UTF-16 会被判成 UTF-8**：ASCII 段在 UTF-16LE 下是每个可见字符后跟一个 `0x00`，而 `0x00` 本身是合法 UTF-8，严格试探因此不会失败，整份档会解出一串夹着 NUL 的字符而不报任何错。
  - 纯 ASCII 档在 UTF-8 与 Big5 下都合法，一段 Big5 双字节也可能正好构成合法 UTF-8 序列。这三类档都必须显式给 `TextEncodingName`——那是唯一确定的做法。
- **编码名取名称或别名，不取代码页编号**：`"big5"`、`"csBig5"`、`"BIG5"` 可，`"950"`、`"MS950"` 解析不到并抛 `ArgumentException`，不退回 UTF-8。
- **`ExcelImportRow.RowNumber` 不是编辑器行数**：二进制档（`.xls`／`.xlsx`）它等于物理行号；文字档在字段值含换行时是**记录序号**——被引号包住的换行属于同一条记录，而读取器不暴露物理行位。两种情况都不随 `HeaderRowIndex` 丢行或 `SkipEmptyRows` 跳行重排。要在文字档上定位到行，得按记录序号数，或先把值里的换行清掉再导。错误报表与文档不得承诺「按编辑器行数找到那一行」。
- **来源不做补偿**：合并单元格除左上角外的格位读回 `null`（那是 Excel 自身的存储形态），公式只读回已缓存的值，样式、批注、图表与宏一律不解释，加密工作簿不支持。空格子在文字档里是空字串、在工作簿里是 `null`——同一列在两种来源上的 CLR 型别可以不同，工作簿交回 `double`／`DateTime`／`bool`／`string`，文字档一切值都是 `string`。
- **重名表头加后缀**：`Values` 的键唯一性由导入器保证（重名表头追加 `_n` 后缀），枚举顺序是源文件的列顺序而不是按键排序。行数比表头窄时缺的列取 `null`，比表头宽时多出的列以 `Col{n}` 保留，不丢弃数据。
- **默认不剥取值空白**：`TrimValues` 默认 `false`——值里的首尾空白是业务数据（条码、单号、定长字段常靠空格补位），去掉就与源档不再逐字往返。表头文案默认剥（`TrimHeaders` 为 `true`），因为表头键是开发者用来取值的。
- **定宽导入的列定义在首次取行时才判**：清单里的空项、空字串的键、非正整数的宽度、重复的键、列宽总和超过 `MaxFixedRowWidthBytes`（1 MiB）、集合为空，都抛 `InvalidOperationException` 并点名是哪一项。之所以不在 `init` 判：选项交出的是**调用方持有的** `IReadOnlyList<T>`，那份清单可能在 `init` 之后被它背后的 `List<T>` 改坏，判据必须在真要切列之前按当时那份清单重算。该路径不认得表头（`HasHeader` 一律按无表头处理）、不支持一个字符里可能含 `0x0D`／`0x0A` 的宽字节编码（UTF-16／UTF-32），行字节数不足列宽总和时缺的列补空字串、超出时丢弃多出的部分（两种情况各记一条 Debug 日志）。
- **`.xls` 读取未经端到端验证**：见「核心能力」的边界一条。对外承诺的产出格式只有 `Xlsx`／`Csv`／`Txt`。
- **读取器一次性**：`ReadAsync` 交回的异步序列不重复枚举；两条路径都绝不对传入的流调用 `Dispose`，枚举结束后可复位重读，但「从流中间续读」不是本契约的能力。

### 模板渲染

- **模板流只能渲染一次**：渲染库读完就把它关掉，重复渲染要每次交回一份新流；模板流必须可读、可定位（`CanSeek`）且非空，这三类失败在调用渲染库之前抛出，输出流零字节。输出流所有权仍在调用方。
- **缺键不报错**：数据里没有对应键时那一格留空，不猜值、不保留占位符原文。占位符区分大小写，<code v-pre>{{ Company }}</code>（内侧带空格）解析不到值。
- **日期落成文本格**：与全量工作簿路径的日期格不同，模板渲染里数值仍是数值格、日期是文本格、`null` 写空。

## 已知不对称与残留

::: tip xlsx 日期下限两条路径同判，但**格位**仍不同
日期下限只有一把尺：**`1900-01-01`**。早于它的三种日期型别，两条 xlsx 路径都拒（`InvalidOperationException`，两条路径抛出的成因句逐字相同）：

| 型别 | 全量路径（ClosedXML） | 流式路径（MiniExcel） |
| --- | --- | --- |
| `DateTime` | 早于下限拒；下限及之后落日期格 | 早于下限拒；下限及之后落日期格 |
| `DateOnly` | 早于下限拒；下限及之后落**文本格**，原值留在文字里 | 早于下限拒；下限及之后落**日期格** |
| `DateTimeOffset` | 早于下限拒；下限及之后落**文本格**，值与偏移量都留在文字里 | 早于下限拒；下限及之后落**日期格**（取钟表时刻，偏移量不落格） |

判据说的是政策：本框架只接受不早于 `1900-01-01` 的日期，更早的值不依赖写出库与表格软件各自的宽容度，也不按取值远近划分哪一段「安全」。下限不跟着格位走，是因为分派器按行数替调用方选路径——若「哪一天之前不能写」由落进哪种格子决定，同一份规格能不能导就成了走哪条的副产品。

因此**早先「要原样保住早年日期就走全量路径（`ForceStreaming = false`）」这条出路已作废**：早于下限的日期只能让该列取成文本（值由呼叫端自己排成字串），或改用不早于 `1900-01-01` 的日期。

下限之后的格位差异照旧存在，且会影响读回的型别：`DateOnly` 与 `DateTimeOffset` 在全量路径读回 `string`、在流式路径读回 `DateTime`（`DateTimeOffset` 的偏移量在流式路径不落格）。这不改变能不能导，但下面那条「不承诺格位型别一致」对它们同样适用。
:::

其它同类现实：`byte` 在流式路径落文本格、在全量路径落数值格；`TimeOnly` 在流式路径落时长格。这类同值异格不改变读回的值，因此不因格位不同而拒写——本包只承诺列顺序、表头文案与数值内容一致，**不承诺格位型别一致**。

## 扩展点 / 自定义

- **替换实现**：`AddXiHanExcel` 全程 `TryAddSingleton`，应用层先行注册 `IExcelExporter`／`IExcelImporter`／`IExcelTemplateRenderer` 的自定义实现即可覆盖，注册顺序不会把它盖回去。
- **直接用某条路径**：`ClosedXmlExporter`、`MiniExcelStreamExporter`、`DelimitedTextExporter`、`ExcelDataReaderImporter`、`FixedWidthTextImporter` 都按具体类型注册在容器里，可以直接解析、绕过分派器或门面。分派器与门面的构造函数收的是具体类型，这两项注册都是必需的。
- **自定义列来源**：不调 `ExcelColumnBuilder` 也可以，直接构造 `ExcelColumn<TRow>` 清单。构建器的列缓存按行类型保留，运行期改变了特性或要重建时调 `ClearCache()` / `ClearCache(Type)`。
- **换掉某一家写出／读取实现**是应用层的正当选择；新增目标格式要先扩抽象包的 `ExcelFormat`，再在 `ExcelExporter` 加分派分支。本包不在默认路径提供 `.xls` 写出。

## 依赖模块

- [XiHan.Framework.Core](./core) — 模块化与依赖注入基础（`XiHanModule`、`ConfigureServices`、选项绑定）
- `XiHan.Framework.Excel.Abstractions` — 契约与模型所在包，不含模块类、不引用任何 Excel 库

第三方库：`ClosedXML`、`MiniExcel`、`ExcelDataReader`。

## 相关模块

- [XiHan.Framework.ObjectStorage](./object-storage) — 把导出的字节存到本地磁盘或云上：本包只管数据与档格式的转换，不管文件存到哪里
- [XiHan.Framework.VirtualFileSystem](./virtual-file-system) — 虚拟路径下的只读文件访问（模板档、静态资源）
- [XiHan.Framework.Templating](./templating) — 文本模板渲染，与本包的 xlsx 模板渲染分属两种产出物
