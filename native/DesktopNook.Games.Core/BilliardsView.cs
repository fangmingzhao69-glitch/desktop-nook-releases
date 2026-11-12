namespace DesktopNook.Games.Core;

// View-only wire values: never serialize Vec.Unit recursively or share icon paths.
public sealed record BallView(int Id,double X,double Y,double Vx,double Vy,bool Sunk,double ExitAge,
    double FromX,double FromY,double PocketX,double PocketY,double Impact,double Angle,int PottedBy);
public sealed record MatchView(int Players,int CurrentPlayer,int Score1,int Score2,int Group1,int Group2,
    int? Winner,bool Finished,string Target,string Message);
public sealed record RoundView(string Rule,int Round,int Shots,double Elapsed,RoundPhase Phase,bool Paused,
    bool Started,BallView[] Balls,int[] PocketOrder,MatchView Match);

public sealed partial class BilliardsMatch
{
    internal MatchView CaptureView()=>new(Players,CurrentPlayer,Scores[0],Scores[1],Groups[0],Groups[1],Winner,Finished,Target,LastMessage);
    internal void ApplyView(MatchView view)
    {
        CurrentPlayer=view.CurrentPlayer;Scores[0]=view.Score1;Scores[1]=view.Score2;Groups[0]=view.Group1;Groups[1]=view.Group2;
        Winner=view.Winner;Finished=view.Finished;Target=view.Target;LastMessage=view.Message;
    }
}
public sealed partial class BilliardsRound
{
    public RoundView CaptureView()=>new(RuleId,RoundNumber,Shots,Elapsed,Phase,Paused,Started,
        _balls.Select(b=>new BallView(b.Id,b.Position.X,b.Position.Y,b.Velocity.X,b.Velocity.Y,b.Sunk,Math.Min(1,b.ExitAge),
            b.ExitFrom.X,b.ExitFrom.Y,b.Pocket.X,b.Pocket.Y,b.Impact,b.Angle,b.PottedBy)).ToArray(),_pocketOrder.ToArray(),Match.CaptureView());
    public void ApplyView(RoundView view)
    {
        ValidateView(view);
        if(view.Rule!=RuleId||view.Match.Players!=Match.Players)throw new InvalidDataException("对局视图规则不一致。");
        bool trayChanged=!_pocketOrder.SequenceEqual(view.PocketOrder);_pocketOrder.Clear();_pocketOrder.AddRange(view.PocketOrder);if(trayChanged)TrayVersion++;
        for(int n=0;n<_balls.Count;n++)
        {
            var a=view.Balls[n];var b=_balls[n];b.Position=new(a.X,a.Y);b.Velocity=new(a.Vx,a.Vy);b.Sunk=a.Sunk;b.ExitAge=a.ExitAge;
            b.ExitFrom=new(a.FromX,a.FromY);b.Pocket=new(a.PocketX,a.PocketY);b.Impact=a.Impact;b.Angle=a.Angle;b.PottedBy=a.PottedBy;
        }
        RoundNumber=view.Round;Shots=view.Shots;Elapsed=view.Elapsed;Phase=view.Phase;Paused=view.Paused;Started=view.Started;Match.ApplyView(view.Match);
    }
    public static void ValidateView(RoundView view)
    {
        if(!BilliardsRules.IsKnown(view.Rule)||view.Balls.Length!=BilliardsRules.Get(view.Rule).Count+1||view.Round<1||view.Shots is <0 or >100000||!double.IsFinite(view.Elapsed)||view.Elapsed is <0 or >604800||!Enum.IsDefined(view.Phase))throw new InvalidDataException("对局视图无效。");
        var m=view.Match;if(m.Players!=2||m.CurrentPlayer is <0 or >1||m.Score1 is <0 or >1000000||m.Score2 is <0 or >1000000||m.Group1 is <0 or >2||m.Group2 is <0 or >2||m.Winner is not (null or 0 or 1)||m.Target.Length>100||m.Message.Length>180)throw new InvalidDataException("计分视图无效。");
        for(int n=0;n<view.Balls.Length;n++)
        {
            var b=view.Balls[n];double[] numbers=[b.X,b.Y,b.Vx,b.Vy,b.ExitAge,b.FromX,b.FromY,b.PocketX,b.PocketY,b.Impact,b.Angle];
            if(b.Id!=n||b.PottedBy is <0 or >1||numbers.Any(v=>!double.IsFinite(v)||Math.Abs(v)>10000)||b.ExitAge<0||b.Impact is <0 or >1)throw new InvalidDataException("球位视图无效。");
        }
        if(view.PocketOrder.Length>=view.Balls.Length||view.PocketOrder.Distinct().Count()!=view.PocketOrder.Length||view.PocketOrder.Any(id=>id<1||id>=view.Balls.Length||!view.Balls[id].Sunk))throw new InvalidDataException("球盘视图无效。");
    }
}
