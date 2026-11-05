using System.Windows;
using System.IO;

namespace BitChat.Windows;

public sealed partial class MainWindow
{
    public async Task RequestUpdateInstall()
    {
        await CheckForUpdates();
        if(availableUpdate==null){status.Text="BitChat is already up to date";return;}
        await InstallUpdate();
    }

    private async Task CheckForUpdates()
    {
        if(updater==null||updating)return;
        try
        {
            var found=await updater.CheckAsync();availableUpdate=found;
            if(updateButton!=null)
            {
                updateButton.Visibility=found==null?Visibility.Collapsed:Visibility.Visible;
                if(found!=null){updateButton.Content="● Update available · "+found.VersionText;updateButton.ToolTip="Download and install this GitHub release, then restart BitChat";}
            }
        }
        catch(Exception e){status.Text="Update check failed: "+e.Message;}
    }

    private async Task InstallUpdate()
    {
        if(updater==null||availableUpdate==null||updating)return;
        updating=true;if(updateButton!=null){updateButton.IsEnabled=false;updateButton.Content="Downloading update…";}
        try
        {
            status.Text="Downloading and verifying BitChat "+availableUpdate.VersionText+"…";
            string installer=await updater.DownloadVerifiedAsync(availableUpdate);
            status.Text="Verified installer. BitChat will close, update, and restart.";
            if(!updater.StartInstallerAfterExit(installer))throw new IOException("Could not start the update helper");
            updateCheckTimer.Stop();await Quit();
        }
        catch(Exception e)
        {
            status.Text="Update could not be installed: "+e.Message;updating=false;
            if(updateButton!=null){updateButton.IsEnabled=true;updateButton.Content="● Update available · "+availableUpdate.VersionText;}
        }
    }
}
