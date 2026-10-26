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

    private AppPaths(string root) => Root = root;

    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashAcry"));
    public static AppPaths ForTest(string dir) => new(dir);

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, RuntimeDir, ImportsDir, CoreDir })
            Directory.CreateDirectory(d);
    }
}
