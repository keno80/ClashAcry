using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI.Views;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
        PageIdentity.Stamp(this);
    }
}
