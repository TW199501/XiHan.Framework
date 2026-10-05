# XiHan.Framework.Excel.Abstractions

## 概述
XiHan.Framework.Excel.Abstractions 定义 Excel 导入导出的全部契约与数据模型：导出契约 `IExcelExporter`、固定版式模板渲染契约 `IExcelTemplateRenderer`、导入契约 `IExcelImporter`，以及表规格 `ExcelSheetSpec`、列模型 `ExcelColumn` / `ExcelColumn<TRow>`、文字档选项 `ExcelTextOptions`、导入选项 `ExcelImportOptions` 与结果模型 `ExcelExportResult` / `ExcelImportRow`。

契约签名里不出现任何第三方类型，写出与读取的具体实现由 `XiHan.Framework.Excel` 提供。业务代码可以只引用本包面向接口编程，把具体实现留给应用层或框架默认实现。

## 核心能力
- 导出契约：单表 `ExportAsync`（按 `ExcelFormat` 分派 Csv／Txt／Xlsx）与多表 `ExportAllAsync`（同一工作簿），结果用 `ExcelExportResult.StylingApplied` 与 `StylingSkipReason` 说明样式有没有落地
- 导入契约：`IExcelImporter.ReadAsync` 逐行交出 `IAsyncEnumerable<ExcelImportRow>`，读是惰性的，不物化整档
- 列模型：代码构造的 `ExcelColumn<TRow>`（取值委托 + 呈现项）与属性驱动的列特性 `[ExcelColumn]` / `[ExcelIgnore]` 两套来源
- 文字档布局：分隔符与固定宽度两种布局由 `ExcelTextLayout` 选择，字节宽度、补位方向、超宽策略与引号策略都在契约上表达
- 模板渲染契约：`IExcelTemplateRenderer` 按现成 xlsx 模板的占位符填数据，版面归模板档，框架不重排
- 配置载体：`XiHanExcelOptions`（配置节 `XiHan:Excel`）与常量表 `ExcelConstants`

能力边界（与实现包同一口径，不藏在这里）：
- **不导出 `.xls`**。导出目标只有 `ExcelFormat.Xlsx`、`Csv`、`Txt` 三个取值；`.xls` 只作为导入侧可识别的输入格式存在，而该读取路径的端到端行为在本组件中尚未经验证，接入前请用真实 `.xls` 档自行验证
- **xlsx 的流式写出模式丢样式**。承载不了排版时返回值是降级结果，`StylingApplied` 为 `false` 并逐项说明丢了什么；文字档本身没有样式概念，写它不构成降级
- **固定宽度一律按字节判定**。列宽的单位是目标编码下的字节数而不是字符数，两侧同一口径，用错编码会让后续列位整体错位

## 依赖关系
- 不引用仓库内任何项目，只依赖 .NET 基类库
- 不包含独立模块类
- 不引用 ClosedXML、MiniExcel、ExcelDataReader 等写出／读取库，因此契约不把库自己的异常型别当对外承诺

