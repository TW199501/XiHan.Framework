// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;

namespace XiHan.Framework.Workflow.SqlSugar.Mapping;

/// <summary>
/// 工作流 JSON 列的序列化工具，选项与 WorkflowValueConverter 的反序列化选项一致
/// </summary>
internal static class WorkflowJsonColumn
{
    /// <summary>
    /// 序列化为 JSON 文本
    /// </summary>
    /// <typeparam name="T">值类型</typeparam>
    /// <param name="value">值</param>
    /// <returns>JSON 文本</returns>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// 从 JSON 文本反序列化，空白文本返回新实例
    /// </summary>
    /// <typeparam name="T">值类型</typeparam>
    /// <param name="json">JSON 文本</param>
    /// <returns>反序列化结果</returns>
    public static T Deserialize<T>(string? json)
        where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new T();
        }

        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? new T();
    }
}
