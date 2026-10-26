using System.Security.Cryptography;
using System.Text.Json;

namespace ClashAcry.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths) { _paths = paths; _paths.EnsureCreated(); }

    public Settings Load()
    {
        lock (_gate)
        {
            if (File.Exists(_paths.SettingsFile))
            {
                try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_paths.SettingsFile), Opts) ?? New(); }
                catch (JsonException)
                {
                    // 损坏原件先留 .corrupt 再回落默认：secret 绝不能被静默清掉（spec §4.3）
                    File.Copy(_paths.SettingsFile, _paths.SettingsFile + ".corrupt", overwrite: true);
                }
            }
            var fresh = New();
            Write(fresh);
            return fresh;
        }
    }

    public Settings Update(Func<Settings, Settings> mutate)
    {
        lock (_gate) { var next = mutate(Load()); Write(next); return next; }
    }

    public void Save(Settings s) { lock (_gate) Write(s); }

    private static Settings New() =>
        new(Secret: Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());

    private void Write(Settings s)
    {
        // 同目录临时文件：跨目录 File.Replace 会失败（硬约束：原子覆盖写 + 保留 .bak）
        var tmp = Path.Combine(_paths.Root, $".tmp-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        var target = _paths.SettingsFile;
        if (File.Exists(target)) File.Replace(tmp, target, target + ".bak");
        else File.Move(tmp, target);
    }
}
