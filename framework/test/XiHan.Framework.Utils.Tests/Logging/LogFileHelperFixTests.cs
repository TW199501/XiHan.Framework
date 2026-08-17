// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.RegularExpressions;
using XiHan.Framework.Utils.Logging;

namespace XiHan.Framework.Utils.Tests.Logging;

/// <summary>
/// LogFileHelper 修复验证测试
/// </summary>
[Collection(LoggingTestCollection.Name)]
public class LogFileHelperFixTests : IDisposable
{
    /// <summary>
    /// 匹配日志文件名结尾的滚动序号，基础文件没有这一段
    /// </summary>
    private static readonly Regex RotationIndexPattern = new(@"_(\d+)$");

    private readonly string _testLogDirectory;

    public LogFileHelperFixTests()
    {
        _testLogDirectory = Path.Combine(Path.GetTempPath(), "XiHanTests", "Fix", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testLogDirectory);

        // 设置测试配置
        LogFileHelper.SetLogDirectory(_testLogDirectory);
        LogFileHelper.SetMaxFileSize(1024 * 50); // 50KB文件大小便于测试
        LogFileHelper.SetBufferSize(10); // 小缓冲区便于快速刷新
        LogFileHelper.SetAsyncWriteEnabled(false); // 同步写入便于测试验证
    }

    /// <summary>
    /// 验证大量日志写入不会创建过多文件 - 重现用户问题的测试
    /// </summary>
    [Fact]
    public void LargeVolumeWrite_ShouldNotCreateExcessiveFiles()
    {
        // Arrange
        const int MessageCount = 10000; // 1万条消息测试
        var expectedMaxFiles = 50; // 预期最多50个文件（50KB * 50 = 2.5MB）

        // Act
        for (var i = 0; i < MessageCount; i++)
        {
            LogFileHelper.Handle($"应用启动中...{i + 1}/{MessageCount}");

            // 每1000条强制刷新一次
            if (i % 1000 == 0)
            {
                LogFileHelper.Flush();
                Thread.Sleep(10);
            }
        }

        LogFileHelper.Flush();
        Thread.Sleep(1000); // 等待所有写入完成

        // Assert
        var logFiles = Directory.GetFiles(_testLogDirectory, "*handle*.log");

        Console.WriteLine($"Created {logFiles.Length} handle log files for {MessageCount} messages");
        foreach (var file in logFiles.Take(10)) // 显示前10个文件信息
        {
            var fileInfo = new FileInfo(file);
            Console.WriteLine($"  {Path.GetFileName(file)}: {fileInfo.Length / 1024.0:F2} KB");
        }

        // 验证文件数量在合理范围内
        Assert.True(logFiles.Length <= expectedMaxFiles,
            $"Created {logFiles.Length} files, expected max {expectedMaxFiles}");

        // 验证所有消息都被写入
        var totalLines = 0;
        foreach (var file in logFiles)
        {
            var lines = File.ReadAllLines(file);
            totalLines += lines.Length;
        }

        Assert.Equal(MessageCount, totalLines);
    }

    /// <summary>
    /// 验证文件大小控制机制工作正常
    /// </summary>
    /// <remarks>
    /// 这是滚动选名按「已分配字节数」判断的回归防线：一旦 GetNextAvailableFileName
    /// 退回按磁盘大小挑选，批量异步写入下磁盘尚未落盘，选名会一直选回同一个文件，
    /// 本用例的「产出多个文件」与「除收尾外每个文件都接近上限」两条断言会同时变红。
    /// </remarks>
    [Fact]
    public void FileSizeControl_ShouldWorkCorrectly()
    {
        // Arrange
        LogFileHelper.SetMaxFileSize(1024); // 1KB 非常小的文件大小
        const int MessageCount = 500;
        var longMessage = new string('X', 100); // 100字符消息

        // Act
        for (var i = 0; i < MessageCount; i++)
        {
            LogFileHelper.Info($"Message-{i:D3}: {longMessage}");
        }

        LogFileHelper.Flush();
        Thread.Sleep(500);

        // Assert
        var logFiles = Directory.GetFiles(_testLogDirectory, "*info*.log");

        Console.WriteLine($"File size control test: {logFiles.Length} files created");

        // 应该创建多个文件
        Assert.True(logFiles.Length > 1, "Should create multiple files due to size limit");

        // 检查每个文件大小（除最后一个外都应该接近1KB）。
        // 「最后一个」必须按文件名里的滚动序号取，不能直接用 Directory.GetFiles 的返回顺序：
        // 那是字母序，_8.log、_9.log 会排在 _71.log 之后，于是真正收尾、只写了一部分的那个
        // 文件反而留在中间被校验，断言必然失败。
        var filesInCreationOrder = logFiles.OrderBy(GetRotationIndex).ToArray();

        foreach (var file in filesInCreationOrder.Take(filesInCreationOrder.Length - 1))
        {
            var fileInfo = new FileInfo(file);
            Console.WriteLine($"  {Path.GetFileName(file)}: {fileInfo.Length} bytes");
            Assert.True(fileInfo.Length >= 1024 * 0.8,
                $"File should be close to size limit: {Path.GetFileName(file)} 只有 {fileInfo.Length} 字节");
        }

        // 验证消息完整性
        var totalLines = 0;
        foreach (var file in logFiles)
        {
            var lines = File.ReadAllLines(file);
            totalLines += lines.Length;
        }

        Assert.Equal(MessageCount, totalLines);
    }

