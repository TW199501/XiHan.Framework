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
    /// 注册一律走 <c>TryAddSingleton</c>：应用层先注册自己的实现，本方法就不覆盖它——换掉某一家写出或读取实现
    /// 是应用层的正当选择，注册顺序不该把它盖回去；同一个方法被重复调用（模块装配与手工调用并存）也不会
    /// 留下两份实现让解析结果看运气。
    /// </para>
    /// <para>
    /// 三个契约各自只绑一个门面：<see cref="IExcelExporter"/> 绑分派器、<see cref="IExcelImporter"/> 绑导入门面。
    /// 分派器与门面要的写出器、读实现另外各自注册一条，门面的构造函数收的是具体类型，两项都必需。
    /// 实现收的是裸 <see cref="XiHanExcelOptions"/>，而选项绑定只交回 <c>IOptions&lt;T&gt;</c>，
    /// 因此这里额外注册一条从 <c>IOptions&lt;T&gt;</c> 取 <c>Value</c> 的转接：漏掉它，报错只会出现在解析契约的那一刻，
    /// 且信息里不说明缺的是哪一条注册。
    /// </para>
    /// <para>
    /// 配置里的 <see cref="XiHanExcelOptions.MaxImportRows"/> 经构造参数进两个读实现，越界的配置值在解析这些服务时
    /// 抛出而不是被夹回上限——配置写错是装配期就该发现的问题，不等第一次导入。
    /// </para>
    /// <para>
    /// 编码提供程序注册（<c>Encoding.RegisterProvider</c>）是进程级且不可逆的副作用，可重复调用，
    /// 这里不判断是否已注册。文字档与定宽档需要的 Big5 等代码页编码全靠它。
    /// </para>
    /// <para>
    /// 本方法不注册日志提供器：文字导出与定宽读取两个实现收 <c>ILogger&lt;T&gt;</c>，宿主没装日志时解析它们会抛。
    /// 装配日志是核心模块与宿主的事，本包不代劳。
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

        // 选项绑定只交回 IOptions<T>，而下面的实现收裸选项对象；这一条转接缺了会在解析契约时报一句指向不明的错
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
