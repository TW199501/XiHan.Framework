// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Auditing.SqlSugar.Mapping;

namespace XiHan.Framework.Auditing.SqlSugar.Tests;

/// <summary>
/// 日志映射测试
/// </summary>
public class AuditingLogMapperTests
{
    private static readonly DateTimeOffset CreatedTime = new(2026, 9, 21, 10, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// 操作日志映射保留全部字段
    /// </summary>
    [Fact]
    public void 操作日志映射保留全部字段()
    {
        var record = new OperationLogRecord
        {
            TraceId = "trace-1",
            SessionId = "session-1",
            UserId = 42,
            UserName = "tester",
            ControllerName = "Order",
            ActionName = "Create",
            Method = "POST",
            Path = "/Order",
            RequestParams = "{}",
            ResponseResult = "{\"ok\":true}",
            StatusCode = 200,
            ElapsedMilliseconds = 12,
            RemoteIp = "127.0.0.1",
            UserAgent = "xunit",
            ErrorMessage = null
        };

        var entity = AuditingLogMapper.ToEntity(record, 1001L, CreatedTime);

        Assert.Equal(1001L, entity.BasicId);
        Assert.Equal(CreatedTime, entity.CreatedTime);
        Assert.Equal("trace-1", entity.TraceId);
        Assert.Equal("session-1", entity.SessionId);
        Assert.Equal(42L, entity.UserId);
        Assert.Equal("tester", entity.UserName);
        Assert.Equal("Order", entity.ControllerName);
        Assert.Equal("Create", entity.ActionName);
        Assert.Equal("POST", entity.Method);
        Assert.Equal("/Order", entity.Path);
        Assert.Equal("{}", entity.RequestParams);
        Assert.Equal("{\"ok\":true}", entity.ResponseResult);
        Assert.Equal(200, entity.StatusCode);
        Assert.Equal(12L, entity.ElapsedMilliseconds);
        Assert.Equal("127.0.0.1", entity.RemoteIp);
        Assert.Equal("xunit", entity.UserAgent);
        Assert.Null(entity.ErrorMessage);
    }

    /// <summary>
    /// 登录日志的登录时间与创建时间各自独立
    /// </summary>
    [Fact]
    public void 登录日志的登录时间与创建时间各自独立()
    {
        var loginTime = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        var record = new LoginLogRecord
        {
            TraceId = "trace-2",
            UserId = 7,
            UserName = "tester",
            SessionId = "session-2",
            LoginResult = 1,
            Message = "成功",
            LoginIp = "10.0.0.1",
            UserAgent = "xunit",
            DeviceId = "device-1",
            LoginTime = loginTime
        };

        var entity = AuditingLogMapper.ToEntity(record, 1002L, CreatedTime);

        Assert.Equal(CreatedTime, entity.CreatedTime);
        Assert.Equal(loginTime, entity.LoginTime);
        Assert.Equal(1, entity.LoginResult);
        Assert.Equal("device-1", entity.DeviceId);
    }

    /// <summary>
    /// 异常日志映射保留异常三要素
    /// </summary>
    [Fact]
    public void 异常日志映射保留异常三要素()
    {
        var record = new ExceptionLogRecord
        {
            TraceId = "trace-3",
            StatusCode = 500,
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "boom",
            ExceptionStackTrace = "at X.Y()"
        };

        var entity = AuditingLogMapper.ToEntity(record, 1003L, CreatedTime);

        Assert.Equal("System.InvalidOperationException", entity.ExceptionType);
        Assert.Equal("boom", entity.ExceptionMessage);
        Assert.Equal("at X.Y()", entity.ExceptionStackTrace);
        Assert.Equal(500, entity.StatusCode);
    }

    /// <summary>
    /// 接口日志映射保留签名校验结果
    /// </summary>
    [Fact]
    public void 接口日志映射保留签名校验结果()
    {
        var record = new ApiLogRecord
        {
            TraceId = "trace-4",
            ClientId = "client-1",
            AppId = "app-1",
            IsSignatureValid = false,
            SignatureAlgorithm = "HMACSHA256",
            Method = "GET",
            Path = "/Api",
            StatusCode = 401,
            IsSuccess = false
        };

        var entity = AuditingLogMapper.ToEntity(record, 1004L, CreatedTime);

        Assert.False(entity.IsSignatureValid);
        Assert.Equal("HMACSHA256", entity.SignatureAlgorithm);
        Assert.False(entity.IsSuccess);
        Assert.Equal("client-1", entity.ClientId);
    }

    /// <summary>
    /// 访问日志映射保留响应大小与耗时
    /// </summary>
    [Fact]
    public void 访问日志映射保留响应大小与耗时()
    {
        var record = new AccessLogRecord
        {
            TraceId = "trace-5",
            ResourceName = "Home",
            Method = "GET",
            Path = "/",
            QueryString = "?a=1",
            StatusCode = 200,
            ElapsedMilliseconds = 8,
            ResponseSize = 2048
        };

        var entity = AuditingLogMapper.ToEntity(record, 1005L, CreatedTime);

        Assert.Equal("Home", entity.ResourceName);
        Assert.Equal("?a=1", entity.QueryString);
        Assert.Equal(8L, entity.ElapsedMilliseconds);
        Assert.Equal(2048L, entity.ResponseSize);
    }
}
