// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Importing;

namespace XiHan.Framework.Excel.Extensions.DependencyInjection;

/// <summary>
/// Excel 服务注册扩展
/// </summary>
public static class XiHanExcelServiceCollectionExtensions
{
    /// <summary>
    /// 添加 Excel 导入导出能力（绑定配置选项、注册导入导出契约与预设实现、注册代码页编码提供程序）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">应用配置，为空时只绑定默认选项</param>
    /// <returns>服务集合</returns>
    /// <remarks>
    /// <para>
    /// 注册一律走 <c>TryAddSingleton</c>：应用层先注册的实现不会被覆盖，重复调用也只留一份。
    /// </para>
    /// <para>
    /// <see cref="IExcelExporter"/> 绑分派器、<see cref="IExcelImporter"/> 绑导入门面、
    /// <see cref="IExcelTemplateRenderer"/> 绑模板渲染器；分派器与门面依赖的写出器、读实现另外各自注册。
    /// 另注册一条从 <c>IOptions&lt;T&gt;</c> 取 <c>Value</c> 的裸 <see cref="XiHanExcelOptions"/>。
    /// </para>
    /// <para>
    /// 配置里的 <see cref="XiHanExcelOptions.MaxImportRows"/> 经构造参数进两个读实现，越界的配置值在解析这些服务时
    /// 抛出，不夹回上限。
    /// </para>
    /// <para>
    /// 注册 <c>CodePagesEncodingProvider</c>（<c>Encoding.RegisterProvider</c>，进程级，可重复调用），
    /// 供文字档与定宽档使用 Big5 等代码页编码。
    /// </para>
    /// <para>
    /// 本方法不注册日志提供器：文字导出与定宽读取两个实现依赖 <c>ILogger&lt;T&gt;</c>，宿主没装日志时解析它们会抛。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddXiHanExcel(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<XiHanExcelOptions>();

        if (configuration is not null)
        {
            services.Configure<XiHanExcelOptions>(configuration.GetSection(XiHanExcelOptions.SectionName));
        }

        // 把 IOptions<T> 转成裸选项对象，供下面的实现注入
        services.TryAddSingleton(static provider => provider.GetRequiredService<IOptions<XiHanExcelOptions>>().Value);

        services.TryAddSingleton<IExcelExporter, ExcelExporter>();
        services.TryAddSingleton<IExcelImporter, ExcelImporter>();
        services.TryAddSingleton<IExcelTemplateRenderer, MiniExcelTemplateRenderer>();

        services.TryAddSingleton<ClosedXmlExporter>();
        services.TryAddSingleton<MiniExcelStreamExporter>();
        services.TryAddSingleton<DelimitedTextExporter>();

        services.TryAddSingleton(static provider => new ExcelDataReaderImporter(provider.GetRequiredService<XiHanExcelOptions>()));
        services.TryAddSingleton(static provider => new FixedWidthTextImporter(
            provider.GetRequiredService<XiHanExcelOptions>(),
            provider.GetRequiredService<ILogger<FixedWidthTextImporter>>()));

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        return services;
    }
}
