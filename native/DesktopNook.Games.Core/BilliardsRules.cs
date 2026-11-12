namespace DesktopNook.Games.Core;

public sealed record BilliardsRule(string Id,string Name,int Count,string Description);
public readonly record struct BallStyle(int Number,string Color,bool Stripe=false);
public static class BilliardsRules
{
    public const string Eight="chinese-eight",Snooker="snooker",Nine="nine-ball",Fifteen="fifteen-ball";
    public static IReadOnlyList<BilliardsRule> All {get;}=Array.AsReadOnly(new[]
    {
        new BilliardsRule(Eight,"中八",15,"15 个目标球＋白球 · 全色 / 花色，8 号黑球居中"),
        new BilliardsRule(Snooker,"斯诺克",21,"15 红＋6 彩＋白球 · 红球三角阵，彩球各就各位"),
        new BilliardsRule(Nine,"九球追分",9,"1～9 号球＋白球 · 菱形球阵，9 号球居中"),
        new BilliardsRule(Fifteen,"十五球追分",15,"1～15 号球＋白球 · 编号三角球阵")
    });
    public static bool IsKnown(string? id)=>All.Any(r=>r.Id==id);
    public static BilliardsRule Get(string? id)=>All.FirstOrDefault(r=>r.Id==id)??All[0];
    public static bool Matches(string? id,int count)=>IsKnown(id)&&Get(id).Count==count;
    public static string Instructions(string rule,int players)
    {
        string controls="移动鼠标瞄准，台面内按住左键蓄力、松开击球。球停稳后才能再击球。空格暂停，Esc 退出；失焦或改变窗口大小自动暂停。";
        if(players==1)return Get(rule).Description+"\n\n单人清台练习：可自由击打任意目标球，将所有目标球打入球袋结束。白球入袋不计分，会安全复位。下方球盘按进球顺序收集目标球。\n\n"+controls;
        string common="本地双人轮流操作。玩家 1 先开球，合法进球可连杆；空杆或犯规换人，等球全部停稳再结算。下方按击球方显示已下球。\n\n";
        string details=rule switch
        {
            Eight=>"中八：首个合法进球分配全色 / 花色，先清完自己一组，再击打 8 号。提前进 8 或进 8 同时犯规判负（本游戏开球同样处理）。先碰错组、未碰目标球、白球入袋，或未进球且碰球后没有任何球碰库，判犯规；对方获得自由摆白球。无指定袋和开球特殊重摆。",
            Nine=>"九球追分：每杆先碰台上最小号码，可组合进其他球。合法进 9 号立即获胜。白球入袋、先碰错球、未碰目标球，或未进球且碰球后没有任何球碰库，判犯规并换人自由摆白球；犯规进 9 会复位。无推杆和连续三次犯规判负。",
            Fifteen=>"十五球追分：每杆先碰台上最小号码，合法进球按球号得分，先达 61 分获胜。白球入袋、先碰错球、未碰目标球，或未进球且碰球后没有任何球碰库，判犯规；本杆目标球复位且不计分，对方自由摆白球。清台同分为平局。",
            _=>"斯诺克：先红后彩交替，红球每颗 1 分，黄 / 绿 / 棕 / 蓝 / 粉 / 黑分别 2～7 分。交替阶段彩球进袋后复位；最后一红后再打一颗彩球，然后按黄到黑清彩。合法得分连杆，空杆换人；先碰错球、进错球、未碰球或白球入袋，给对方 4～7 分并换人，误进彩球复位。白球落袋安全复位；无自由球、重打及裁判复位，清台同分按平局处理。"
        };
        return common+details+"\n\n这是本游戏的简化本地对战规则。\n\n"+controls;
    }
    private static readonly string[] Colors=["#E6B61C","#285CC4","#D93935","#783BB0","#EB791C","#168653","#8E2738","#171D28"];
    public static BallStyle Style(string? rule,int index)
    {
        if(rule==Snooker)
        {
            if(index<15)return new(1,"#D72E35");
            return index switch{15=>new(2,"#E6BD21"),16=>new(3,"#168653"),17=>new(4,"#865234"),18=>new(5,"#285CC4"),19=>new(6,"#EC91AD"),_=>new(7,"#171D28")};
        }
        int number=index%15+1;return new(number,Colors[(number-1)%8],number>8);
    }
}
