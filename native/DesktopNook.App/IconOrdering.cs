using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow
{
    private IEnumerable<NookItem> VisibleOrderedItems()=>Store.OrderedItems(_activeCategory);
    internal bool MoveIconRelative(string id,string targetId,bool after)
    {
        var item=Store.State.Items.SingleOrDefault(i=>i.Id==id);var target=Store.State.Items.SingleOrDefault(i=>i.Id==targetId);
        if(item is null||target is null||item.CategoryId!=target.CategoryId)return false;
        if(id==targetId)return true;
        var ids=Store.OrderedItems(item.CategoryId).Select(i=>i.Id).ToList();ids.Remove(id);ids.Insert(ids.IndexOf(targetId)+(after?1:0),id);
        return Run(()=>Store.ReorderItems(item.CategoryId,ids),"图标顺序已保存");
    }
    private (NookItem Item,bool After,Rect Bounds)? OrderTarget(Point point)
    {
        if(!new Rect(0,0,DropSurface.ActualWidth,DropSurface.ActualHeight).Contains(point))return null;
        var entries=FindVisualChildren<Button>(Icons).Where(b=>b.Tag is NookItem)
            .Select(b=>(Item:(NookItem)b.Tag,Bounds:new Rect(b.TranslatePoint(new Point(),DropSurface),new Size(b.ActualWidth,b.ActualHeight)))).ToList();
        if(entries.Count==0)return null;
        var nearest=point.Y>entries.Max(e=>e.Bounds.Bottom)?entries[^1]:point.Y<entries.Min(e=>e.Bounds.Top)?entries[0]:entries.MinBy(e=>Math.Pow(point.X-(e.Bounds.Left+e.Bounds.Width/2),2)+Math.Pow(point.Y-(e.Bounds.Top+e.Bounds.Height/2),2));
        bool after=point.Y>entries.Max(e=>e.Bounds.Bottom)||(point.Y>=entries.Min(e=>e.Bounds.Top)&&point.X>=nearest.Bounds.Left+nearest.Bounds.Width/2);
        return(nearest.Item,after,nearest.Bounds);
    }
    private void OrderDragOver(DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;OrderMarker.Visibility=Visibility.Collapsed;DropOutline.Visibility=Visibility.Collapsed;
        if(!e.Data.GetDataPresent(ItemFormat)||e.Data.GetData(ItemFormat) is not string id)return;
        var source=Store.State.Items.SingleOrDefault(i=>i.Id==id);
        if(source is null||OrderTarget(e.GetPosition(DropSurface)) is not {} target||source.CategoryId!=target.Item.CategoryId)return;
        e.Effects=DragDropEffects.Move;OrderMarker.Height=target.Bounds.Height;OrderMarker.Margin=new Thickness(target.After?target.Bounds.Right:target.Bounds.Left,target.Bounds.Top,0,0);OrderMarker.Visibility=Visibility.Visible;
        var pointer=e.GetPosition(IconsScroll);if(pointer.Y<24)IconsScroll.LineUp();else if(pointer.Y>IconsScroll.ActualHeight-24)IconsScroll.LineDown();
    }
    private void OrderDrop(DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;OrderMarker.Visibility=Visibility.Collapsed;
        if(e.Data.GetDataPresent(ItemFormat)&&e.Data.GetData(ItemFormat) is string id&&OrderTarget(e.GetPosition(DropSurface)) is {} target)
        {
            if(MoveIconRelative(id,target.Item.Id,target.After))e.Effects=DragDropEffects.Move;
            else SetStatus("仅可调整同一分类内顺序；移动分类请拖到上方分类标签");
        }
    }
}
