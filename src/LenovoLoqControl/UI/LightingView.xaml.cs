using System.Windows.Controls;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI;

public partial class LightingView : UserControl
{
    public LightingView(IHardwareBackend hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        InitializeComponent();
        KeyboardLighting.Attach(hardware.KeyboardLight);
    }
}
