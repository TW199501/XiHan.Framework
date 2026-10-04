// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text;

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 测试程序集的编码环境准备
/// </summary>
internal static class TestEncodingSetup
{
    /// <summary>
    /// 注册代码页编码提供程序，让 Big5 这类代码页编码在测试里可直接解析
    /// </summary>
    /// <remarks>
    /// 正式路径由 <c>AddXiHanExcel</c> 完成注册；测试直接构造导出器与助手、不经过模块装配，因此在测试程序集内
    /// 自己注册一次。注册是进程级且可重复调用的，这里不判断是否已注册。不注册时 Linux 构建机解析 Big5 会失败。
    /// </remarks>
    [ModuleInitializer]
    internal static void RegisterCodePagesEncodingProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
