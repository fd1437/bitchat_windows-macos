using System.Windows;
using System.IO;

namespace BitChat.Windows;

public sealed partial class MainWindow
{
    public async Task RequestUpdateInstall()
    {
        if(closing)return;await CheckForUpdates();
        if(closing)return;
        if(availableUpdate==null){status.Text="BitChat is already up to date";return;}
        await InstallUpdate();
    }

    private async Task CheckForUpdates()
    {
        if(closing||updater==null||updating)return;
        try
        {
            var found=await updater.CheckAsync();if(closing)return;availableUpdate=found;
            if(updateButton!=null)
            {
                updateButton.Visibility=found==null?Visibility.Collapsed:Visibility.Visible;
                if(found!=null){updateButton.Content="● Update available · "+found.VersionText;updateButton.ToolTip="Download and install this GitHub release, then restart BitChat";}
            }
        }
        catch(Exception e){if(!closing)status.Text="Update check failed: "+e.Message;}
    }

    private async Task InstallUpdate()
    {
        if(closing||updater==null||availableUpdate==null||updating)return;
        updating=true;if(updateButton!=null){updateButton.IsEnabled=false;updateButton.Content="Downloading update…";}
        try
        {
            status.Text="Downloading and verifying BitChat "+availableUpdate.VersionText+"…";
            if(updateProgress!=null){updateProgress.Visibility=Visibility.Visible;updateProgress.IsIndeterminate=true;}
            var progress=new Progress<DownloadProgress>(value=>
            {
                if(closing)return;double percent=value.Verified?100:value.Total is >0?Math.Min(99,100d*value.Bytes/value.Total.Value):0;
                string label=value.Verified?"Verified · 100%":value.Total is >0?$"Downloading · {percent:0}%":$"Downloading · {value.Bytes/1048576d:0.0} MB";
                if(updateButton!=null)updateButton.Content=label;
                if(updateProgress!=null){updateProgress.IsIndeterminate=value.Total is null or <=0;updateProgress.Value=percent;}
                status.Text=label;
            });
            var installerUpdater=updater;
            string installer=await installerUpdater.DownloadVerifiedAsync(availableUpdate,progress);
            if(closing)return;
            status.Text="Verified installer. BitChat will close, update, and restart.";
            if(!installerUpdater.StartInstallerAfterExit(installer))throw new IOException("Could not start the update helper");
            updateCheckTimer.Stop();await Quit();
        }
        catch(Exception e)
        {
            if(closing)return;status.Text="Update could not be installed: "+e.Message;updating=false;if(updateProgress!=null)updateProgress.Visibility=Visibility.Collapsed;
            if(updateButton!=null){updateButton.IsEnabled=true;updateButton.Content="● Update available · "+availableUpdate.VersionText;}
        }
    }
}
