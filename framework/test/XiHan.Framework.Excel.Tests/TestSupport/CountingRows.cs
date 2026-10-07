// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 记录被枚举次数的行集合，供自适应列宽的取样上限断言读取
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。集合保持惰性：每产出一个元素给外层计数加一，跨多次枚举累计。
/// </remarks>
public sealed class CountingRows : IEnumerable<SampleRow>
{
    private readonly int _total;

    /// <summary>
    /// 至今被交出的元素个数，跨多次枚举累计
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// 构造一个每次枚举产出 <paramref name="total" /> 行的惰性集合
    /// </summary>
    /// <param name="total">单次枚举计划产出的行数</param>
    public CountingRows(int total)
    {
        _total = total;
    }

    /// <summary>
    /// 逐行产出可预期的测试行，同时累计被枚举的行数
    /// </summary>
    /// <returns>行枚举器</returns>
    public IEnumerator<SampleRow> GetEnumerator()
    {
        for (var index = 0; index < _total; index++)
        {
            Count++;
            yield return new SampleRow
            {
                AwbNo = $"AWB{index}",
                Weight = index,
                Eta = new DateTime(2026, 1, 1).AddDays(index % 28)
            };
        }
    }

    /// <summary>
    /// 非泛型枚举器
    /// </summary>
    /// <returns>行枚举器</returns>
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}
