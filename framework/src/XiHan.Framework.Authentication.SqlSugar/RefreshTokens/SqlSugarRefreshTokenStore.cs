// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Authentication.Jwt;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Mapping;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.RefreshTokens;

/// <summary>
/// 刷新令牌 SqlSugar 存储
/// </summary>
/// <remarks>
/// 只保存令牌的 SHA-256 哈希；移除令牌时标记撤销而不删除行。每次调用在新的依赖注入作用域中解析数据库客户端。
/// </remarks>
public class SqlSugarRefreshTokenStore : IRefreshTokenStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="scopeFactory">作用域工厂</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="timeProvider">时间提供程序</param>
    public SqlSugarRefreshTokenStore(
        IServiceScopeFactory scopeFactory,
        IDistributedIdGenerator<long> idGenerator,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _idGenerator = idGenerator;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 保存刷新令牌
    /// </summary>
    /// <remarks>
    /// 过期时间不晚于当前时间时只把该令牌标记为已撤销，不插入、不抛出。
    /// </remarks>
    /// <param name="refreshToken">刷新令牌</param>
    /// <param name="subject">主体标识</param>
    /// <param name="expiresAt">过期时间</param>
    public void Save(string refreshToken, string? subject, DateTime expiresAt)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var now = GetUtcNow();
        var expiresAtUtc = StorageTime.ToUtc(expiresAt);

        using var scope = _scopeFactory.CreateScope();
        var client = GetClient(scope);

        if (expiresAtUtc <= now)
        {
            Revoke(client, RefreshTokenHasher.Hash(refreshToken), now);
            return;
        }

        client.Insertable(new SysAuthRefreshToken(_idGenerator.NextId())
        {
            TenantId = GetTenantId(scope),
            TokenHash = RefreshTokenHasher.Hash(refreshToken),
            Subject = subject,
            ExpiresAt = expiresAtUtc,
            CreatedTime = now
        }).ExecuteCommand();
    }

    /// <summary>
    /// 校验刷新令牌
    /// </summary>
    /// <param name="refreshToken">刷新令牌</param>
    /// <param name="subject">主体标识，为空白时不做绑定校验</param>
    /// <returns>令牌存在、未过期、未撤销且主体相符时返回 true</returns>
    public bool Validate(string refreshToken, string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        var tokenHash = RefreshTokenHasher.Hash(refreshToken);

        using var scope = _scopeFactory.CreateScope();

        var entry = GetClient(scope).Queryable<SysAuthRefreshToken>()
            .Where(item => item.TokenHash == tokenHash)
            .First();

        if (entry is null || entry.ExpiresAt <= GetUtcNow() || entry.RevokedTime is not null)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(subject) ||
               string.Equals(entry.Subject, subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// 移除刷新令牌，标记为已撤销
    /// </summary>
    /// <remarks>
    /// 令牌不存在时不做任何事；令牌已被撤销时抛出异常。
    /// </remarks>
    /// <param name="refreshToken">刷新令牌</param>
    /// <exception cref="InvalidOperationException">该令牌已被撤销</exception>
    public void Remove(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var tokenHash = RefreshTokenHasher.Hash(refreshToken);

        using var scope = _scopeFactory.CreateScope();
        var client = GetClient(scope);

        if (Revoke(client, tokenHash, GetUtcNow()) > 0)
        {
            return;
        }

        var alreadyRevoked = client.Queryable<SysAuthRefreshToken>()
            .Where(item => item.TokenHash == tokenHash && item.RevokedTime != null)
            .Any();

        if (alreadyRevoked)
        {
            throw new InvalidOperationException("刷新令牌已被撤销。");
        }
    }

    /// <summary>
    /// 把未撤销的令牌标记为已撤销
    /// </summary>
    /// <param name="client">当前客户端</param>
    /// <param name="tokenHash">令牌哈希</param>
    /// <param name="now">当前 UTC 时间</param>
    /// <returns>受影响的行数</returns>
    private static int Revoke(ISqlSugarClient client, string tokenHash, DateTime now)
    {
        return client.Updateable<SysAuthRefreshToken>()
            .SetColumns(item => new SysAuthRefreshToken { RevokedTime = now })
            .Where(item => item.TokenHash == tokenHash && item.RevokedTime == null)
            .ExecuteCommand();
    }

    private DateTime GetUtcNow()
    {
        return _timeProvider.GetUtcNow().UtcDateTime;
    }

    private static ISqlSugarClient GetClient(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<ISqlSugarClientResolver>().GetClientForEntity<SysAuthRefreshToken>();
    }

    private static long GetTenantId(IServiceScope scope)
    {
        return scope.ServiceProvider.GetService<ICurrentTenant>()?.Id ?? 0;
    }
}
