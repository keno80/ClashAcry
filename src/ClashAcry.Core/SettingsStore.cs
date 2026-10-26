using System.Security.Cryptography;
using System.Text.Json;

namespace ClashAcry.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private static readonly Settings Def = new();   // 默认值只有这一处来源
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths) { _paths = paths; _paths.EnsureCreated(); }

    public Settings Load()
    {
        lock (_gate)   // Monitor 可重入，Update→Load 不构成死锁；换 SemaphoreSlim 才会炸
        {
            if (File.Exists(_paths.SettingsFile))
            {
                Settings? fromDisk = null;
                try { fromDisk = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_paths.SettingsFile), Opts); }
                catch (JsonException)   // 类型不对（"port":"abc"）STJ 抛的也是 JsonException
                {
                    // 损坏原件先留 .corrupt 再回落默认：secret 绝不能被静默清掉（spec §4.3）
                    File.Copy(_paths.SettingsFile, _paths.SettingsFile + ".corrupt", overwrite: true);
                }
                if (fromDisk is null) { var freshNull = New(); Write(freshNull); return freshNull; }
                var healed = Heal(fromDisk);
                if (healed != fromDisk) Write(healed);   // 修补必须落盘，否则每次 Load 都换一个新 secret
                return healed;
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

    private static Settings New() => new(Secret: NewSecret());

    /// 「能解析」不等于「可用」：手改 settings.json 是 spec §4.3 的预期通路，null/空串必须回落默认值，
    /// secret 不合 32hex 格式则换新值（旧值仍可从 `.bak`/`.corrupt` 找回）。不落空 secret 的理由是硬的：
    /// `secret: ""` 注进 config.yaml 等于把无鉴权的 controller 裸暴露在 127.0.0.1:9097。
    private static Settings Heal(Settings s) => s with
    {
        ControllerHost = string.IsNullOrWhiteSpace(s.ControllerHost) ? Def.ControllerHost : s.ControllerHost!,
        ControllerPort = s.ControllerPort is <= 0 or > 65535 ? Def.ControllerPort : s.ControllerPort,
        MixedPort = s.MixedPort is <= 0 or > 65535 ? Def.MixedPort : s.MixedPort,   // v1 不支持 0=禁用 mixed port
        Secret = IsHex32(s.Secret) ? s.Secret : NewSecret(),
        DelayTestUrl = string.IsNullOrWhiteSpace(s.DelayTestUrl) ? Def.DelayTestUrl : s.DelayTestUrl!,
    };

    private static bool IsHex32(string? v) =>
        v is { Length: 32 } && System.Text.RegularExpressions.Regex.IsMatch(v, "^[0-9a-f]{32}$");
    private static string NewSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private void Write(Settings s)
    {
        // 同目录临时文件：跨目录 File.Replace 会失败（硬约束：原子覆盖写 + 保留 .bak）
        var tmp = Path.Combine(_paths.Root, $".tmp-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
            var target = _paths.SettingsFile;
            if (File.Exists(target)) File.Replace(tmp, target, target + ".bak");
            else File.Move(tmp, target);
        }
        finally
        {
            // 成功的路上 tmp 已经被 Move/Replace 吃掉；失败（目标被钉住）时 best-effort 清掉，
            // 否则 `.tmp-*` 会留在 Root 里，把「无 .tmp 残留」这条断言变成偶发红灯。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }
}
