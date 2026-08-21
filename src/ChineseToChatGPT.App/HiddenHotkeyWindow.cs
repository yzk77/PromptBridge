using System.Windows;

namespace ChineseToChatGPT.App;

internal sealed class HiddenHotkeyWindow : Window
{
    public HiddenHotkeyWindow()
    {
        Width = 0;
        Height = 0;
        Left = -10_000;
        Top = -10_000;
        Opacity = 0;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ShowActivated = false;
    }
}
