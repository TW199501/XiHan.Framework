// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Script.Core;
using XiHan.Framework.Script.Exceptions;
using XiHan.Framework.Script.Options;

namespace XiHan.Framework.Script.Tests.Core;

/// <summary>
/// 脚本执行安全边界测试
/// </summary>
public class ScriptSecurityBoundaryTests
{
    /// <summary>
    /// 进程内引擎无法隔离脚本时，严格模式拒绝执行
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenStrictModeIsEnabled_RejectsInProcessExecution()
    {
        using var engine = new ScriptEngine();
        const string code = "result = 7;";
        var cacheKey = Guid.NewGuid().ToString("N");
        var trustedResult = await engine.ExecuteAsync(
            code,
            ScriptOptions.Default.WithCacheKey(cacheKey));
        Assert.True(trustedResult.IsSuccess, trustedResult.ErrorMessage);

        var options = ScriptOptions.Default
            .WithCacheKey(cacheKey)
            .WithStrictSecurity()
            .DisableSecurity();

        var result = await engine.ExecuteAsync(code, options)
            .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var exception = Assert.IsType<ScriptSecurityException>(result.Exception);
        Assert.Equal("StrictModeUnsupported", exception.ViolationType);
    }
}
