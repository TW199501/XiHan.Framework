// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Auditing.SqlSugar.Entities;

namespace XiHan.Framework.Auditing.SqlSugar.Mapping;

/// <summary>
/// 审计日志记录到实体的映射
/// </summary>
public static class AuditingLogMapper
{
    /// <summary>
    /// 把访问日志记录转换为实体
    /// </summary>
    /// <param name="record">访问日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>访问日志实体</returns>
    public static SysAccessLog ToEntity(AccessLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysAccessLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            SessionId = record.SessionId,
            ResourceName = record.ResourceName,
            Method = record.Method,
            Path = record.Path,
            QueryString = record.QueryString,
            RequestBody = record.RequestBody,
            StatusCode = record.StatusCode,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent,
            Referer = record.Referer,
            ElapsedMilliseconds = record.ElapsedMilliseconds,
            ResponseSize = record.ResponseSize,
            ErrorMessage = record.ErrorMessage
        };
    }

    /// <summary>
    /// 把接口日志记录转换为实体
    /// </summary>
    /// <param name="record">接口日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>接口日志实体</returns>
    public static SysApiLog ToEntity(ApiLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysApiLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            ClientId = record.ClientId,
            AppId = record.AppId,
            IsSignatureValid = record.IsSignatureValid,
            SignatureAlgorithm = record.SignatureAlgorithm,
            Method = record.Method,
            Path = record.Path,
            ApiName = record.ApiName,
            ControllerName = record.ControllerName,
            ActionName = record.ActionName,
            RequestParams = record.RequestParams,
            RequestBody = record.RequestBody,
            ResponseBody = record.ResponseBody,
            StatusCode = record.StatusCode,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent,
            Referer = record.Referer,
            ElapsedMilliseconds = record.ElapsedMilliseconds,
            RequestSize = record.RequestSize,
            ResponseSize = record.ResponseSize,
            IsSuccess = record.IsSuccess,
            ErrorMessage = record.ErrorMessage
        };
    }

    /// <summary>
    /// 把异常日志记录转换为实体
    /// </summary>
    /// <param name="record">异常日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>异常日志实体</returns>
    public static SysExceptionLog ToEntity(ExceptionLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysExceptionLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            Path = record.Path,
            Method = record.Method,
            ControllerName = record.ControllerName,
            ActionName = record.ActionName,
            StatusCode = record.StatusCode,
            ExceptionType = record.ExceptionType,
            ExceptionMessage = record.ExceptionMessage,
            ExceptionStackTrace = record.ExceptionStackTrace,
            RequestHeaders = record.RequestHeaders,
            RequestParams = record.RequestParams,
            RequestBody = record.RequestBody,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent
        };
    }

    /// <summary>
    /// 把登录日志记录转换为实体
    /// </summary>
    /// <param name="record">登录日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>登录日志实体</returns>
    public static SysLoginLog ToEntity(LoginLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysLoginLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            SessionId = record.SessionId,
            LoginResult = record.LoginResult,
            Message = record.Message,
            LoginIp = record.LoginIp,
            UserAgent = record.UserAgent,
            DeviceId = record.DeviceId,
            LoginTime = record.LoginTime
        };
    }

    /// <summary>
    /// 把操作日志记录转换为实体
    /// </summary>
    /// <param name="record">操作日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>操作日志实体</returns>
    public static SysOperationLog ToEntity(OperationLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysOperationLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            SessionId = record.SessionId,
            UserId = record.UserId,
            UserName = record.UserName,
            ControllerName = record.ControllerName,
            ActionName = record.ActionName,
            Method = record.Method,
            Path = record.Path,
            RequestParams = record.RequestParams,
            ResponseResult = record.ResponseResult,
            StatusCode = record.StatusCode,
            ElapsedMilliseconds = record.ElapsedMilliseconds,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent,
            ErrorMessage = record.ErrorMessage
        };
    }
}
