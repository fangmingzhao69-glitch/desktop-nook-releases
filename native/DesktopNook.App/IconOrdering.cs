using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow
{
    private const string OrderFormat="DesktopNook.IconOrder.v1";
    private bool _ordering;
    private string? _orderCategory;
    private List<string> _orderDraft=[];
    internal bool IsOrdering=>_ordering;
    private IEnumerable<NookItem> VisibleOrderedItems()
    {
        if(!_ordering)return Store.OrderedItems(_activeCategory);
        var items=Store.State.Items.ToDictionary(i=>i.Id);
        return _orderDraft.Where(items.ContainsKey).Select(id=>items[id]);
    }
    internal void BeginIconOrdering()
    {
        if(_ordering)return;
        if(_activeCategory is null){_interactions++;try{UiDialogs.Show(this,"“全部”按顶部分类顺序汇总。请先选择具体分类，再调整该分类内的图标顺序。","调整图标顺序");}finally{_interactions--;}return;}
        _orderCategory=_activeCategory;_orderDraft=Store.OrderedItems(_orderCategory).Select(i=>i.Id).ToList();
        if(_orderDraft.Count<2){SetStatus("当前分类至少需要两个图标才能调整顺序");return;}
        _ordering=true;_interactions++;Tabs.IsEnabled=false;CollectButton.IsEnabled=false;OrderBar.Visibility=Visibility.Visible;
        Render();SetStatus("拖动图标调整顺序；完成后保存，Esc 取消");
    }
    internal void EndIconOrdering(bool save)
    {
        if(!_ordering||_dragging)return;
        if(save){try{Store.ReorderItems(_orderCategory!,_orderDraft);}catch(Exception ex){ShowFailure(ex);return;}}
        _ordering=false;_orderCategory=null;_orderDraft.Clear();_interactions--;Tabs.IsEnabled=true;CollectButton.IsEnabled=true;
        OrderBar.Visibility=Visibility.Collapsed;OrderMarker.Visibility=Visibility.Collapsed;Render();SetStatus(save?"图标顺序已保存":"已取消排序，原顺序保留");
    }
    private void OnOrderDone(object sender,RoutedEventArgs e)=>EndIconOrdering(true);
    private void OnOrderCancel(object sender,RoutedEventArgs e)=>EndIconOrdering(false);
    internal void MoveOrderDraft(string id,string targetId,bool after)
    {
        if(!_ordering||id==targetId||!_orderDraft.Contains(id)||!_orderDraft.Contains(targetId))return;
        _orderDraft.Remove(id);int index=_orderDraft.IndexOf(targetId)+(after?1:0);_orderDraft.Insert(index,id);Render();
    }
    private void StartOrderDrag(Button button,NookItem item)
    {
        _dragging=true;var data=new DataObject();data.SetData(OrderFormat,item.Id);
        QueryContinueDragEventHandler query=(_,e)=>{if(e.EscapePressed){e.Action=DragAction.Cancel;e.Handled=true;}};
        DragDrop.AddQueryContinueDragHandler(button,query);
        try{DragDrop.DoDragDrop(button,data,DragDropEffects.Move);}
        finally{DragDrop.RemoveQueryContinueDragHandler(button,query);_dragging=false;_suppressUntil=DateTime.UtcNow.AddMilliseconds(250);OrderMarker.Visibility=Visibility.Collapsed;}
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
        e.Handled=true;e.Effects=DragDropEffects.None;OrderMarker.Visibility=Visibility.Collapsed;
        if(!e.Data.GetDataPresent(OrderFormat)||e.Data.GetData(OrderFormat) is not string id||!_orderDraft.Contains(id))return;
        if(OrderTarget(e.GetPosition(DropSurface)) is not {} target)return;
        e.Effects=DragDropEffects.Move;OrderMarker.Height=target.Bounds.Height;OrderMarker.Margin=new Thickness(target.After?target.Bounds.Right:target.Bounds.Left,target.Bounds.Top,0,0);OrderMarker.Visibility=Visibility.Visible;
        var pointer=e.GetPosition(IconsScroll);if(pointer.Y<24)IconsScroll.LineUp();else if(pointer.Y>IconsScroll.ActualHeight-24)IconsScroll.LineDown();
    }
    private void OrderDrop(DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;OrderMarker.Visibility=Visibility.Collapsed;
        if(e.Data.GetDataPresent(OrderFormat)&&e.Data.GetData(OrderFormat) is string id&&OrderTarget(e.GetPosition(DropSurface)) is {} target)
        {MoveOrderDraft(id,target.Item.Id,target.After);e.Effects=DragDropEffects.Move;}
    }
}
