// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 实体约定测试
/// </summary>
public class EntityConventionTests
{
    /// <summary>
    /// 全部实体的表名以 sys_authz_ 开头
    /// </summary>
    [Fact]
    public void 全部实体的表名以sys_authz_开头()
    {
        Assert.NotEmpty(AuthorizationTestContext.EntityTypes);

        foreach (var entityType in AuthorizationTestContext.EntityTypes)
        {
            var tableName = entityType.GetCustomAttribute<SugarTable>()!.TableName;

            Assert.StartsWith("sys_authz_", tableName, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 全部实体声明 Authorization 建表分组且只建在主库
    /// </summary>
    [Fact]
    public void 全部实体声明Authorization建表分组()
    {
        foreach (var entityType in AuthorizationTestContext.EntityTypes)
        {
            var attribute = entityType.GetCustomAttribute<TableInitializationAttribute>(inherit: true);

            Assert.NotNull(attribute);
            Assert.True(attribute.Enabled);
            Assert.Equal("Authorization", attribute.Group);
            Assert.False(attribute.IncludeModuleConnections);
        }
    }

    /// <summary>
    /// 关联实体按租户严格隔离
    /// </summary>
    /// <param name="entityType">实体类型</param>
    [Theory]
    [InlineData(typeof(SysAuthzUserPermission))]
    [InlineData(typeof(SysAuthzRolePermission))]
    public void 关联实体按租户严格隔离(Type entityType)
    {
        Assert.True(typeof(IStrictMultiTenantEntity).IsAssignableFrom(entityType));
    }

    /// <summary>
    /// 定义实体不按租户隔离
    /// </summary>
    /// <param name="entityType">实体类型</param>
    [Theory]
    [InlineData(typeof(SysAuthzPermission))]
    public void 定义实体不按租户隔离(Type entityType)
    {
        Assert.False(typeof(IMultiTenantEntity).IsAssignableFrom(entityType));
    }

    /// <summary>
    /// 全部实体的表都能建出来
    /// </summary>
    [Fact]
    public void 全部实体的表都能建出来()
    {
        using var context = new AuthorizationTestContext();

        var tableNames = context.Client.DbMaintenance.GetTableInfoList(false)
            .Select(table => table.Name)
            .ToList();

        foreach (var entityType in AuthorizationTestContext.EntityTypes)
        {
            var expected = entityType.GetCustomAttribute<SugarTable>()!.TableName;

            Assert.Contains(tableNames, name => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 权限名称唯一
    /// </summary>
    [Fact]
    public async Task 权限名称唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewPermission(context, "User.Create")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewPermission(context, "User.Create")).ExecuteCommandAsync());
    }

    /// <summary>
    /// 用户权限授予在同一租户内唯一，不同租户可重复
    /// </summary>
    [Fact]
    public async Task 用户权限授予在同一租户内唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewUserPermission(context, 0, "u1", "A")).ExecuteCommandAsync();
        await context.Client.Insertable(NewUserPermission(context, 1, "u1", "A")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewUserPermission(context, 0, "u1", "A")).ExecuteCommandAsync());
    }

    /// <summary>
    /// 角色权限授予在同一租户内唯一，不同租户可重复
    /// </summary>
    [Fact]
    public async Task 角色权限授予在同一租户内唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewRolePermission(context, 0, "r1", "A")).ExecuteCommandAsync();
        await context.Client.Insertable(NewRolePermission(context, 1, "r1", "A")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewRolePermission(context, 0, "r1", "A")).ExecuteCommandAsync());
    }

    private static SysAuthzPermission NewPermission(AuthorizationTestContext context, string name)
    {
        return new SysAuthzPermission(context.IdGenerator.NextId())
        {
            PermissionName = name,
            DisplayName = name,
            IsEnabled = true
        };
    }

    private static SysAuthzUserPermission NewUserPermission(AuthorizationTestContext context, long tenantId, string userId, string permissionName)
    {
        return new SysAuthzUserPermission(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            UserId = userId,
            PermissionName = permissionName
        };
    }

    private static SysAuthzRolePermission NewRolePermission(AuthorizationTestContext context, long tenantId, string roleId, string permissionName)
    {
        return new SysAuthzRolePermission(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            RoleId = roleId,
            PermissionName = permissionName
        };
    }
}
