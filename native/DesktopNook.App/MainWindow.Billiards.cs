using System.Windows;

namespace DesktopNook.App;
public partial class MainWindow
{
    private BilliardsController? _games;
    internal bool BilliardsRunning => _games?.Running == true;

    private void ShowBilliardsLicense()
    {
        _interactions++;
        try
        {
            var assembly = typeof(MainWindow).Assembly;
            using var license = new System.IO.StreamReader(assembly.GetManifestResourceStream("DesktopNook.BilliardsLicense")!);
            using var audioLicense = new System.IO.StreamReader(assembly.GetManifestResourceStream("DesktopNook.BilliardsAudioLicense")!);
            UiDialogs.Show(this, "Desktop Icon Billiards · LXG\n\n" + license.ReadToEnd() + "\n\n" + audioLicense.ReadToEnd(), "图标台球许可");
        }
        finally { _interactions--; }
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
            await _games.Launch(setup.Selection,setup.Settings,TaskbarPlacement.ScreenArea(this),online:setup.Online);
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
