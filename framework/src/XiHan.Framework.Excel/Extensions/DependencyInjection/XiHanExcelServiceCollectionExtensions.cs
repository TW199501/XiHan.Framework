// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Excel.Abstractions;

namespace XiHan.Framework.Excel.Extensions.DependencyInjection;

/// <summary>
/// Excel 服务注册扩展
/// </summary>
public static class XiHanExcelServiceCollectionExtensions
{
    /// <summary>
    /// 添加 Excel 导入导出能力（绑定配置选项并注册代码页编码提供程序）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">应用配置，为空时只绑定默认选项</param>
    /// <returns>服务集合</returns>
    /// <remarks>
    /// 编码提供程序注册是全局且可重复调用的，这里不判断是否已注册；
    /// 导入导出契约服务的注册在后续任务补充。
    /// </remarks>
    public static IServiceCollection AddXiHanExcel(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<XiHanExcelOptions>();

        if (configuration is not null)
        {
            services.Configure<XiHanExcelOptions>(configuration.GetSection(XiHanExcelOptions.SectionName));
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        return services;
    }
}
