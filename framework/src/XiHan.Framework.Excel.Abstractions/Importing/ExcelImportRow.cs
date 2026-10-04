// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Importing;

/// <summary>
/// 导入读到的一行数据：源文件行号与「表头键 → 单元格值」
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Values"/> 的枚举顺序就是源文件里列的顺序，不是按键排序：读档方按列位取值，
/// 报表与逐列断言都依赖这个顺序。键的唯一性由导入器保证（重名表头加 <c>_n</c> 后缀，见
/// <see cref="ExcelImportOptions"/>）。
/// </para>
/// <para>
/// 值的 CLR 型别由来源决定，导入器不做统一化：二进制与 <c>.xlsx</c> 交回 <see cref="double"/>、
/// <see cref="DateTime"/>、<see cref="bool"/>、<see cref="string"/>，空格子是 <c>null</c>；
/// 文字档没有格型概念，一切值都是 <see cref="string"/>，空字段是空字串而不是 <c>null</c>。
/// 同一列在两种来源上型别可以不同，取值侧要按 <see cref="IConvertible"/> 或字串自己转。
/// </para>
/// </remarks>
/// <param name="RowNumber">
/// <para>
/// 本表内 <b>1 起始的行序号</b>，含表头行、<see cref="ExcelImportOptions.HeaderRowIndex"/> 丢掉的前导行与
/// <see cref="ExcelImportOptions.SkipEmptyRows"/> 跳过的空行——两种来源下它都<u>不随跳行重排</u>。
/// <see cref="ExcelImportOptions.HeaderRowIndex"/> 为 <c>0</c> 且 <see cref="ExcelImportOptions.HasHeader"/>
/// 为 <c>true</c> 时，第一条数据行的行号是 <c>2</c>。
/// </para>
/// <para>
/// <b>二进制档（</b><c>.xls</c>／<c>.xlsx</c><b>）它等于物理行号</b>：空行、稀疏行都会各交出一条记录，
/// 序号与簿里的行号一一对上。
/// </para>
/// <para>
/// <b>文字档（</b><c>.csv</c>／<c>.txt</c><b>）在字段值含换行时它是记录序号而不是编辑器行数</b>：
/// 被引号包住的换行属于同一条记录，后续记录的序号按记录递增，不会为它补跳一格。这不是可修的细节——
/// 读取器不暴露物理行位（<c>RowCount</c> 给的是结果集总行数、迭代期间不变），要拿到物理行号只能自造一层
/// CSV 词法去数换行，那会与 <c>FallbackEncoding</c>、<c>AutodetectSeparators</c> 那套解析规则分叉成两套语义。
/// </para>
/// <para>
/// 因此错误报表与本组件的文档都<u>不得</u>承诺「按编辑器里的行数找到那一行」；文字档上要定位到行，
/// 得按记录序号数，或先把值里的换行清掉再导。
/// </para>
/// </param>
/// <param name="Values">
/// 本行的取值集合，键为表头（无表头或未覆盖到的列为 <c>Col{列序}</c>），值的型别见本类型的说明；
/// 行数比表头窄时缺的列取 <c>null</c>，比表头宽时多出的列以 <c>Col{n}</c> 保留，不丢弃数据
/// </param>
public sealed record ExcelImportRow(int RowNumber, IReadOnlyDictionary<string, object?> Values);
