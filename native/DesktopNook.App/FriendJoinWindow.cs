using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopNook.Games.Network;

namespace DesktopNook.App;
internal sealed class FriendJoinWindow : Window
{
    private readonly TextBox _invite=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=90,MaxLength=8192,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly ListBox _rooms=new(){Height=145,Margin=new Thickness(0,8,0,12)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,FontSize=12,Margin=new Thickness(0,8,0,12)};
    private readonly CancellationTokenSource _cancel=new();
    internal string Invitation {get;private set;}="";
    internal FriendJoinWindow(bool scanOnLoad=true)
    {
        Title="加入好友房间";Width=400;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(20,14,20,20)};body.Children.Add(new TextBlock{Text="粘贴朋友发来的邀请信息",Margin=new Thickness(0,0,0,8),Foreground=Brushes.SlateGray});body.Children.Add(_invite);
        var tools=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0)};var paste=new Button{Content="粘贴邀请",Margin=new Thickness(0,0,8,0)};paste.Click+=(_,_)=>{try{_invite.Text=Clipboard.GetText().Trim();}catch{_status.Text="剪贴板暂时不可用，请手动粘贴。";}};tools.Children.Add(paste);
        var scan=new Button{Content="搜索局域网"};async Task Scan(){scan.IsEnabled=false;_status.Text="正在寻找同网房间…";try{var found=await LanDiscovery.Find(token:_cancel.Token);_rooms.ItemsSource=found;_status.Text=found.Count==0?"未发现房间，可直接粘贴邀请；防火墙或隔离网络可能阻止广播。":"选择房间后加入，需要房主确认。";}catch(OperationCanceledException){}catch(Exception ex){_status.Text="搜索未完成："+ex.Message;}finally{scan.IsEnabled=true;}}scan.Click+=async(_,_)=>await Scan();tools.Children.Add(scan);body.Children.Add(tools);body.Children.Add(_rooms);body.Children.Add(_status);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};actions.Children.Add(new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,8,0)});var join=new Button{Content="加入房间",IsDefault=true,Style=(Style)Application.Current.FindResource("PrimaryButton")};join.Click+=(_,_)=>
        {
            try{var invite=_rooms.SelectedItem is DiscoveredRoom room&&string.IsNullOrWhiteSpace(_invite.Text)?room.Invitation():GameInvite.Parse(_invite.Text.Trim());invite.Validate();Invitation=invite.Encode();DialogResult=true;}catch(Exception ex){_status.Text=ex.Message;}
        };actions.Children.Add(join);body.Children.Add(actions);Content=body;UiDialogs.Chrome(this);Loaded+=async(_,_)=>{if(Owner is not null)Width=Math.Max(310,Math.Min(400,Owner.ActualWidth*.95));if(scanOnLoad)await Scan();};Closed+=(_,_)=>_cancel.Cancel();
    }
}
