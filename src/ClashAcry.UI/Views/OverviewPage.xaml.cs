using ClashAcry.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI.Views;

/// <summary>
/// 概览页：内核状态/启停、模式、系统代理、内核更新进度与内核输出。
/// 页面自己**不持有任何状态**——所有判据都从外壳的 <see cref="OverviewViewModel"/> 派生属性读，
/// 这样切页不重置、设置页与概览页也不会把同一个值显示成两份（R-14d）。
/// </summary>
public sealed partial class OverviewPage : Page
{
    private readonly MainWindow _shell;

    /// <summary>代码回填开关/单选态时屏蔽事件回调，否则「渲染 → 触发 Toggled → 再发命令」会绕成循环。</summary>
    private bool _syncing;

    private bool _logOpen;

    public OverviewPage(MainWindow shell)
    {
        InitializeComponent();
        PageIdentity.Stamp(this);
        _shell = shell;
        shell.Overview.Changed += Render;
        Render();
    }

    private OverviewViewModel Vm => _shell.Overview;

    private void OnCoreToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetCore(CoreToggle.IsOn);              // 只发命令；界面状态等 core.state 事件与重读（spec §3 禁令 3）
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var mode = ReferenceEquals(sender, ModeGlobal) ? "global"
                 : ReferenceEquals(sender, ModeDirect) ? "direct"
                 : "rule";
        Vm.RequestMode(mode);
    }

    private void OnSysProxyToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Vm.RequestSysProxy(SysProxyToggle.IsOn);
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) => Vm.StartCoreUpdate();

    /// <summary>日志面板页内展开（裁定 R-14b）：Go 原文一行一条，不翻译、不裁剪、不加表情（spec §9）。</summary>
    private void OnLogButtonClick(object sender, RoutedEventArgs e)
    {
        _logOpen = !_logOpen;
        LogPanel.Visibility = _logOpen ? Visibility.Visible : Visibility.Collapsed;
        LogButton.Content = _logOpen ? "收起内核输出" : "展开内核输出";
        if (_logOpen) Render();                   // 展开这一刻补一次：折叠期间的日志更新没往这里写
    }

    private void Render()
    {
        var vm = Vm;
        _syncing = true;
        try
        {
            SetText(CoreStateLabel, vm.CoreStateText);
            CoreToggle.IsEnabled = vm.CoreToggleEnabled;
            CoreToggle.IsOn = vm.CoreOn;

            ModeRule.IsEnabled = ModeGlobal.IsEnabled = ModeDirect.IsEnabled = vm.ModeEnabled;
            ModeRule.IsChecked = vm.Mode == "rule";
            ModeGlobal.IsChecked = vm.Mode == "global";
            ModeDirect.IsChecked = vm.Mode == "direct";

            SysProxyToggle.IsEnabled = vm.SysProxyEnabled;
            SysProxyToggle.IsOn = vm.SysProxyOn;
            SetText(SysProxyStateLabel, vm.SysProxyText);

            UpdateButton.IsEnabled = vm.UpdateEnabled;
            SetContent(UpdateButton, vm.UpdateButtonText);
            UpdateProgress.Visibility = vm.UpdateBarVisible ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgress.Value = vm.UpdatePct;
            SetText(CoreVersionLabel, vm.CoreVersion);

            // 折叠时不写：日志面板收起后每 2 s 改一次 invisible 文本，是给 Task 16 的取样制造噪声。
            if (_logOpen) SetText(LogText, vm.LogText);
        }
        finally { _syncing = false; }
    }

    /// <summary>同值不写：TextBlock 换文本会触发 UIA 的 Name 变更事件，静默刷新不得留下这种痕迹（AGENTS.md 红线）。</summary>
    private static void SetText(TextBlock t, string value)
    {
        if (t.Text != value) t.Text = value;
    }

    private static void SetContent(ContentControl c, string value)
    {
        if (c.Content as string != value) c.Content = value;
    }
}
