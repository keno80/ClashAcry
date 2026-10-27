namespace ClashAcry.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string RuntimeDir => Path.Combine(Root, "runtime");
    public string ImportsDir => Path.Combine(Root, "imports");
    public string CoreDir => Path.Combine(Root, "core");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ConfigFile => Path.Combine(RuntimeDir, "config.yaml");
    public string CoreExecutable => Path.Combine(CoreDir, "mihomo.exe");
    public string CoreVersionFile => Path.Combine(CoreDir, "version.txt");
    /// R-11e：下载缓存目录归 Paths 管（更新器与测试都从这里取路径，不许各自拼字符串），
    /// 并和其余目录一样由 <see cref="EnsureCreated"/> 建出来。
    public string CoreDownloadsDir => Path.Combine(CoreDir, "downloads");

    /// <summary>崩溃与诊断日志（R-12a：和其余目录一样归 Paths 管，UI 侧不许自己拼 `%LOCALAPPDATA%`）。</summary>
    public string LogsDir => Path.Combine(Root, "logs");

    private AppPaths(string root) => Root = root;

    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashAcry"));
    public static AppPaths ForTest(string dir) => new(dir);

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, RuntimeDir, ImportsDir, CoreDir, CoreDownloadsDir, LogsDir })
            Directory.CreateDirectory(d);
    }
}
