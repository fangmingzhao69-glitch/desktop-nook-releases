namespace DesktopNook.Games.Core;

public sealed record ShotResult(IReadOnlyList<int> Respot,bool FreeBall,bool Finished,string Message);
public sealed partial class BilliardsMatch(string rule,int players)
{
    public int Players {get;}=players is 1 or 2?players:throw new ArgumentOutOfRangeException(nameof(players));
    public int CurrentPlayer {get;private set;}
    public int[] Scores {get;}=[0,0];
    public int[] Groups {get;}=[0,0]; // 1 = solids; 2 = stripes.
    public int? Winner {get;private set;}
    public bool Finished {get;private set;}
    public string LastMessage {get;private set;}="玩家 1 开球";
    private readonly HashSet<int> _allowed=[];
    private bool _canBlack,_colorAfterRed;
    private int _nextColor=2;
    public string Target {get;private set;}="";
    public void Begin(IReadOnlyList<Ball> balls)
    {
        _allowed.Clear();var live=balls.Where(b=>b.Id>0&&!b.Sunk).ToList();_canBlack=false;
        if(rule==BilliardsRules.Eight)
        {
            int group=Groups[CurrentPlayer];var own=live.Where(b=>b.Id!=8&&(group==0||Group(b.Id)==group)).ToList();
            _canBlack=group!=0&&own.Count==0;foreach(var b in live)if(_canBlack?b.Id==8:b.Id!=8&&(group==0||Group(b.Id)==group))_allowed.Add(b.Id);
            Target=_canBlack?"8 号黑球":group==0?"全色 / 花色待分组":group==1?"全色球":"花色球";
        }
        else if(rule==BilliardsRules.Snooker)
        {
            bool red=live.Any(b=>b.Id<=15);foreach(var b in live)if(_colorAfterRed?b.Id>15:red?b.Id<=15:b.Style.Number==_nextColor)_allowed.Add(b.Id);
            Target=_colorAfterRed?"任意一颗彩球":red?"红球":$"{ColorName(_nextColor)}球（{_nextColor} 分）";
        }
        else
        {
            int lowest=live.Select(b=>b.Id).DefaultIfEmpty(0).Min();if(lowest>0)_allowed.Add(lowest);Target=$"先碰 {lowest} 号球";
        }
    }
    private static int Group(int id)=>id<8?1:2;
    private static string ColorName(int value)=>value switch{2=>"黄",3=>"绿",4=>"棕",5=>"蓝",6=>"粉",_=>"黑"};
    public ShotResult Resolve(IReadOnlyList<Ball> balls,int? first,IReadOnlyList<int> pockets,bool railAfterContact)
    {
        if(Players!=2||Finished)return new([],false,Finished,LastMessage);
        var sunk=pockets.Where(id=>id>0).Distinct().ToList();bool scratch=pockets.Contains(0),foul=scratch||first is null||!_allowed.Contains(first.Value);
        if(rule!=BilliardsRules.Snooker&&sunk.Count==0&&!railAfterContact)foul=true;
        var respot=new List<int>();bool keep=false,free=false;int shooter=CurrentPlayer,other=1-shooter;
        if(rule==BilliardsRules.Eight)
        {
            if(sunk.Contains(8)){Winner=!foul&&_canBlack?shooter:other;Finished=true;}
            else
            {
                if(!foul&&Groups[shooter]==0&&sunk.Count>0){Groups[shooter]=Group(sunk[0]);Groups[other]=3-Groups[shooter];}
                if(Groups[shooter]>0){for(int p=0;p<2;p++)Scores[p]=balls.Count(b=>b.Id>0&&b.Id!=8&&b.Sunk&&Group(b.Id)==Groups[p]);}
                keep=!foul&&sunk.Any(id=>Groups[shooter]==Group(id));free=foul;
            }
        }
        else if(rule==BilliardsRules.Nine)
        {
            if(sunk.Contains(9)){if(foul)respot.Add(9);else{Winner=shooter;Finished=true;}}
            if(!foul)Scores[shooter]+=sunk.Count;keep=!foul&&sunk.Count>0;free=foul;
        }
        else if(rule==BilliardsRules.Fifteen)
        {
            if(foul)respot.AddRange(sunk);else Scores[shooter]+=sunk.Sum();
            keep=!foul&&sunk.Count>0;free=foul;
            if(Scores[shooter]>=61){Winner=shooter;Finished=true;}
            else if(balls.Where(b=>b.Id>0).All(b=>b.Sunk)&&!foul){Finished=true;Winner=Scores[0]==Scores[1]?null:Scores[0]>Scores[1]?0:1;}
        }
        else
        {
            bool redAtStart=!_colorAfterRed&&_allowed.Any(id=>id<=15);
            if(sunk.Any(id=>!_allowed.Contains(id))||(!redAtStart&&sunk.Count>1))foul=true;
            if(foul)
            {
                int penalty=Math.Max(4,sunk.Append(first??0).Append(_allowed.Count==1?_allowed.First():0).Where(id=>id>0).Select(id=>balls[id].Style.Number).DefaultIfEmpty(4).Max());Scores[other]+=penalty;
                respot.AddRange(sunk.Where(id=>id>15));_colorAfterRed=false;
            }
            else if(sunk.Count>0)
            {
                Scores[shooter]+=sunk.Sum(id=>balls[id].Style.Number);keep=true;
                if(redAtStart)_colorAfterRed=true;
                else if(_colorAfterRed){respot.AddRange(sunk);_colorAfterRed=false;}
                else{_nextColor++;if(_nextColor>7){Finished=true;Winner=Scores[0]==Scores[1]?null:Scores[0]>Scores[1]?0:1;}}
            }
            else _colorAfterRed=false;
        }
        if(!Finished&&!keep)CurrentPlayer=other;
        LastMessage=Finished?(Winner is int winner?$"玩家 {winner+1} 获胜":"双方平局"):foul?$"玩家 {shooter+1} 犯规，玩家 {CurrentPlayer+1} 击球":keep?$"玩家 {shooter+1} 进球，继续击球":$"换玩家 {CurrentPlayer+1} 击球";
        return new(respot,free,Finished,LastMessage);
    }
}
