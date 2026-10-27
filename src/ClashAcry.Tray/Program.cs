using System.Runtime.InteropServices;
using ClashAcry.Tray;

// Tray 宿主入口（替换 Task 2 的空壳占位）。
//   --no-tray：跳过 Win32NotifyIcon 与消息循环，只留命名管道，供 FlaUI/CI 在无交互环境跑宿主（计划 Task 10 Step 5）。
//
// 退出有三条路，都汇到 TrayHost.ShutdownAsync（那里有 Interlocked 闸门，走两遍也只做一遍）：
//   ① 托盘菜单「退出」——发布形态下唯一实测可用的一条；
//   ② Console.CancelKeyPress —— 只在进程有控制台时可达；
//   ③ SetConsoleCtrlHandler —— 关终端 / 注销 / 关机。
// 实测订正（Task 10 Step 6，本机 self-contained 发布产物）：OutputType=WinExe 的进程**不继承父控制台**，
// 从 Git Bash 起它之后 `kill -INT` 打不进去——②③ 都没被触发，进程照跑、管道照应答。
// 所以派发裁定 R-10k 里「WinExe 从终端起来时 CancelKeyPress 是可用的主路」这句是错的：
// 对最终用户，②③ 只在「从带控制台的宿主启动且显式接管控制台」时才有效，产品常态是 ①。
// 遗留（记进 Task 12）：--no-tray 的 CI 形态因此没有优雅出口，FlaUI 收尾只能硬杀进程。
// 不用 args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase)：`string[]` 隐式转 ReadOnlySpan<string>，
// 于是它绑到 MemoryExtensions.Contains<T>(ReadOnlySpan<T>, T, IEqualityComparer<T>) 而不是 Enumerable.Contains——
// 这个重载是 .NET 9 才加的，一旦产物里混进旧/被裁的 CoreLib（Task 10 真踩过：publish 目录残留 trimmed CoreLib）
// 就是启动期 MissingMethodException。用 Any 把这条隐式绑定摊平，少一个能咬人的面。
var noTray = args.Any(a => string.Equals(a, "--no-tray", StringComparison.OrdinalIgnoreCase));
using var cts = new CancellationTokenSource();
TrayHost? host = null;

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                       // 别让 CLR 直接拔线：必须走「清代理 + 停内核」这套收尾
    cts.Cancel();
    if (host is { } h) _ = h.ShutdownAsync();
};

host = new TrayHost(noTray);
if (!ConsoleExitHooks.Attach(() => host.ShutdownAsync()))
    Console.Error.WriteLine($"SetConsoleCtrlHandler 未装上（Win32Error {Marshal.GetLastWin32Error()}），退出只依赖 Console.CancelKeyPress");

try
{
    host.RunAsync(cts.Token).GetAwaiter().GetResult();
}
catch (OperationCanceledException)
{
    // 宿主被取消：收尾已经在 CancelKeyPress / --no-tray 的等待出口里做了，不当成故障刷屏。
}

// ── 类型声明跟在顶层语句之后（C# 顶层语句允许）──

/// <summary>
/// 控制台事件的退出钩子。裁定 R-10k 的硬要求：交给 Win32 的委托**必须**存在静态字段里——
/// 临时委托会被 GC 回收，回调进来就是访问违例（进程直接没，连一句遗言都留不下）。
/// </summary>
internal static class ConsoleExitHooks
{
    private static Func<Task>? _onExit;

    private static readonly NativeMethods.CtrlHandler Handler = OnCtrl;   // 静态字段持有，绝不让它被回收

    /// <returns>SetConsoleCtrlHandler 是否装成功（失败时调用方仍只依赖 Console.CancelKeyPress）。</returns>
    public static bool Attach(Func<Task> onExit)
    {
        _onExit = onExit;
        return NativeMethods.SetConsoleCtrlHandler(Handler, add: true);
    }

    private static bool OnCtrl(uint ctrlType)
    {
        // CTRL_C_EVENT 交给 Console.CancelKeyPress（CLR 自己会把它变成那个事件），这里只接
        // 关闭(2)/注销(5)/关机(6)——返回 false 等于「没处理」，让后面的处理器继续跑。
        if (ctrlType == NativeMethods.CTRL_C_EVENT) return false;
        try { Task.Run(() => _onExit?.Invoke()).Wait(TimeSpan.FromSeconds(5)); }   // 关闭事件只有 ~5 s 预算
        catch (Exception) { /* 关不干净也要退出：控制台事件里没有第二次机会 */ }
        return true;
    }
}
