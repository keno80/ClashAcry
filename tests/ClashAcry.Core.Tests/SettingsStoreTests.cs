namespace ClashAcry.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ca-settings-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private SettingsStore Store() => new(AppPaths.ForTest(_dir));

    [Fact]
    public void FirstLoad_CreatesFileWithRandomSecretAndDefaults()
    {
        var s = Store().Load();

        Assert.Equal(9097, s.ControllerPort);
        Assert.Equal(7897, s.MixedPort);
        Assert.Equal(32, s.Secret.Length);
        Assert.Matches("^[0-9a-f]{32}$", s.Secret);
        Assert.True(File.Exists(AppPaths.ForTest(_dir).SettingsFile));
    }

    [Fact]
    public void Secret_StableAcrossReloads()
    {
        var first = Store().Load().Secret;
        Assert.Equal(first, Store().Load().Secret);
    }

    [Fact]
    public void Save_LeavesNoTempFiles_AndBacksUpPreviousVersion()
    {
        var store = Store();
        store.Update(s => s with { MixedPort = 7899 });
        var settings = AppPaths.ForTest(_dir).SettingsFile;
        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray();

        Assert.Contains("settings.json", files);
        Assert.DoesNotContain(files, f => f!.StartsWith(".tmp"));
        // 名字里的"备份"必须真被断言：第一次 Write 是 Move（无目标），第二次才 Replace 出 .bak
        Assert.True(File.Exists(settings + ".bak"), "覆盖写必须留下 .bak（spec §4.3）");
        Assert.Equal(7899, Store().Load().MixedPort);
    }

    [Fact]
    public void PartialFile_UsesDefaultsForAbsentKeys()
    {
        // 显式 [JsonPropertyName] + positional record 默认值：老文件缺键不能把 null/0 灌进来，
        // 也不能因为缺键抛（用户手改 settings.json 是预期通路，spec §4.3）。
        File.WriteAllText(AppPaths.ForTest(_dir).SettingsFile, """{"mixed_port":7899,"secret":"ab"}""");
        var s = Store().Load();

        Assert.Equal(7899, s.MixedPort);
        Assert.Equal("ab", s.Secret);
        Assert.Equal(9097, s.ControllerPort);
        Assert.Equal("127.0.0.1", s.ControllerHost);
    }

    [Fact]
    public void CorruptedFile_FallsBackWithBackupPreserved()
    {
        File.WriteAllText(AppPaths.ForTest(_dir).SettingsFile, "{ not json");
        var s = Store().Load();

        Assert.Equal(9097, s.ControllerPort);
        Assert.True(File.Exists(AppPaths.ForTest(_dir).SettingsFile + ".corrupt"));
    }
}
