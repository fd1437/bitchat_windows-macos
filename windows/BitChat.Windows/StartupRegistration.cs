using Microsoft.Win32;
using System.IO;

namespace BitChat.Windows;

internal static class StartupRegistration
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool Enabled
    {
        get{using var key=Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("BitChat") is string command&&command==Command;}
    }
    private static string Command=>"\""+(Environment.ProcessPath??throw new IOException("Application path unavailable"))+"\" --background";
    internal static void Set(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey);
        if(enabled)key.SetValue("BitChat",Command,RegistryValueKind.String);else key.DeleteValue("BitChat",false);
    }
    // Kept outside chat identities so panic/reset does not repeat onboarding.
    internal static bool Prompted
    {
        get{using var key=Registry.CurrentUser.OpenSubKey(@"Software\BitChat");return key?.GetValue("StartupPrompted") is int value&&value==1;}
        set{using var key=Registry.CurrentUser.CreateSubKey(@"Software\BitChat");key.SetValue("StartupPrompted",value?1:0,RegistryValueKind.DWord);}
    }
}
