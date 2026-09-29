// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Tasks.BackgroundJobs.Abstractions;
using XiHan.Framework.Tasks.BackgroundJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Entities;
using XiHan.Framework.Tasks.SqlSugar.Mapping;
using XiHan.Framework.Tasks.SqlSugar.Options;
using XiHan.Framework.Timing;

namespace XiHan.Framework.Tasks.SqlSugar.BackgroundJobs;

/// <summary>
/// 后台作业存储的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 作业行写入默认布局的主库；存在事务型环境工作单元时，入队参与该工作单元的事务。
/// </remarks>
public class SqlSugarBackgroundJobStore : IBackgroundJobStore
{
    private readonly TasksHostClientAccessor _clientAccessor;
    private readonly IClock _clock;
    private readonly XiHanTasksSqlSugarOptions _options;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientAccessor">宿主上下文客户端访问器</param>
    /// <param name="clock">时钟</param>
    /// <param name="options">任务存储配置</param>
    public SqlSugarBackgroundJobStore(
        TasksHostClientAccessor clientAccessor,
        IClock clock,
        IOptions<XiHanTasksSqlSugarOptions> options)
    {
        _clientAccessor = clientAccessor;
        _clock = clock;
        _options = options.Value;
    }

    /// <summary>
    /// 按标识查找作业，已放弃的作业同样返回
    /// </summary>
    /// <param name="jobId">作业标识</param>
    /// <returns>作业信息，不存在则为 null</returns>
    public async Task<BackgroundJobInfo?> FindAsync(Guid jobId)
    {
        var entity = await _clientAccessor.ExecuteAsync(client => client.Queryable<SysBackgroundJob>()
            .Where(item => item.BasicId == jobId)
            .FirstAsync());

        return entity is null ? null : BackgroundJobMapper.ToJobInfo(entity);
    }

    /// <summary>
    /// 插入作业
    /// </summary>
    /// <param name="jobInfo">作业信息</param>
    /// <returns>任务</returns>
    public async Task InsertAsync(BackgroundJobInfo jobInfo)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);

        var entity = BackgroundJobMapper.ToEntity(jobInfo);

        await _clientAccessor.ExecuteAsync(client => client.Insertable(entity).ExecuteCommandAsync());
    }

    /// <summary>
    /// 获取待执行作业
    /// </summary>
    /// <param name="applicationName">应用名</param>
    /// <param name="maxResultCount">最大返回数量</param>
    /// <returns>待执行作业列表</returns>
    public Task<List<BackgroundJobInfo>> GetWaitingJobsAsync(string? applicationName, int maxResultCount)
    {
        return Task.FromResult(new List<BackgroundJobInfo>());
    }

    /// <summary>
    /// 删除作业
    /// </summary>
    /// <param name="jobId">作业标识</param>
    /// <returns>任务</returns>
    public async Task DeleteAsync(Guid jobId)
    {
        await _clientAccessor.ExecuteAsync(client => client.Deleteable<SysBackgroundJob>()
            .Where(item => item.BasicId == jobId)
            .ExecuteCommandAsync());
    }

    /// <summary>
    /// 更新作业并释放领取租约，作业不存在时不插入
    /// </summary>
    /// <param name="jobInfo">作业信息</param>
    /// <returns>任务</returns>
    public async Task UpdateAsync(BackgroundJobInfo jobInfo)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);

        var entity = BackgroundJobMapper.ToEntity(jobInfo);

        await _clientAccessor.ExecuteAsync(client => client.Updateable(entity).ExecuteCommandAsync());
    }
}
