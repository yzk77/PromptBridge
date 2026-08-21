using Microsoft.Win32;

namespace ChineseToChatGPT.App;

internal static class StartupManager
{
    private const string ValueName = "ChineseToChatGPT";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
