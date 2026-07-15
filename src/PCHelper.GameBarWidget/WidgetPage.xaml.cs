using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace PCHelper.GameBarWidget;

public sealed partial class WidgetPage : Page
{
    private readonly GameBarUserAgentBridge _bridge = new();

    public WidgetPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        StatusText.Text = "Checking the signed-in user agent…";
        GameBarBridgeSnapshot snapshot = await _bridge.GetOverlayStatusAsync(CancellationToken.None);
        StatusText.Text = snapshot.Summary;
        RtssText.Text = snapshot.Rtss ?? string.Empty;
        CaptureText.Text = snapshot.Capture ?? string.Empty;
    }
}
