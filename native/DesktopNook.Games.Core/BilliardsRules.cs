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