    /// <summary>
    /// 验证并发写入时文件管理的正确性
    /// </summary>
    [Fact]
    public async Task ConcurrentWrite_ShouldMaintainFileIntegrity()
    {
        // Arrange
        LogFileHelper.SetMaxFileSize(2048); // 2KB文件大小
        const int ThreadCount = 10;
        const int MessagesPerThread = 100;
        var totalMessages = ThreadCount * MessagesPerThread;

        // Act
        var tasks = new List<Task>();
        for (var i = 0; i < ThreadCount; i++)
        {
            var threadId = i;
            var task = Task.Run(() =>
            {
                for (var j = 0; j < MessagesPerThread; j++)
                {
                    LogFileHelper.Warn($"Thread-{threadId:D2}-Message-{j:D3}: Concurrent test data");
                }
            }, TestContext.Current.CancellationToken);
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        LogFileHelper.Flush();
        
        // Assert
        var logFiles = Directory.GetFiles(_testLogDirectory, "*warn*.log");

        Console.WriteLine($"Concurrent test: {logFiles.Length} files, {totalMessages} total messages");

        // 验证消息完整性
        var actualMessages = 0;
        var allContent = new List<string>();

        foreach (var file in logFiles)
        {
            var lines = File.ReadAllLines(file);
            actualMessages += lines.Length;
            allContent.AddRange(lines);
        }

        Assert.Equal(totalMessages, actualMessages);

        // 验证没有重复的消息
        var duplicates = allContent.GroupBy(x => x).Where(g => g.Count() > 1).ToList();
        Assert.Empty(duplicates);
    }

    /// <summary>
    /// 验证不同日志级别的文件管理
    /// </summary>
    [Fact]
    public void MultipleLogLevels_ShouldMaintainSeparateFiles()
    {
        // Arrange
        LogFileHelper.SetMaxFileSize(1024); // 1KB
        const int MessagesPerLevel = 100;
        var longMessage = new string('Y', 80);

        // Act
        for (var i = 0; i < MessagesPerLevel; i++)
        {
            LogFileHelper.Info($"Info-{i:D3}: {longMessage}");
            LogFileHelper.Warn($"Warn-{i:D3}: {longMessage}");
            LogFileHelper.Error($"Error-{i:D3}: {longMessage}");
            LogFileHelper.Success($"Success-{i:D3}: {longMessage}");
            LogFileHelper.Handle($"Handle-{i:D3}: {longMessage}");
        }

        LogFileHelper.Flush();
        
        // Assert
        var allLogFiles = Directory.GetFiles(_testLogDirectory, "*.log");
        var logLevels = new[] { "info", "warn", "error", "success", "handle" };

        Console.WriteLine($"Multiple levels test: {allLogFiles.Length} total files");

        foreach (var level in logLevels)
        {
            var levelFiles = allLogFiles.Where(f => Path.GetFileName(f).Contains(level)).ToArray();
            Assert.NotEmpty(levelFiles);

            var totalLines = 0;
            foreach (var file in levelFiles)
            {
                var lines = File.ReadAllLines(file);
                totalLines += lines.Length;
            }

            Assert.Equal(MessagesPerLevel, totalLines);
            Console.WriteLine($"  {level}: {levelFiles.Length} files, {totalLines} messages");
        }
    }

    /// <summary>
    /// 验证文件命名的一致性
    /// </summary>
    [Fact]
    public void FileNaming_ShouldBeConsistent()
    {
        // Arrange
        LogFileHelper.SetMaxFileSize(512); // 512字节，强制多文件
        const int MessageCount = 200;

        // Act
        for (var i = 0; i < MessageCount; i++)
        {
            LogFileHelper.Error($"Error message {i:D3} with some content to reach size limit");
        }

        LogFileHelper.Flush();
        Thread.Sleep(500);

        // Assert
        var logFiles = Directory.GetFiles(_testLogDirectory, "*error*.log")
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f)
            .ToArray();

        Console.WriteLine($"File naming test: {logFiles.Length} files");
        foreach (var file in logFiles)
        {
            Console.WriteLine($"  {file}");
        }

        // 验证文件命名规律。
        // LogFileHelper 生成的名字是 {yyyyMMdd}_error.log，滚动出的文件在其后追加 _{序号}，
        // 序号自 1 起连续递增；原断言写成 Assert.Contains("error_1.log", logFiles)，
        // 走的是集合「元素相等」重载，拿不带日期前缀的短名去比全名，永远不可能命中。
        //
        // 这里直接核对命名规则本身，而不是靠文件个数间接推断：滚动出几个文件取决于
        // 后台写盘线程能否跟上入队速度（机器越忙文件越少），命名规则却与负载无关。
        var namePattern = new Regex(@"^(?<date>\d{8})_error(?:_(?<index>\d+))?\.log$");

        var parsedNames = logFiles.Select(fileName =>
        {
            var match = namePattern.Match(fileName);
            Assert.True(match.Success, $"文件名不符合 {{日期}}_error[_{{序号}}].log 命名规则：{fileName}");
            return (
                Date: match.Groups["date"].Value,
                Index: match.Groups["index"].Success
                    ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)
                    : 0);
        }).ToArray();

