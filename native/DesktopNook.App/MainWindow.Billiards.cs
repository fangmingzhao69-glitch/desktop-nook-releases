using System.Windows;
using System.Windows.Controls;

namespace DesktopNook.App;
public partial class MainWindow
{
    private BilliardsController? _games;
    private MenuItem GamesMenu()
    {
        var menu=new MenuItem{Header="桌面玩法"};
        menu.Items.Add(Menu(_games?.Running==true?"继续图标台球":"图标台球…",()=>_=StartBilliardsAsync()));
        if(_games?.Running==true)menu.Items.Add(Menu("退出台球",()=>_games.Stop()));
        menu.Items.Add(new Separator());menu.Items.Add(Menu("开源许可…",()=>
        {
            _interactions++;try{using var stream=typeof(MainWindow).Assembly.GetManifestResourceStream("DesktopNook.BilliardsLicense");using var reader=new System.IO.StreamReader(stream!);UiDialogs.Show(this,"Desktop Icon Billiards · LXG\n\n"+reader.ReadToEnd(),"开源许可");}finally{_interactions--;}
        }));
        return menu;
    }
    internal async Task StartBilliardsAsync()
    {
        if(_storageMoving)return;
        if(_games?.Running==true){_games.Activate();BeginHidePanel();return;}
        var storage=new GameStorage(Store.DataDirectory);BilliardsSetupWindow setup;
        FinishPanelMotion();_interactions++;
        try{setup=new(Store,ActiveCategory,App.IsSandbox,storage){Owner=this};if(setup.ShowDialog()!=true)return;}
        finally{_interactions--;}
        try
        {
            SetStatus("正在准备图标台球…");_games??=new(Store.DataDirectory);
            await _games.Launch(setup.Selection,setup.Settings,TaskbarPlacement.ScreenArea(this));
            SetStatus("图标台球已打开");BeginHidePanel();
        }
        catch(Exception ex){if(!_allowExit){ShowPanel();SetStatus("台球未启动："+ex.Message);}}
    }
    private bool StopBilliards()
    {
        try{_games?.Dispose();_games=null;return true;}
        catch(Exception ex){SetStatus("请先结束当前台球对局："+ex.Message);return false;}
    }
    private string GameUpdateNotice=>_games?.Running==true?"\n更新将结束当前台球对局。":"";
}
