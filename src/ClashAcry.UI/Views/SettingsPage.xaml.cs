using ClashAcry.Contracts;
using ClashAcry.Core;
using ClashAcry.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI.Views;

/// <summary>
/// 设置页的材质/主题档：选中即写 <see cref="UiPrefs"/> 并让窗口即时换装（spec §8：不重启、不重开窗）。
/// 降级档必须在这里说出来——用户选的是亚克力、实际却是纯色还不吭声，就违反「降级要明说」那条硬约束。
/// 订阅导入区与测速地址也在这里：导入只在 Tray 执行（UI 是 <c>config.yaml</c> 的非写者），
/// 测速地址的回显走外壳 VM 的 <c>settings.changed</c> 快照，不自己留一份。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly MainWindow _shell;

    /// <summary>程序化回填单选/下拉态时屏蔽回调，避免「同步 → 应用 → 再同步」绕圈。</summary>
    private bool _syncing;

    /// <summary>下拉项只在列表真换过时重挂 ItemsSource（重挂会闪一下选中态）。</summary>
    private IReadOnlyList<string>? _optionsBound;

    public SettingsPage(MainWindow shell)
    {
        InitializeComponent();
        PageIdentity.Stamp(this);
        _shell = shell;
        shell.Overview.Changed += RenderState;
        Sync();
        RenderState();
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

    // ---- 订阅导入（URL 与本地 yaml 两条通路，回包形状同一个） ------------------------------------

    private async void OnImportUrlClick(object sender, RoutedEventArgs e)
    {
        var url = ImportUrlBox.Text.Trim();
        if (url.Length == 0) { SetResult("请先填订阅链接"); return; }
        await ImportAsync(new ImportRequest(url, null));
    }

    private async void OnImportFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _shell.Handle);   // 不挂句柄 = unpackaged 下点了没反应
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;                        // 用户自己取消，不是故障，不出红字
        await ImportAsync(new ImportRequest(null, file.Path));
    }

    /// <summary>
    /// 导入的进行中反馈走行内文案与按钮自身，绝不弹阻塞框（用户红线）。
    /// 同时把外壳的轮询暂停：PipeServer 每条连接的请求是**串行**的，一条分钟级的导入会把同一条连接上的
    /// 状态读取全堵住（R-14c），进度与 <c>settings.changed</c> 走的是广播那条写路径，不受影响。
    /// </summary>
    private async Task ImportAsync(ImportRequest request)
    {
        SetBusy(true);
        _shell.Overview.BeginExternalWork();
        try
        {
            var reply = await _shell.Link.RequestAsync<ImportReply>(CmdTypes.SubImport, request);
            SetResult(reply switch
            {
                null => "托盘未响应",
                var r when r.Reason is not null => $"导入失败：{r.Reason}",
                var r => $"已导入：{r.ConfigFile}",
            });
        }
        catch (TrayCommandException ex) { SetResult($"导入失败：{ex.Message}"); }
        finally { _shell.Overview.EndExternalWork(); SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        ImportButton.IsEnabled = !busy;
        ImportFileButton.IsEnabled = !busy;
        if (ImportButton.Content as string != (busy ? "导入中…" : "导入订阅"))
            ImportButton.Content = busy ? "导入中…" : "导入订阅";
    }

    private void SetResult(string text)
    {
        if (ImportResult.Text != text) ImportResult.Text = text;
    }

    // ---- 测速地址：写走 settings.update，回显只认快照 -------------------------------------------

    private void OnDelayUrlSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (DelayUrlCombo.SelectedItem is string url) _shell.Overview.RequestDelayUrl(url);
    }

    private void RenderState()
    {
        var vm = _shell.Overview;
        _syncing = true;
        try
        {
            if (!ReferenceEquals(_optionsBound, vm.DelayUrlOptions))
            {
                _optionsBound = vm.DelayUrlOptions;
                DelayUrlCombo.ItemsSource = vm.DelayUrlOptions;
            }
            if (vm.DelayUrl.Length > 0 && DelayUrlCombo.SelectedItem as string != vm.DelayUrl)
                DelayUrlCombo.SelectedItem = vm.DelayUrl;
            DelayUrlCombo.IsEnabled = vm.SysProxyEnabled;         // 连不上托盘时改不了的东西不许做出能改的样子
        }
        finally { _syncing = false; }
    }
}