        // 跨 UTC 零点时会出现两个日期前缀，各自独立编号，故按日期分组核对
        foreach (var sameDayFiles in parsedNames.GroupBy(item => item.Date))
        {
            var indexes = sameDayFiles.Select(item => item.Index).OrderBy(index => index).ToArray();

            // 0 代表未编号的基础文件：首条日志必然落在它上面，其后的编号必须是连续的 1、2、3……
            Assert.Equal(Enumerable.Range(0, indexes.Length), indexes);
        }
    }

    /// <summary>
    /// 性能回归测试 - 确保修复后性能仍然良好
    /// </summary>
    [Fact]
    public void PerformanceRegression_ShouldMaintainGoodPerformance()
    {
        // Arrange
        const int MaxFileSize = 10 * 1024; // 10KB
        const int MessageCount = 5000;
        LogFileHelper.SetMaxFileSize(MaxFileSize);
        var message = "Performance test message with moderate length content";

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < MessageCount; i++)
        {
            LogFileHelper.Success($"{message} #{i:D4}");
        }

        LogFileHelper.Flush();
        stopwatch.Stop();

        // Assert
        var throughput = MessageCount / stopwatch.Elapsed.TotalSeconds;
        var logFiles = Directory.GetFiles(_testLogDirectory, "*success*.log");

        Console.WriteLine($"Performance test:");
        Console.WriteLine($"  Messages: {MessageCount}");
        Console.WriteLine($"  Time: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Console.WriteLine($"  Throughput: {throughput:F0} messages/second");
        Console.WriteLine($"  Files created: {logFiles.Length}");

        // 性能应该保持良好。
        // 500 msg/s 这个下限不是拿本机跑分凑的：入队只做一次格式化加一次 Channel 写入，
        // 真正的耗时上限来自 Flush，它内部最多等 5 秒就返回。也就是说无论机器多忙，
        // 总耗时都被压在「入队时间 + 5 秒」里，5000 条要跌破 500 msg/s 得整整 10 秒。
        // 它拦的是量级事故（例如退回逐条同步 IO），不是几个百分点的波动，
        // 因此在双核且被其它测试项目抢占的 CI 上同样成立。
        Assert.True(throughput > 500, $"Throughput too low: {throughput:F0} msg/s");

        // 文件数量应该合理。
        // 原断言写死 “< 10”，与本用例自己设的 10KB 上限自相矛盾：5000 条约 460KB，
        // 只要滚动正常就必然产出 40 多个文件，这个上限过去只在滚动塌缩成单文件时才成立。
        // 改为由「实际落盘字节数 ÷ 单文件上限」推出理论文件数，再放一倍余量：
        // 界限跟着配置走，量的是「有没有异常碎片化」而不是机器有多快。
        // 只设上限不设下限，是因为滚动选名依据的是磁盘大小，后台写盘跟不上时
        // 基础文件会超限膨胀，实际文件数可以低于理论值（见 FileSizeControl_ShouldWorkCorrectly 的跳过说明）。
        var totalBytes = logFiles.Sum(file => new FileInfo(file).Length);
        var expectedFiles = (int)Math.Ceiling(totalBytes / (double)MaxFileSize);
        var maxAcceptableFiles = (expectedFiles * 2) + 2;

        Assert.True(logFiles.Length <= maxAcceptableFiles,
            $"文件过于碎片化：{totalBytes} 字节按 {MaxFileSize} 字节上限约需 {expectedFiles} 个文件，实际 {logFiles.Length} 个");
    }

    /// <summary>
    /// 取日志文件名里的滚动序号，基础文件（无序号）记作 0
    /// </summary>
    /// <remarks>
    /// 序号即创建顺序，而 <see cref="Directory.GetFiles(string, string)"/> 给的是字母序，
    /// _10 排在 _2 之前、_8 排在 _71 之后，拿它当创建顺序会取错「最后一个文件」。
    /// </remarks>
    /// <param name="filePath">日志文件路径</param>
    /// <returns>滚动序号</returns>
    private static int GetRotationIndex(string filePath)
    {
        var match = RotationIndexPattern.Match(Path.GetFileNameWithoutExtension(filePath));
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    public void Dispose()
    {
        try
        {
            LogFileHelper.Flush();
            
            if (Directory.Exists(_testLogDirectory))
            {
                Directory.Delete(_testLogDirectory, true);
            }
        }
        catch (Exception)
        {
            // 忽略清理异常
        }

        // 恢复默认配置
        LogFileHelper.SetMaxFileSize(10 * 1024 * 1024); // 10MB
        LogFileHelper.SetBufferSize(100);
        LogFileHelper.SetAsyncWriteEnabled(true);
        GC.SuppressFinalize(this);
    }
}
