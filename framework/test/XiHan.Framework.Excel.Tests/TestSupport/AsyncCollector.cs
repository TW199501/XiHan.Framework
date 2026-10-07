// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 异步序列的测试收集助手：逐条取进 <see cref="List{T}"/>
/// </summary>
/// <remarks>
/// 只收集、不做断言：枚举过程中抛出的异常原样上抛，由调用方的 <c>Assert.ThrowsAsync</c> 接住。
/// </remarks>
internal static class AsyncCollector
{
    /// <summary>
    /// 按原顺序取完异步序列并交出清单
    /// </summary>
    /// <typeparam name="T">元素类型</typeparam>
    /// <param name="source">异步序列</param>
    /// <returns>收集到的元素清单；序列为空时交出空清单</returns>
    internal static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var items = new List<T>();

        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }
}
