using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopNook.Core;

namespace DesktopNook.App;
internal sealed class BilliardsSetupWindow : Window
{
    private readonly ComboBox _source=new(){Padding=new Thickness(8)};
    private readonly Slider _count=new(){Minimum=15,Maximum=45,TickFrequency=1,IsSnapToTickEnabled=true};
    private readonly CheckBox _guide=new(){Content="显示瞄准辅助线",Margin=new Thickness(0,12,0,12)};
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,Margin=new Thickness(0,12,0,8)};
    private readonly Button _start=new(){Content="开始台球",IsDefault=true,MinWidth=100};
    private readonly NookStore _store;private readonly string? _category;private readonly bool _sandbox;
    private int _revision;
    internal List<GameIcon> Selection {get;private set;}=[];
    internal GameSettings Settings {get;private set;}
    internal BilliardsSetupWindow(NookStore store,string? category,bool sandbox,GameStorage storage)
    {
        _store=store;_category=category;_sandbox=sandbox;Settings=storage.LoadSettings();Title="图标台球";Width=410;SizeToContent=SizeToContent.Height;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(22,14,22,20)};
        body.Children.Add(new TextBlock{Text="用图标打一局台球",FontSize=18,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(25,60,88)),Margin=new Thickness(0,0,0,12)});
        body.Children.Add(new TextBlock{Text="图标来源",Margin=new Thickness(0,0,0,5),Foreground=Brushes.SlateGray});
        foreach(var (label,value) in new[]{(category is null?"当前页（全部）":"当前分类","current"),("全部收纳图标","all"),("桌面图标镜像","desktop")})_source.Items.Add(new ComboBoxItem{Content=label,Tag=value});
        _source.SelectedIndex=Settings.Source=="desktop"?2:Settings.Source=="all"?1:0;body.Children.Add(_source);
        var number=new TextBlock{Margin=new Thickness(0,16,0,6)};_count.Value=Settings.Count;_count.ValueChanged+=(_,_)=>{number.Text=$"本局 {_count.Value:0} 个图标";UpdateSummary();};number.Text=$"本局 {_count.Value:0} 个图标";body.Children.Add(number);body.Children.Add(_count);
        _guide.IsChecked=Settings.Guide;body.Children.Add(_guide);body.Children.Add(_summary);
        body.Children.Add(new TextBlock{Text="只操作游戏画面，进球计分。Esc 退出，失焦暂停。",FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,14)});
        var scores=storage.Scores();if(storage.Best is { } best)body.Children.Add(new TextBlock{Text=$"个人最佳：{best.AverageSeconds:0.00} 秒 / 图标 · 最近 {scores.Count} 局",FontSize=11,Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,12)});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};actions.Children.Add(new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,8,0)});_start.Style=(Style)Application.Current.FindResource("PrimaryButton");actions.Children.Add(_start);body.Children.Add(actions);
        _start.Click+=(_,_)=>{Settings=new(){Source=(string)((ComboBoxItem)_source.SelectedItem).Tag,Count=(int)_count.Value,Guide=_guide.IsChecked==true};Selection=Selection.Take(Settings.Count).ToList();DialogResult=true;};
        _source.SelectionChanged+=async(_,_)=>await RefreshAsync();Content=body;UiDialogs.Chrome(this);Loaded+=async(_,_)=>await RefreshAsync();Closed+=(_,_)=>_revision++;
    }
    private async Task RefreshAsync()
    {
        int revision=++_revision;_start.IsEnabled=false;_summary.Text="正在读取图标…";
        try{string source=(string)((ComboBoxItem)_source.SelectedItem).Tag;var selected=source=="desktop"?await GameIconCatalog.Desktop(_store,_sandbox):GameIconCatalog.Collected(_store,source=="current"?_category:null);if(revision!=_revision)return;Selection=selected;UpdateSummary();}
        catch(Exception ex){if(revision==_revision){Selection=[];_summary.Text="图标读取未完成："+ex.Message;}}
    }
    private void UpdateSummary(){if(_source.SelectedItem is null)return;_start.IsEnabled=Selection.Count>0;int count=(int)_count.Value;_summary.Text=Selection.Count==0?"这个来源没有可用图标，请切换来源。":Selection.Count<count?$"来源 {Selection.Count} 个图标，游戏内复制补到 {count} 个。":$"来源 {Selection.Count} 个图标，按当前顺序取前 {count} 个。";}
}
