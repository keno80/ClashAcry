using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        PageIdentity.Stamp(this);
    }
}
