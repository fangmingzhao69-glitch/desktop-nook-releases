using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopNook.App;
internal static class UiDialogs
{
    public static void Chrome(Window window,bool compact=false)
    {
        window.UseLayoutRounding=true;window.SnapsToDevicePixels=true;TextOptions.SetTextFormattingMode(window,TextFormattingMode.Display);window.FontFamily=new FontFamily("Microsoft YaHei UI, Segoe UI");var body=(UIElement)window.Content;window.Content=null;window.WindowStyle=WindowStyle.None;window.AllowsTransparency=true;window.Background=Brushes.Transparent;
        var dock=new DockPanel();var heading=new Grid{Margin=compact?new Thickness(16,12,12,0):new Thickness(20,16,14,0)};heading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        heading.Children.Add(new TextBlock{Text=window.Title,FontSize=compact?14:15,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(25,60,88)),VerticalAlignment=VerticalAlignment.Center});
        var close=new Button{Content="×",FontSize=19,Padding=new Thickness(8,1,8,3),Foreground=Brushes.SlateGray};close.Click+=(_,_)=>window.DialogResult=false;Grid.SetColumn(close,1);heading.Children.Add(close);
        heading.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is TextBlock || e.OriginalSource==heading)window.DragMove();};
        DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);dock.Children.Add(body);
        window.Content=new Border{CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),BorderBrush=(Brush)Application.Current.FindResource("LineBrush"),Background=(Brush)Application.Current.FindResource("PanelBrush"),Child=dock};
    }
    public static MessageBoxResult ShowUpdate(Window owner,string text,string title,MessageBoxButton buttons=MessageBoxButton.OK)=>Show(owner,text,title,buttons,compact:true);
    public static MessageBoxResult Show(Window owner,string text,string title,MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None,bool compact=false)
    {
        var window=new Window{Title=title,Owner=owner,Width=compact?Math.Clamp(owner.ActualWidth-40,280,390):470,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,ResizeMode=ResizeMode.NoResize};
        var body=new StackPanel{Margin=compact?new Thickness(16,12,16,16):new Thickness(22,16,22,20)};body.Children.Add(new ScrollViewer{MaxHeight=compact?210:320,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=compact?12:13,LineHeight=compact?20:23,Foreground=new SolidColorBrush(Color.FromRgb(100,116,142))}});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,compact?14:22,0,0)};
        if(buttons!=MessageBoxButton.OK){var cancel=new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,8,0),MinWidth=72};cancel.Click+=(_,_)=>window.DialogResult=false;actions.Children.Add(cancel);}
        var confirm=new Button{Content="确定",IsDefault=true,MinWidth=76,Style=(Style)Application.Current.FindResource("PrimaryButton")};confirm.Click+=(_,_)=>window.DialogResult=true;actions.Children.Add(confirm);body.Children.Add(actions);window.Content=body;Chrome(window,compact);
        return window.ShowDialog()==true?MessageBoxResult.OK:MessageBoxResult.Cancel;
    }
}
