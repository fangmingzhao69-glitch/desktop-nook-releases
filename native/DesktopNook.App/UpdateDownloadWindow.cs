using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopNook.App;

internal sealed class UpdateDownloadWindow : Window
{
    internal string? DownloadedFile {get;private set;}
    private readonly CancellationTokenSource _cancel=new();
    internal UpdateDownloadWindow(Window owner,OnlineRelease release,string dataRoot,UpdateSource channel=UpdateSource.Auto)
    {
        Owner=owner;Title="更新折页";Width=Math.Clamp(owner.ActualWidth-40,280,440);SizeToContent=SizeToContent.Height;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(16,12,16,16)};
        var message=new TextBlock{Text=$"当前版本 {MainWindow.CurrentVersionLabel} → v{release.Version.ToString(3)}\n校验通过后自动重启。",FontSize=13,LineHeight=20,TextWrapping=TextWrapping.Wrap,Foreground=ThemePalette.Brush("TextPrimaryBrush"),Margin=new Thickness(0,0,0,16)};
        var bar=new ProgressBar{Minimum=0,Maximum=100,Height=8};
        var status=new TextBlock{Text="准备下载…",Margin=new Thickness(0,10,0,18),Foreground=ThemePalette.Brush("TextSecondaryBrush"),TextWrapping=TextWrapping.Wrap};
        var cancel=new Button{Content="取消",HorizontalAlignment=HorizontalAlignment.Right,MinWidth=78};cancel.Click+=(_,_)=>{_cancel.Cancel();DialogResult=false;};
        body.Children.Add(message);body.Children.Add(bar);body.Children.Add(status);body.Children.Add(cancel);Content=body;UiDialogs.Chrome(this,true);
        Closed+=(_,_)=>_cancel.Cancel();
        Loaded+=async (_,_)=>
        {
            try
            {
                using var client=OnlineUpdater.CreateClient();
                DownloadedFile=await OnlineUpdater.DownloadAsync(client,release,dataRoot,new Progress<double>(value=>{bar.Value=value;status.Text=value>=100?"正在校验更新文件…":$"已下载 {value:F0}%";}),_cancel.Token,channel);
                if(!_cancel.IsCancellationRequested)DialogResult=true;
            }
            catch(OperationCanceledException){ }
            catch(Exception ex){if(!_cancel.IsCancellationRequested){status.Foreground=ThemePalette.Brush("DangerBrush");status.Text="下载或校验失败。"+OnlineUpdater.FailureMessage(ex);cancel.Content="关闭";}}
        };
    }
}
