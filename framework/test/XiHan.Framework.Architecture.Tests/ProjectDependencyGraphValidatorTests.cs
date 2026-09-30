// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Architecture.Tests.ProjectGraph;

namespace XiHan.Framework.Architecture.Tests;

/// <summary>
/// 依赖图校验规则测试
/// </summary>
public class ProjectDependencyGraphValidatorTests
{
    private static readonly DependencyPolicy NoExceptions = new([]);

    /// <summary>
    /// 同层与向下引用不报告
    /// </summary>
    [Fact]
    public void 同层与下层引用不报告()
    {
        var graph = new ProjectDependencyGraph(
            [Source("Core", 3), Source("Utils", 1), Source("Timing", 3)],
            [new ProjectEdge("Core", "Utils"), new ProjectEdge("Timing", "Core")]);

        Assert.Empty(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));
    }

    /// <summary>
    /// 向上引用报告起点、终点与所在层
    /// </summary>
    [Fact]
    public void 向上层引用报告起点与终点()
    {
        var graph = new ProjectDependencyGraph(
            [Source("Core", 3), Source("Web", 7)],
            [new ProjectEdge("Core", "Web")]);

        var violation = Assert.Single(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));

        Assert.Equal(ArchitectureViolationKind.UpwardReference, violation.Kind);
        Assert.Contains("Core（第 3 层 Layer3）", violation.Message);
        Assert.Contains("Web（第 7 层 Layer7）", violation.Message);
    }

    /// <summary>
    /// 例外清单登记的向上引用不报告
    /// </summary>
    [Fact]
    public void 例外清单登记的向上引用不报告()
    {
        var graph = new ProjectDependencyGraph(
            [Source("Core", 3), Source("Web", 7)],
            [new ProjectEdge("Core", "Web")]);
        var policy = new DependencyPolicy([new DependencyException("Core", "Web", "测试")]);

        Assert.Empty(ProjectDependencyGraphValidator.Validate(graph, policy));
    }

    /// <summary>
    /// 三个项目构成的环报告完整路径
    /// </summary>
    [Fact]
    public void 三节点环报告完整路径()
    {
        var graph = new ProjectDependencyGraph(
            [Source("A", 6), Source("B", 6), Source("C", 6)],
            [new ProjectEdge("A", "B"), new ProjectEdge("B", "C"), new ProjectEdge("C", "A")]);

        var violation = Assert.Single(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));

        Assert.Equal(ArchitectureViolationKind.Cycle, violation.Kind);
        Assert.Contains("A → B → C → A", violation.Message);
    }

    /// <summary>
    /// 例外清单放行了环上的向上引用，环仍被报告
    /// </summary>
    [Fact]
    public void 例外清单不能隐藏环()
    {
        var graph = new ProjectDependencyGraph(
            [Source("Low", 1), Source("High", 2)],
            [new ProjectEdge("Low", "High"), new ProjectEdge("High", "Low")]);
        var policy = new DependencyPolicy([new DependencyException("Low", "High", "测试")]);

        var violations = ProjectDependencyGraphValidator.Validate(graph, policy);

        Assert.Contains(violations, item => item.Kind == ArchitectureViolationKind.Cycle && item.Message.Contains("High → Low → High"));
        Assert.DoesNotContain(violations, item => item.Kind == ArchitectureViolationKind.UpwardReference);
    }

    /// <summary>
    /// 源码项目引用测试项目时报告
    /// </summary>
    [Fact]
    public void 源码项目引用测试项目报告()
    {
        var graph = new ProjectDependencyGraph(
            [Source("Core", 3), new ProjectNode("Core.Tests", ProjectArea.Test, null)],
            [new ProjectEdge("Core", "Core.Tests")]);

        var violation = Assert.Single(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));

        Assert.Equal(ArchitectureViolationKind.SourceReferencesNonSource, violation.Kind);
        Assert.Contains("Core.Tests", violation.Message);
    }

    /// <summary>
    /// 测试项目引用源码项目不受分层约束
    /// </summary>
    [Fact]
    public void 测试项目引用源码项目不报告()
    {
        var graph = new ProjectDependencyGraph(
            [Source("Web", 7), new ProjectNode("Core.Tests", ProjectArea.Test, null)],
            [new ProjectEdge("Core.Tests", "Web")]);

        Assert.Empty(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));
    }

    /// <summary>
    /// 契约包引用自身实现包时报告
    /// </summary>
    [Fact]
    public void 契约包引用自身实现包报告()
    {
        var graph = new ProjectDependencyGraph(
            [Source("EventBus.Abstractions", 6), Source("EventBus", 6)],
            [new ProjectEdge("EventBus.Abstractions", "EventBus")]);

        var violation = Assert.Single(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));

        Assert.Equal(ArchitectureViolationKind.AbstractionsReferencesImplementation, violation.Kind);
    }

    /// <summary>
    /// 未登记分层目录的源码项目报告
    /// </summary>
    [Fact]
    public void 未登记分层的源码项目报告()
    {
        var graph = new ProjectDependencyGraph([new ProjectNode("Orphan", ProjectArea.Source, null)], []);

        var violation = Assert.Single(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));

        Assert.Equal(ArchitectureViolationKind.UnregisteredLayer, violation.Kind);
        Assert.Contains("Orphan", violation.Message);
    }

    /// <summary>
    /// 例外清单中引用已不存在的条目报告过期
    /// </summary>
    [Fact]
    public void 例外清单中已不存在的引用报告过期()
    {
        var graph = new ProjectDependencyGraph([Source("Core", 3), Source("Web", 7)], []);
        var policy = new DependencyPolicy([new DependencyException("Core", "Web", "测试")]);

        var violation = Assert.Single(ProjectDependencyGraphValidator.Validate(graph, policy));

        Assert.Equal(ArchitectureViolationKind.StaleException, violation.Kind);
    }

    /// <summary>
    /// 引用依赖图中不存在的项目时报告
    /// </summary>
    [Fact]
    public void 引用不存在的项目报告()
    {
        var graph = new ProjectDependencyGraph([Source("Core", 3)], [new ProjectEdge("Core", "Missing")]);

        var violation = Assert.Single(ProjectDependencyGraphValidator.Validate(graph, NoExceptions));

        Assert.Equal(ArchitectureViolationKind.UnknownProject, violation.Kind);
    }

    private static ProjectNode Source(string name, int rank)
    {
        return new ProjectNode(name, ProjectArea.Source, new ProjectLayer(rank, $"Layer{rank}"));
    }
}
