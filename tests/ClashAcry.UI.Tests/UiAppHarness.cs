using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace ClashAcry.UI.Tests;

/// <summary>
/// 起真窗口、读 UIA、收尾杀进程。四条本机实测约束：
/// ① exe 路径从测试程序集位置**向上找 sln** 再拼——`dotnet test` 的工作目录不是仓库根，
///    而且产物目录随调用方式在 <c>bin\Release\…</c> 与 <c>bin\x64\Release\…</c> 之间变（R-12e），
///    写死任一条都会在某天变成"进程启动失败"；
/// ② 启动前清掉残留的 <c>ClashAcry.UI</c>：单实例互斥体（<c>Local\ClashAcry.UI.v1</c>）还在别人手上时，
///    新进程会立刻 <c>Exit()</c>，FlaUI 那边只看到"窗口永远找不到"；
/// ③ 冷启动是 self-contained WinUI 3 加载运行时，实测到窗口出现要几秒，所以主窗口等待给 30 s；
/// ④ FlaUI 5 把 <c>element.Current.X</c> 扁平成了 <c>element.X</c>（R-12g，计划里的样例是 4.x 写法）。
/// </summary>
public sealed class UiAppHarness
{
    public string Exe { get; } = ResolveExe();

    /// <summary>启动 UI 并返回一个会话；用完必须 Dispose（Dispose 会杀掉进程）。</summary>
    public UiSession Launch(string? arg = null)
    {
        KillStrayUi();
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        if (arg is not null) psi.ArgumentList.Add(arg);
        var process = Process.Start(psi)!;
        var automation = new UIA3Automation();
        try
        {
            var app = Application.Attach(process.Id);
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
            Assert.NotNull(window);
            return new UiSession(process, automation, app, window!);
        }
        catch
        {
            automation.Dispose();
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>只起进程、不 attach、不清场——给单实例这类「要同时存在两个进程」的用例用。</summary>
    public Process StartBare(string? arg = null)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        if (arg is not null) psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    private static string ResolveExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClashAcry.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "从测试输出目录向上没找到 ClashAcry.sln");

        var candidates = new[]
        {
            Path.Combine(dir!.FullName, "src", "ClashAcry.UI", "bin", "x64", "Release"),
            Path.Combine(dir.FullName, "src", "ClashAcry.UI", "bin", "Release"),
        };
        var exe = candidates
            .Select(c => Path.Combine(c, "net10.0-windows10.0.19041.0", "win-x64", "ClashAcry.UI.exe"))
            .FirstOrDefault(File.Exists);
        Assert.True(exe is not null, $"未找到 UI exe（先跑 dotnet build ClashAcry.sln -c Release）");
        return exe!;
    }

    private static void KillStrayUi()
    {
        foreach (var p in Process.GetProcessesByName("ClashAcry.UI"))
            using (p)
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
                catch (Exception) { /* 已经自己退了 */ }
            }
    }
}

public sealed class UiSession : IDisposable
{
    private readonly Process _process;
    private readonly UIA3Automation _automation;

    public Application App { get; }
    public Window Window { get; }
    public Process Process => _process;

    internal UiSession(Process process, UIA3Automation automation, Application app, Window window)
    {
        _process = process;
        _automation = automation;
        App = app;
        Window = window;
    }

    /// <summary>按 AutomationId 找后代元素；找不到直接断言失败，调用点不用再套一层 null 检查。</summary>
    public AutomationElement Require(string automationId)
    {
        var e = Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        Assert.NotNull(e);
        return e!;
    }

    public void Dispose()
    {
        _automation.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception) { /* 已经自己退了 */ }
        _process.Dispose();
    }
}
