using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly List<CheckBox> _checks=[];private readonly ComboBox _categories;private readonly string _defaultCategory;
    public string CategoryId => (string)(_categories.SelectedValue??_defaultCategory);
    public List<DesktopEntry> Selection => _checks.Where(c=>c.IsChecked==true).Select(c=>(DesktopEntry)c.Tag).ToList();
    public CollectWindow(NookStore store,string selectedCategory)
    {
        _defaultCategory=store.DefaultCategoryId;
        Title="添加图标";Width=470.4;Height=515.2;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;Background=new SolidColorBrush(Color.FromRgb(246,248,253));FontSize=13;
        var root=new Grid{Margin=new Thickness(15)};root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.Children.Add(new TextBlock{Text="选择桌面图标，也可以直接拖入轻匣。",Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,10)});
        var list=new StackPanel();var scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var entries=new List<DesktopEntry>();
        foreach(var desktop in store.DesktopRoots.Where(Directory.Exists))
        {
            try
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(desktop))
                {
                    if((File.GetAttributes(path)&FileAttributes.Hidden)!=0)continue;
                    if(store.State.Items.Any(i=>string.Equals(i.OriginalPath,path,StringComparison.OrdinalIgnoreCase)))continue;
                    entries.Add(new(path,Directory.Exists(path)?System.IO.Path.GetFileName(path):new[]{".lnk",".url"}.Contains(System.IO.Path.GetExtension(path),StringComparer.OrdinalIgnoreCase)?System.IO.Path.GetFileNameWithoutExtension(path):System.IO.Path.GetFileName(path),desktop,false));
                }
            }
            catch(UnauthorizedAccessException) {list.Children.Add(new TextBlock{Text="部分桌面目录无读取权限",Foreground=Brushes.SlateGray});}
        }
        if(!store.State.Items.Any(i=>i.Kind=="recycle-bin")&&App.Recycle.IsVisible)entries.Add(new("","回收站","系统图标",true));
        var accept=new Button{Content="收纳到轻匣",IsDefault=true,IsEnabled=false,FontSize=13,Style=(Style)Application.Current.FindResource("PrimaryButton")};
        foreach(var entry in entries.OrderBy(i=>i.Name,StringComparer.CurrentCulture))
        {
            var content=new StackPanel{Orientation=Orientation.Horizontal};content.Children.Add(new ShellIcon{IconPath=entry.Path,IsRecycleBin=entry.Recycle,Width=20,Height=20,Margin=new Thickness(4,0,9,0)});
            var text=new StackPanel();text.Children.Add(new TextBlock{Text=entry.Name,FontSize=13});text.Children.Add(new TextBlock{Text=entry.Source,FontSize=10,Foreground=Brushes.SlateGray,MaxWidth=225,TextTrimming=TextTrimming.CharacterEllipsis});content.Children.Add(text);
            var check=new CheckBox{Content=content,Tag=entry,Margin=new Thickness(0,4,0,4),VerticalContentAlignment=VerticalAlignment.Center,ToolTip=entry.Recycle?"系统回收站，不会清空内容":entry.Path};
            check.Checked+=(_,_)=>accept.IsEnabled=Selection.Count>0;check.Unchecked+=(_,_)=>accept.IsEnabled=Selection.Count>0;
            _checks.Add(check);list.Children.Add(check);
        }
        if(entries.Count==0)list.Children.Add(new TextBlock{Text="没有待收纳的桌面图标",Margin=new Thickness(0,15,0,0),Foreground=Brushes.SlateGray});
        var categoryPanel=new StackPanel{Margin=new Thickness(0,10,0,10)};categoryPanel.Children.Add(new TextBlock{Text="放入分类",Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,5)});
        _categories=new ComboBox{ItemsSource=store.State.Categories,DisplayMemberPath="Name",SelectedValuePath="Id",SelectedValue=selectedCategory,Padding=new Thickness(4)};categoryPanel.Children.Add(_categories);Grid.SetRow(categoryPanel,2);root.Children.Add(categoryPanel);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};buttons.Children.Add(new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,6,0),FontSize=13});accept.Click+=(_,_)=>{if(Selection.Count>0)DialogResult=true;};buttons.Children.Add(accept);Grid.SetRow(buttons,3);root.Children.Add(buttons);Content=root;Height+=48;UiDialogs.Chrome(this);
    }
}
