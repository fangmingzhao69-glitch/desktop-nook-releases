using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class TextDialog : Window
{
    private readonly TextBox _input;
    public string Value => _input.Text.Trim();
    public TextDialog(string title,string label,string initial)
    {
        Title=title;Width=403.2;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;Background=new SolidColorBrush(Color.FromRgb(245,248,253));FontSize=13;
        var panel=new StackPanel{Margin=new Thickness(16)};
        panel.Children.Add(new TextBlock{Text=label,Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,8)});
        _input=new TextBox{Text=initial,MaxLength=40,Padding=new Thickness(7),MinWidth=220};panel.Children.Add(_input);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,15,0,0)};
        var cancel=new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,6,0)};var ok=new Button{Content="保存",IsDefault=true,Style=(Style)Application.Current.FindResource("PrimaryButton")};
        ok.Click+=(_,_)=>{if(Value.Length>0)DialogResult=true;};buttons.Children.Add(cancel);buttons.Children.Add(ok);panel.Children.Add(buttons);Content=panel;UiDialogs.Chrome(this);
        Loaded+=(_,_)=>{_input.Focus();_input.SelectAll();};
    }
}

internal sealed record DesktopEntry(string Path,string Name,string Source,bool Recycle);
internal sealed class CollectWindow : Window
{
    private readonly List<ToggleButton> _checks=[];
    private readonly ComboBox _categories;
    private readonly string _defaultCategory;
    private readonly ScrollViewer _scroll=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private readonly Button _icons=new(){Content="▦  图标",Padding=new Thickness(12,6,12,6)},_details=new(){Content="☷  详细列表",Padding=new Thickness(12,6,12,6)},_saveDefault=new(){Content="设为默认",Padding=new Thickness(10,6,10,6)};
    private readonly TextBlock _count=new(){FontSize=11,Foreground=Brushes.SlateGray,VerticalAlignment=VerticalAlignment.Center};
    private readonly Button _accept=new(){Content="添加到轻匣",IsDefault=true,IsEnabled=false,FontSize=13};
    internal string ViewMode {get;private set;}="icons";
    public string CategoryId => (string)(_categories.SelectedValue??_defaultCategory);
    public List<DesktopEntry> Selection => _checks.Where(c=>c.IsChecked==true).Select(c=>(DesktopEntry)c.Tag).ToList();
    public CollectWindow(NookStore store,string selectedCategory)
    {
        _defaultCategory=store.DefaultCategoryId;
        Title="添加图标";Width=Math.Min(620,SystemParameters.WorkArea.Width-32);Height=Math.Min(640,SystemParameters.WorkArea.Height-32);MinWidth=440;MinHeight=400;ResizeMode=ResizeMode.CanResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;Background=new SolidColorBrush(Color.FromRgb(246,248,253));FontSize=13;
        _accept.Style=(Style)Application.Current.FindResource("PrimaryButton");
        var root=new Grid{Margin=new Thickness(18,10,18,18)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})root.RowDefinitions.Add(new(){Height=height});
        root.Children.Add(new TextBlock{Text="点击项目可多选，再次点击取消选择。",Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,12)});
        var toolbar=new DockPanel{Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(_saveDefault,Dock.Right);toolbar.Children.Add(_saveDefault);
        var views=new StackPanel{Orientation=Orientation.Horizontal};views.Children.Add(_icons);_details.Margin=new Thickness(5,0,0,0);views.Children.Add(_details);toolbar.Children.Add(views);Grid.SetRow(toolbar,1);root.Children.Add(toolbar);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_icons,"CollectView-Icons");System.Windows.Automation.AutomationProperties.SetAutomationId(_details,"CollectView-Details");System.Windows.Automation.AutomationProperties.SetAutomationId(_saveDefault,"CollectView-SaveDefault");
        _icons.Click+=(_,_)=>SetView("icons");_details.Click+=(_,_)=>SetView("details");
        _saveDefault.Click+=(_,_)=>{try{App.Options.DefaultCollectView=ViewMode;UpdateDefaultButton();}catch(Exception ex){UiDialogs.Show(this,"默认视图未能保存："+ex.Message,"添加图标");}};
        Grid.SetRow(_scroll,2);root.Children.Add(_scroll);
        var entries=new List<DesktopEntry>();var errors=new List<string>();
        foreach(var desktop in store.DesktopRoots.Where(Directory.Exists))
        {
            try
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(desktop))
                {
                    if((File.GetAttributes(path)&FileAttributes.Hidden)!=0||store.State.Items.Any(i=>string.Equals(i.OriginalPath,path,StringComparison.OrdinalIgnoreCase)))continue;
                    entries.Add(new(path,Directory.Exists(path)?Path.GetFileName(path):new[]{".lnk",".url"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase)?Path.GetFileNameWithoutExtension(path):Path.GetFileName(path),desktop,false));
                }
            }
            catch(UnauthorizedAccessException){errors.Add("部分桌面目录无读取权限");}
        }
        if(!store.State.Items.Any(i=>i.Kind=="recycle-bin")&&App.Recycle.IsVisible)entries.Add(new("","回收站","系统图标",true));
        foreach(var entry in entries.OrderBy(i=>i.Name,StringComparer.CurrentCulture))
        {
            var check=new ToggleButton{Tag=entry,Style=(Style)Application.Current.FindResource("CollectSelectionTile"),VerticalContentAlignment=VerticalAlignment.Center,ToolTip=entry.Recycle?"系统回收站，不会清空内容":entry.Path};
            System.Windows.Automation.AutomationProperties.SetName(check,entry.Name);
            check.Checked+=(_,_)=>UpdateSelection();check.Unchecked+=(_,_)=>UpdateSelection();_checks.Add(check);
        }
        var categoryPanel=new StackPanel{Margin=new Thickness(0,12,0,12)};categoryPanel.Children.Add(new TextBlock{Text="放入分类",Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,5)});
        _categories=new ComboBox{ItemsSource=store.State.Categories,DisplayMemberPath="Name",SelectedValuePath="Id",SelectedValue=selectedCategory,Padding=new Thickness(4)};categoryPanel.Children.Add(_categories);Grid.SetRow(categoryPanel,3);root.Children.Add(categoryPanel);
        var footer=new DockPanel();var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,8,0),FontSize=13});_accept.Click+=(_,_)=>{if(Selection.Count>0)DialogResult=true;};buttons.Children.Add(_accept);DockPanel.SetDock(buttons,Dock.Right);footer.Children.Add(buttons);footer.Children.Add(_count);Grid.SetRow(footer,4);root.Children.Add(footer);
        Content=root;UiDialogs.Chrome(this);SetView(App.Options.DefaultCollectView);UpdateSelection();
        SourceInitialized+=(_,_)=>FitToOwner();
        if(errors.Count>0)_count.ToolTip=string.Join("\n",errors);
    }
    private void FitToOwner()
    {
        if(Owner is not Window owner)return;
        var work=TaskbarPlacement.WorkArea(owner);
        double width=Math.Min(owner.ActualWidth*.88,work.Width-24),height=Math.Min(owner.ActualHeight*.88,work.Height-24);
        MinWidth=Math.Min(320,width);MinHeight=Math.Min(280,height);MaxWidth=width;MaxHeight=height;
        Width=Math.Min(620,width);Height=Math.Min(640,height);
        Left=owner.Left+(owner.ActualWidth-Width)/2;Top=owner.Top+(owner.ActualHeight-Height)/2;
    }
    private void UpdateSelection(){_accept.IsEnabled=Selection.Count>0;_count.Text=$"已选 {Selection.Count} / {_checks.Count} 项";}
    private void UpdateDefaultButton(){bool same=App.Options.DefaultCollectView==ViewMode;_saveDefault.Content=same?"默认视图":"设为默认";_saveDefault.IsEnabled=!same;_saveDefault.ToolTip="当前默认："+(App.Options.DefaultCollectView=="icons"?"图标":"详细列表");}
    internal void SetView(string mode)
    {
        if(mode is not ("icons" or "details"))throw new ArgumentException("无效视图");ViewMode=mode;
        _scroll.Content=null;
        Panel panel=mode=="icons"?new WrapPanel{Orientation=Orientation.Horizontal}:new StackPanel();
        foreach(var check in _checks)
        {
            var entry=(DesktopEntry)check.Tag;
            if(VisualTreeHelper.GetParent(check) is Panel parent)parent.Children.Remove(check);
            var image=new ShellIcon{IconPath=entry.Path,IsRecycleBin=entry.Recycle,Width=mode=="icons"?40:24,Height=mode=="icons"?40:24};
            if(mode=="icons")
            {
                var tile=new StackPanel{Width=112,HorizontalAlignment=HorizontalAlignment.Center};image.Margin=new Thickness(0,0,0,7);tile.Children.Add(image);tile.Children.Add(new TextBlock{Text=entry.Name,FontSize=12,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=34,LineHeight=17});check.Content=tile;check.Width=134;check.Height=104;check.Margin=new Thickness(2,3,2,3);
            }
            else
            {
                var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(36)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});image.HorizontalAlignment=HorizontalAlignment.Left;row.Children.Add(image);
                var text=new StackPanel();text.Children.Add(new TextBlock{Text=entry.Name,FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis});text.Children.Add(new TextBlock{Text=entry.Source,FontSize=10,Foreground=Brushes.SlateGray,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,3,0,0)});Grid.SetColumn(text,1);row.Children.Add(text);check.Content=row;check.Width=double.NaN;check.Height=58;check.Margin=new Thickness(0,2,0,2);
            }
            panel.Children.Add(check);
        }
        if(_checks.Count==0)panel.Children.Add(new TextBlock{Text="没有待添加的桌面项目",Margin=new Thickness(4,20,4,0),Foreground=Brushes.SlateGray});
        _scroll.Content=panel;_scroll.ScrollToTop();
        _icons.Background=mode=="icons"?new SolidColorBrush(Color.FromRgb(221,242,255)):Brushes.Transparent;_details.Background=mode=="details"?new SolidColorBrush(Color.FromRgb(221,242,255)):Brushes.Transparent;UpdateDefaultButton();
    }
}
