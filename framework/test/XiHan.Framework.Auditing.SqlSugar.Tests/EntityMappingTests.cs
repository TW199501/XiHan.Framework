// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Tests;

/// <summary>
/// 日志实体映射测试
/// </summary>
public class EntityMappingTests
{
    /// <summary>
    /// 操作日志表名使用 sys_ 前缀并带三个分表变量
    /// </summary>
    [Fact]
    public void SysOperationLog_表名带前缀与三个分表变量()
    {
        var table = typeof(SysOperationLog).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_operation_log_{year}{month}{day}", table.TableName);
    }

    /// <summary>
    /// 操作日志按月分表
    /// </summary>
    [Fact]
    public void SysOperationLog_按月分表()
    {
        var split = typeof(SysOperationLog).GetCustomAttribute<SplitTableAttribute>();

        Assert.NotNull(split);
        Assert.Equal(SplitType.Month, split.SplitType);
    }

    /// <summary>
    /// 分表字段标注在 CreatedTime 上
    /// </summary>
    [Fact]
    public void SysOperationLog_分表字段为创建时间()
    {
        var property = typeof(SysOperationLog).GetProperty(nameof(SysOperationLog.CreatedTime));

        Assert.NotNull(property);
        Assert.NotNull(property!.GetCustomAttribute<SplitFieldAttribute>());
    }

    /// <summary>
    /// 实体实现分表标记接口
    /// </summary>
    [Fact]
    public void SysOperationLog_实现分表标记接口()
    {
        Assert.True(typeof(ISplitTableEntity).IsAssignableFrom(typeof(SysOperationLog)));
    }

    /// <summary>
    /// 列名使用 Pascal_Snake_Case
    /// </summary>
    [Fact]
    public void SysOperationLog_列名使用帕斯卡下划线()
    {
        var property = typeof(SysOperationLog).GetProperty(nameof(SysOperationLog.TraceId));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Trace_Id", column!.ColumnName);
    }
}
