// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Extensions.DependencyInjection;

namespace XiHan.Framework.Excel.Tests.Options;

/// <summary>
/// Excel 配置选项测试
/// </summary>
public class XiHanExcelOptionsTests
{
    /// <summary>
    /// 配置节名称固定为 XiHan:Excel
    /// </summary>
    [Fact]
    public void SectionName_使用框架配置节前缀()
    {
        Assert.Equal("XiHan:Excel", XiHanExcelOptions.SectionName);
    }

    /// <summary>
    /// 未赋值时六个选项取常量表的默认值
    /// </summary>
    [Fact]
    public void 默认值_取常量表()
    {
        var options = new XiHanExcelOptions();

        Assert.Equal(50_000, options.StreamingThreshold);
        Assert.Equal(500, options.AutoWidthSampleRows);
        Assert.Equal("utf-8-bom", options.DefaultEncodingName);
        Assert.Equal(1_000_000, options.MaxImportRows);
        Assert.Equal(ExcelConstants.DefaultMaxImportBytes, options.MaxImportBytes);
        Assert.Equal(ExcelConstants.MaxImportCompressionRatio, options.MaxImportCompressionRatio);
    }

    /// <summary>
    /// 解压比默认值是 <c>200</c>
    /// </summary>
    [Fact]
    public void 解压比默认值按实测安全窗口钉在两百()
    {
        Assert.Equal(200, ExcelConstants.MaxImportCompressionRatio);
        Assert.Equal(200, new XiHanExcelOptions().MaxImportCompressionRatio);
    }

    /// <summary>
    /// 不传配置时仍能解析出选项实例
    /// </summary>
    [Fact]
    public void AddXiHanExcel_未给配置时仍绑定选项()
    {
        var services = new ServiceCollection();

        services.AddXiHanExcel();

        var built = services.BuildServiceProvider();

        Assert.NotNull(built.GetRequiredService<IOptions<XiHanExcelOptions>>().Value);
    }

    /// <summary>
    /// services 为空时立即抛参数异常
    /// </summary>
    [Fact]
    public void AddXiHanExcel_拒绝空services()
    {
        Assert.Throws<ArgumentNullException>(() => XiHanExcelServiceCollectionExtensions.AddXiHanExcel(null!));
    }
}
