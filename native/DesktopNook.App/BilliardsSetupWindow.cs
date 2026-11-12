using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopNook.Core;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
internal sealed class BilliardsSetupWindow : Window
{
    private readonly ComboBox _source=new(){Padding=new Thickness(8)};
    private readonly ComboBox _rule=new(){Padding=new Thickness(8)};
    private readonly TextBlock _record=new(){FontSize=11,Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,12)};
    private readonly GameStorage _storage;
    private readonly CheckBox _guide=new(){Content="显示瞄准辅助线",Margin=new Thickness(0,12,0,12)};
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,Margin=new Thickness(0,12,0,8)};
    private readonly Button _start=new(){Content="开始台球",IsDefault=true,MinWidth=100};
    private readonly NookStore _store;private readonly string? _category;private readonly bool _sandbox;
    private int _revision;
    internal List<GameIcon> Selection {get;private set;}=[];
    internal GameSettings Settings {get;private set;}
    internal BilliardsSetupWindow(NookStore store,string? category,bool sandbox,GameStorage storage)
    {
        _store=store;_category=category;_sandbox=sandbox;_storage=storage;Settings=storage.LoadSettings();Title="图标台球";Width=410;SizeToContent=SizeToContent.Height;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(22,14,22,20)};
        body.Children.Add(new TextBlock{Text="用图标打一局台球",FontSize=18,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(25,60,88)),Margin=new Thickness(0,0,0,12)});
        body.Children.Add(new TextBlock{Text="图标来源",Margin=new Thickness(0,0,0,5),Foreground=Brushes.SlateGray});
        foreach(var (label,value) in new[]{(category is null?"当前页（全部）":"当前分类","current"),("全部收纳图标","all"),("桌面图标镜像","desktop")})_source.Items.Add(new ComboBoxItem{Content=label,Tag=value});
        _source.SelectedIndex=Settings.Source=="desktop"?2:Settings.Source=="all"?1:0;body.Children.Add(_source);
        body.Children.Add(new TextBlock{Text="台球规则",Margin=new Thickness(0,16,0,6),Foreground=Brushes.SlateGray});
        foreach(var rule in BilliardsRules.All)_rule.Items.Add(new ComboBoxItem{Content=$"{rule.Name}（{rule.Count} 球＋白球）",Tag=rule.Id});
        _rule.SelectedIndex=BilliardsRules.All.ToList().FindIndex(r=>r.Id==Settings.Rule);body.Children.Add(_rule);
        _rule.SelectionChanged+=(_,_)=>UpdateSummary();
        _guide.IsChecked=Settings.Guide;body.Children.Add(_guide);body.Children.Add(_summary);
        body.Children.Add(new TextBlock{Text="按所选规则配置球数、配色和摆球，单人清台练习。Esc 退出，失焦暂停。",FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,14)});
        body.Children.Add(_record);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};actions.Children.Add(new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,8,0)});_start.Style=(Style)Application.Current.FindResource("PrimaryButton");actions.Children.Add(_start);body.Children.Add(actions);
        _start.Click+=(_,_)=>{Settings=new(){Source=(string)((ComboBoxItem)_source.SelectedItem).Tag,Rule=(string)((ComboBoxItem)_rule.SelectedItem).Tag,Guide=_guide.IsChecked==true};Selection=Selection.Take(Settings.Count).ToList();DialogResult=true;};
        _source.SelectionChanged+=async(_,_)=>await RefreshAsync();Content=body;UiDialogs.Chrome(this);Loaded+=async(_,_)=>await RefreshAsync();Closed+=(_,_)=>_revision++;
    }
    private async Task RefreshAsync()
    {
        int revision=++_revision;_start.IsEnabled=false;_summary.Text="正在读取图标…";
        try{string source=(string)((ComboBoxItem)_source.SelectedItem).Tag;var selected=source=="desktop"?await GameIconCatalog.Desktop(_store,_sandbox):GameIconCatalog.Collected(_store,source=="current"?_category:null);if(revision!=_revision)return;Selection=selected;UpdateSummary();}
        catch(Exception ex){if(revision==_revision){Selection=[];_summary.Text="图标读取未完成："+ex.Message;}}
    }
    private void UpdateSummary()
    {
        if(_source.SelectedItem is null||_rule.SelectedItem is not ComboBoxItem selected)return;var rule=BilliardsRules.Get((string)selected.Tag);int count=rule.Count;_start.IsEnabled=Selection.Count>0;
        _summary.Text=rule.Description+"\n"+(Selection.Count==0?"这个来源没有可用图标，请切换来源。":Selection.Count<count?$"来源 {Selection.Count} 个图标，游戏内复制补到 {count} 个。":$"来源 {Selection.Count} 个图标，按当前顺序取前 {count} 个。");
        _record.Text=_storage.BestForRule(rule.Id) is { } best?$"{rule.Name}最佳：{best.AverageSeconds:0.00} 秒 / 球":"";_record.Visibility=_record.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
    }
}