## 配置与约定
- 选项类为 `XiHanExcelOptions`，配置节名固定为 `XiHan:Excel`（`XiHanExcelOptions.SectionName`）；`StreamingThreshold`、`AutoWidthSampleRows`、`DefaultEncodingName`、`MaxImportRows` 四项的默认值都取 `ExcelConstants` 上的常量，不随配置变化的是常量本身。
- 导入行数上限分两层：`ExcelConstants.DefaultMaxImportRows`（1_000_000）是框架绝对上界，调用方只能收紧不能放宽；`ExcelImportOptions.MaxRowCount` 与 `XiHanExcelOptions.MaxImportRows` 越界一律抛 `ArgumentOutOfRangeException`，不夹回上界，也不把 0 或负数当成「不限制」。
- 表规格的 `SheetName` 为 `null` 或仅含空白时在 `init` 直接抛 `ArgumentException`；`Title` 仅含空白时按「未填」处理，不写标题行也不抛。两者口径不同，前者是工作簿的必要标识，后者是可选项。
- 列模型 `ExcelColumn.Width` 的有效域是「大于 0 且不高于 255 的有限数」，`null` 表示自适应列宽；0、负数、`NaN`、无穷与超过 255 的取值在 xlsx 全量导出时被拒（`ArgumentOutOfRangeException`）。
- 列特性 `[ExcelColumn]` **不带字节宽度**，因此 `Layout = ExcelTextLayout.FixedWidth` 时每列的 `ExcelColumn.FixedWidth` 必须由调用方用代码构造并给出，缺这道设置的列在导出时抛出并列出缺宽度的列键；特性侧要填宽度只能走代码构造的列。
- 特性上的 `Width` 是 `double` 而非可空数值（特性命名参数不支持可空数值类型），因此以 `0` 表示「未指定／自动列宽」，由列构建器映射为列模型上的 `null`。
- `ExcelColumn.Order` 是列构建器**排定后回写的位置序号**，不是用户在特性上声明的值，不要用它的字面值做回读或跨请求比对；写出顺序以列清单的给出顺序为准。
- `ExcelColumn.NumberFormat` 是 Excel 的数字／日期格式串，只在数值格与日期格生效，文字路径不套用（文字档上对应的设置是 `ExcelColumn.TextFormat`）；它与 .NET 格式串不通用。
- 文字档的编码名一律按严格回退解析：目标编码收不下待写出的字符时抛 `EncoderFallbackException`，不会静默写成 `?`。Big5 收不下简体字，简体内容请用 `utf-8`／`utf-8-bom` 或 `gb18030`。
- 导入行的 `ExcelImportRow.RowNumber` 是本表内 1 起始的行序号：二进制档（`.xls`／`.xlsx`）等于物理行号；文字档在字段值含换行时是记录序号而不是编辑器行数。两种情况都不随跳行重排。错误报表不得承诺「按编辑器行数找到那一行」。
- `ExcelImportOptions.FixedColumns` 非 `null` 就是把这份档当固定宽度文字档读：`HasHeader` 在该路径一律按「无表头」处理，键名恒取 `ExcelFixedWidthField.Key`，宽度单位是字节。
- 值的 CLR 型别由来源决定，契约不做统一化：工作簿交回 `double`／`DateTime`／`bool`／`string`，空格子是 `null`；文字档一切值都是 `string`，空字段是空字串。
- 流所有权在调用方：导出只写入不关闭、不复位流位置；导入只读取不关闭，但一律从流的起点开始读。取消被观察到时一律抛出、绝不交出成功结果，调用方在抛出后必须丢弃该流的内容。

## 使用方式
面向契约编程时只引用本包，具体实现由应用层注册：

```csharp
public class ReportService(IExcelExporter exporter, IExcelImporter importer)
{
    public async Task ExportAsync(
        Stream output,
        IReadOnlyList<ExcelColumn> columns,
        IEnumerable<InvoiceRow> rows,
        CancellationToken cancellationToken)
    {
        var sheet = new ExcelSheetSpec
        {
            SheetName = "提單明細",
            RowType = typeof(InvoiceRow),
            Columns = columns,                       // 由调用方构造，或交实现包的列构建器按属性生成
            Rows = rows,                             // 允许惰性序列，契约不要求先物化成列表
            ExpectedRowCount = 80_000
        };

        var result = await exporter.ExportAsync(output, sheet, ExcelFormat.Xlsx, cancellationToken: cancellationToken);

        // 走流式模式时 StylingApplied 为 false，StylingSkipReason 逐项说明没落地的排版
        if (!result.StylingApplied)
        {
            Console.WriteLine(result.StylingSkipReason);
        }
    }
}
```

完整能力（写出器、读取器、模块与配置绑定）请引用实现包 `XiHan.Framework.Excel`。

## 扩展点
- 自定义写出实现：实现 `IExcelExporter`（或 `IExcelTemplateRenderer`），由应用层先注册即可覆盖框架默认实现
- 自定义读取实现：实现 `IExcelImporter`，可直接把 `ReadAsync` 写成惰性异步序列，行号与取消口径由 `ExcelImportRow` 的契约约束
- 自定义列来源：不调用实现包的列构建器也可以，直接构造 `ExcelColumn<TRow>` 清单，呈现项按上面的值域给出
- 复用模型：`ExcelSheetSpec`、`ExcelColumn`、`ExcelTextOptions`、`ExcelExportResult` 都是可在抽象层引用的模型，`ExcelTextOptions` 与 `ExcelImportOptions` 为 `record`，用 `with` 派生副本而不是原地改共享实例

## 目录结构
```text
XiHan.Framework.Excel.Abstractions/
  README.md
  ExcelConstants.cs
  XiHanExcelOptions.cs
  Attributes/
    ExcelColumnAttribute.cs
    ExcelIgnoreAttribute.cs
  Enums/
    ExcelAlignment.cs
    ExcelFormat.cs
    ExcelImportFormat.cs
    ExcelTextLayout.cs
    ExcelTextOverflow.cs
    ExcelTextPadding.cs
    ExcelTextQuote.cs
  Exporting/
    ExcelColumn.cs
    ExcelExportResult.cs
    ExcelSheetSpec.cs
    ExcelTextOptions.cs
    ExcelTextStyle.cs
    IExcelExporter.cs
    IExcelTemplateRenderer.cs
  Importing/
    ExcelFixedWidthField.cs
    ExcelImportOptions.cs
    ExcelImportRow.cs
    IExcelImporter.cs
```
