using System.Reflection;

namespace ClashAcry.Core.Tests;

/// <summary>
/// Task 2 脚手架冒烟测试：确认测试工程能解析到 Core 与 Contracts 程序集。
/// Contracts 被测试工程直接引用——Core 目前没有代码用到它，传递复制不会把
/// Contracts.dll 放进测试输出目录（实测 Assembly.Load 会 FileNotFound）。
/// 后续任务会在此工程内补充真正的单元测试。
/// </summary>
public class ScaffoldSmokeTests
{
    [Theory]
    [InlineData("ClashAcry.Core")]
    [InlineData("ClashAcry.Contracts")]
    public void ProjectAssembly_IsLoadable(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        Assert.NotNull(assembly);
    }
}
