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
/// 源文件里的 1 起始行号，含表头行、<see cref="ExcelImportOptions.HeaderRowIndex"/> 丢掉的前导行与
/// <see cref="ExcelImportOptions.SkipEmptyRows"/> 跳过的空行——它是给错误报表定位用的，不随跳过动作重排。
/// <see cref="ExcelImportOptions.HeaderRowIndex"/> 为 <c>0</c> 且 <see cref="ExcelImportOptions.HasHeader"/>
/// 为 <c>true</c> 时，第一条数据行的行号是 <c>2</c>。
/// </para>
/// <para>
/// 文字档里的「行」是<u>一条记录</u>而不是一个物理行：被引号包住的换行属于同一条记录，后续记录的行号按记录数递增，
/// 不会为它补跳一格。读取器不暴露物理行位（<c>RowCount</c> 给的是结果集总行数、迭代期间不变），
/// 因此这个口径无法再精确，写在这里以免被当成逐字节行号使用。
/// </para>
/// </param>
/// <param name="Values">
/// 本行的取值集合，键为表头（无表头或未覆盖到的列为 <c>Col{列序}</c>），值的型别见本类型的说明；
/// 行数比表头窄时缺的列取 <c>null</c>，比表头宽时多出的列以 <c>Col{n}</c> 保留，不丢弃数据
/// </param>
public sealed record ExcelImportRow(int RowNumber, IReadOnlyDictionary<string, object?> Values);
