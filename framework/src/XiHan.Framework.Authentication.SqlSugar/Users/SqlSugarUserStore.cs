// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using SqlSugar;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Mapping;
using XiHan.Framework.Authentication.Users;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.Users;

/// <summary>
/// 用户 SqlSugar 存储
/// </summary>
/// <remarks>
/// 读写都限定在当前租户内。同一实例内对同一用户的多次读取返回同一个 <see cref="UserInfo"/> 实例。
/// </remarks>
public class SqlSugarUserStore : IUserStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly Dictionary<long, UserInfo> _loadedUsers = [];

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarUserStore(
        ISqlSugarClientResolver clientResolver,
        ICurrentTenant currentTenant,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _currentTenant = currentTenant;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 根据用户名获取用户，不区分大小写
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户信息，不存在时返回空</returns>
    public async Task<UserInfo?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var entity = await FindByUserNameAsync(GetClient(), GetTenantId(), username);

        return entity is null ? null : Track(entity);
    }

    /// <summary>
    /// 根据用户标识获取用户
    /// </summary>
    /// <param name="userId">用户标识</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户信息，不存在或标识不是正整数时返回空</returns>
    public async Task<UserInfo?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryParseUserId(userId, out var id))
        {
            return null;
        }

        var tenantId = GetTenantId();
        var entity = await GetClient().Queryable<SysAuthUser>()
            .Where(item => item.BasicId == id && item.TenantId == tenantId)
            .FirstAsync();

        return entity is null ? null : Track(entity);
    }

    /// <summary>
    /// 更新用户信息
    /// </summary>
    /// <param name="user">用户信息</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task UpdateUserAsync(UserInfo user, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 更新用户密码
    /// </summary>
    /// <param name="userId">用户标识</param>
    /// <param name="passwordHash">密码哈希</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task UpdatePasswordAsync(string userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 获取登录失败次数
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>失败次数</returns>
    public Task<int> GetFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 记录登录失败
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task IncrementFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 重置登录失败次数
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task ResetFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 设置账户锁定时间
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="lockoutEnd">锁定结束时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task SetLockoutEndAsync(string username, DateTime? lockoutEnd, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 获取账户锁定结束时间
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>锁定结束时间</returns>
    public Task<DateTime?> GetLockoutEndAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 添加用户
    /// </summary>
    /// <remarks>
    /// 密码哈希按原样写入。用户标识为空时生成新标识并回写到 <paramref name="user"/>。
    /// </remarks>
    /// <param name="user">用户信息</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户标识</returns>
    /// <exception cref="ArgumentException">用户名为空白，或用户标识不是正整数</exception>
    /// <exception cref="InvalidOperationException">当前租户内已存在同名用户（不区分大小写）</exception>
    public async Task<string> AddUserAsync(UserInfo user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            throw new ArgumentException("用户名不能为空", nameof(user));
        }

        long id;
        if (string.IsNullOrWhiteSpace(user.UserId))
        {
            id = _idGenerator.NextId();
        }
        else if (!TryParseUserId(user.UserId, out id))
        {
            throw new ArgumentException("用户ID必须是正整数", nameof(user));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = GetClient();
        var tenantId = GetTenantId();

        if (await FindByUserNameAsync(client, tenantId, user.Username) is not null)
        {
            throw new InvalidOperationException($"用户名 {user.Username} 已存在");
        }

        await client.Insertable(AuthUserMapper.ToEntity(user, id, tenantId)).ExecuteCommandAsync();

        user.UserId = id.ToString(CultureInfo.InvariantCulture);
        _loadedUsers[id] = user;

        return user.UserId;
    }

    private ISqlSugarClient GetClient()
    {
        return _clientResolver.GetClientForEntity<SysAuthUser>();
    }

    private long GetTenantId()
    {
        return _currentTenant.Id ?? 0;
    }

    private UserInfo Track(SysAuthUser entity)
    {
        if (_loadedUsers.TryGetValue(entity.BasicId, out var loaded))
        {
            return loaded;
        }

        var user = AuthUserMapper.ToUserInfo(entity);
        _loadedUsers[entity.BasicId] = user;

        return user;
    }

    private static async Task<SysAuthUser?> FindByUserNameAsync(ISqlSugarClient client, long tenantId, string username)
    {
        var normalizedUserName = AuthUserMapper.NormalizeUserName(username);

        return await client.Queryable<SysAuthUser>()
            .Where(item => item.TenantId == tenantId && item.NormalizedUserName == normalizedUserName)
            .FirstAsync();
    }

    private static bool TryParseUserId(string? userId, out long id)
    {
        return long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
