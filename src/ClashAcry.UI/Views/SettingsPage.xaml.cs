using ClashAcry.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI.Views;

/// <summary>
/// 设置页的材质/主题档：选中即写 <see cref="Services.UiPrefs"/> 并让窗口即时换装（spec §8：不重启、不重开窗）。
/// 降级档必须在这里说出来——用户选的是亚克力、实际却是纯色还不吭声，就违反「降级要明说」那条硬约束。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly MainWindow _shell;

    /// <summary>程序化回填单选态时屏蔽 Checked 回调，避免「同步 → 应用 → 再同步」绕圈。</summary>
    private bool _syncing;

    public SettingsPage(MainWindow shell)
    {
        InitializeComponent();
        PageIdentity.Stamp(this);
        _shell = shell;
        Sync();
    }

    private void OnBackdropChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var kind = ReferenceEquals(sender, BackdropMica) ? BackdropKind.Mica
                 : ReferenceEquals(sender, BackdropSolid) ? BackdropKind.Solid
                 : BackdropKind.Acrylic;
        _shell.ApplyBackdrop(kind);
        UpdateLabels();
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var theme = ReferenceEquals(sender, ThemeLight) ? ElementTheme.Light
                  : ReferenceEquals(sender, ThemeDark) ? ElementTheme.Dark
                  : ElementTheme.Default;
        _shell.ApplyTheme(theme);
        UpdateLabels();               // backdrop 的明暗跟着内容面走，生效档不变但文案要重算
    }

    private void Sync()
    {
        _syncing = true;
        var kind = _shell.Backdrop.Requested;
        BackdropAcrylic.IsChecked = kind == BackdropKind.Acrylic;
        BackdropMica.IsChecked = kind == BackdropKind.Mica;
        BackdropSolid.IsChecked = kind == BackdropKind.Solid;

        var theme = _shell.CurrentTheme;
        ThemeLight.IsChecked = theme == ElementTheme.Light;
        ThemeDark.IsChecked = theme == ElementTheme.Dark;
        ThemeSystem.IsChecked = theme == ElementTheme.Default;
        _syncing = false;

        UpdateLabels();
    }

    private void UpdateLabels()
    {
        BackdropSelectedLabel.Text = _shell.Backdrop.Requested.ToString();      // Acrylic / Mica / Solid
        BackdropEffectiveLabel.Text = _shell.Backdrop.EffectiveLabel;
    }
}
