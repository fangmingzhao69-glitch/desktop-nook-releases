// Inspired by desktop-icon-billiards (LXG), MIT; see ThirdPartyNotices.
namespace DesktopNook.Games.Core;

public readonly record struct Vec(double X,double Y)
{
    public double Length=>Math.Sqrt(X*X+Y*Y);
    public Vec Unit=>Length>1e-9?this/Length:new(1,0);
    public static Vec operator +(Vec a,Vec b)=>new(a.X+b.X,a.Y+b.Y);
    public static Vec operator -(Vec a,Vec b)=>new(a.X-b.X,a.Y-b.Y);
    public static Vec operator *(Vec a,double s)=>new(a.X*s,a.Y*s);
    public static Vec operator /(Vec a,double s)=>new(a.X/s,a.Y/s);
    public static double Dot(Vec a,Vec b)=>a.X*b.X+a.Y*b.Y;
}
public enum RoundPhase { Ready,Charging,Stroke,Rolling,WhitePlacement,Finished }
public sealed class Ball
{
    public int PottedBy {get;set;}
    public BallStyle Style {get;set;}
    public int Id {get;init;}
    public int Source {get;init;}=-1;
    public Vec Position {get;set;}
    public Vec Velocity {get;set;}
    public double Radius {get;init;}=19;
    public bool Sunk {get;set;}
    public double ExitAge {get;set;}=1;
    public Vec ExitFrom {get;set;}
    public Vec Pocket {get;set;}
    public double Impact {get;set;}
    public double Angle {get;set;}
}
public readonly record struct AimPreview(Vec Contact,Vec? ObjectDirection,Vec? WhiteDirection,int? BallId);

public sealed class BilliardsRound
{
    public const double Width=1600,Height=900,Left=80,Top=80,Right=1520,Bottom=820,PocketRadius=33;
    private readonly List<Ball> _balls=[];
    private readonly Dictionary<(int,int),List<Ball>> _grid=[];
    private readonly List<List<Ball>> _buckets=[];
    public IReadOnlyList<Ball> Balls=>_balls;
    public Ball White=>_balls[0];
    public Vec[] Pockets {get;}=[new(Left,Top),new(800,Top),new(Right,Top),new(Left,Bottom),new(800,Bottom),new(Right,Bottom)];
    public RoundPhase Phase {get;private set;}=RoundPhase.Ready;
    public bool Paused {get;private set;}
    public bool Started {get;private set;}
    public int RoundNumber {get;private set;}
    public int Shots {get;private set;}
    public double Elapsed {get;private set;}
    public int Total=>_balls.Count-1;
    public string RuleId {get;private set;}=BilliardsRules.Eight;
    public int Pocketed=>_balls.Count(b=>b.Id!=0&&b.Sunk);
    public BilliardsMatch Match {get;private set;}=new(BilliardsRules.Eight,1);
    private readonly List<int> _pocketOrder=[],_shotPockets=[];
    public IReadOnlyList<int> PocketOrder=>_pocketOrder;
    public int TrayVersion {get;private set;}
    public Action<BallSound>? Sound {get;set;}
    private int? _firstContact;
    private bool _railAfterContact;
    public bool NeedsFrames=>!Paused&&(Phase is RoundPhase.Rolling or RoundPhase.Stroke or RoundPhase.Charging||_balls.Any(b=>b.Sunk&&b.ExitAge<.62));
    public bool CanShoot=>!Paused&&Phase==RoundPhase.Ready;
    private Vec _aim=new(1,0);
    private double _power,_stroke;
    public BilliardsRound(int sourceCount,int count){Reset(sourceCount,count);}
    public BilliardsRound(int sourceCount,string rule){Reset(sourceCount,rule);}
    public BilliardsRound(int sourceCount,string rule,int players){Reset(sourceCount,rule,players);}
    public void Reset(int sourceCount,string rule,int players=1)
    {
        var profile=BilliardsRules.Get(rule);Reset(sourceCount,profile.Count);RuleId=profile.Id;
        foreach(var b in _balls.Skip(1))b.Style=BilliardsRules.Style(RuleId,b.Id-1);
        if(RuleId==BilliardsRules.Eight)
        {
            (_balls[5].Position,_balls[8].Position)=(_balls[8].Position,_balls[5].Position);
            // The rear corners contain one solid and one stripe.
            (_balls[11].Position,_balls[7].Position)=(_balls[7].Position,_balls[11].Position);
        }
        else if(RuleId==BilliardsRules.Nine)
        {
            int index=1;foreach(int column in Enumerable.Range(0,5))
            {
                int rows=column<3?column+1:5-column;
                for(int row=0;row<rows;row++)_balls[index++].Position=new(1100+column*33.5,450+(row-(rows-1)*.5)*39);
            }
            (_balls[5].Position,_balls[9].Position)=(_balls[9].Position,_balls[5].Position);
        }
        else if(RuleId==BilliardsRules.Snooker)
        {
            White.Position=new(280,520);
            Vec[] spots=[new(400,600),new(400,300),new(400,450),new(800,450),new(1050,450),new(1430,450)];
            for(int n=0;n<6;n++)_balls[16+n].Position=spots[n];
        }
        Match=new(RuleId,players);Match.Begin(_balls);
    }
    public void Reset(int sourceCount,int count)
    {
        if(sourceCount is <1 or >4096||count is <9 or >45)throw new ArgumentOutOfRangeException(nameof(count));
        RuleId=BilliardsRules.Eight;
        _pocketOrder.Clear();_shotPockets.Clear();TrayVersion++;_firstContact=null;_railAfterContact=false;Match=new(RuleId,1);
        foreach(var bucket in _buckets)bucket.Clear();_grid.Clear();_balls.Clear();_balls.Add(new(){Id=0,Position=new(420,450),Radius=18});int n=0;
        for(int column=0;n<count;column++)for(int row=0;row<=column&&n<count;row++,n++)_balls.Add(new(){Id=n+1,Source=n%sourceCount,Position=new(1100+column*33.5,450+(row-column*.5)*39),Radius=19,Style=BilliardsRules.Style(null,n)});
        Phase=RoundPhase.Ready;Paused=false;Started=false;Shots=0;Elapsed=0;_stroke=0;RoundNumber++;
    }
    public void SetPaused(bool value){Paused=value;if(value&&Phase==RoundPhase.Charging)Phase=RoundPhase.Ready;}
    public bool BeginCharge(){if(!CanShoot)return false;Phase=RoundPhase.Charging;return true;}
    public void CancelCharge(){if(Phase==RoundPhase.Charging)Phase=RoundPhase.Ready;}
    public bool Shoot(Vec target,double power)
    {
        if(Paused||Phase is not (RoundPhase.Ready or RoundPhase.Charging)||!double.IsFinite(power)||!double.IsFinite(target.X)||!double.IsFinite(target.Y))return false;
        Match.Begin(_balls);_firstContact=null;_railAfterContact=false;_shotPockets.Clear();_aim=(target-White.Position).Unit;_power=500+Math.Clamp(power,.18,1)*1500;_stroke=0;Phase=RoundPhase.Stroke;Shots++;Started=true;return true;
    }
    public void Advance(double elapsed)
    {
        if(Paused||!double.IsFinite(elapsed)||elapsed<=0)return;
        if(Started&&Phase!=RoundPhase.Finished)Elapsed+=elapsed;
        double dt=Math.Min(.05,elapsed);foreach(var b in _balls)if(b.Sunk)b.ExitAge+=dt;
        if(Phase==RoundPhase.Stroke){_stroke+=dt;if(_stroke<.082)return;White.Velocity=_aim*_power;Phase=RoundPhase.Rolling;Sound?.Invoke(new(BallSoundKind.Cue,(_power-500)/1500));}
        if(Phase!=RoundPhase.Rolling)return;
        double speed=0;foreach(var b in _balls)if(!b.Sunk)speed=Math.Max(speed,b.Velocity.Length);
        int steps=Math.Clamp((int)Math.Ceiling(dt*Math.Max(240,speed/7)),1,32);double h=dt/steps;
        for(int step=0;step<steps;step++)
        {
            foreach(var b in _balls)
            {
                if(b.Sunk)continue;
                b.Position+=b.Velocity*h;
                foreach(var pocket in Pockets){var d=pocket-b.Position;double range=PocketRadius+b.Radius*.35;if(Vec.Dot(d,d)>=range*range)continue;double impact=b.Velocity.Length;b.Sunk=true;b.ExitAge=0;b.ExitFrom=b.Position;b.Pocket=pocket;b.Velocity=new();b.PottedBy=Match.CurrentPlayer;_shotPockets.Add(b.Id);if(b.Id>0){_pocketOrder.Add(b.Id);TrayVersion++;}Sound?.Invoke(new(BallSoundKind.Pocket,Math.Clamp(impact/1400,.15,1)));break;}
                if(b.Sunk)continue;Walls(b);
            }
            _grid.Clear();int used=0;
            foreach(var b in _balls)
            {
                if(b.Sunk)continue;
                int x=(int)Math.Floor(b.Position.X/40),y=(int)Math.Floor(b.Position.Y/40);
                for(int yy=y-1;yy<=y+1;yy++)for(int xx=x-1;xx<=x+1;xx++)if(_grid.TryGetValue((xx,yy),out var others))foreach(var other in others){double hit=Collide(other,b);if(hit>0){if(_firstContact is null&&(b.Id==0||other.Id==0))_firstContact=b.Id==0?other.Id:b.Id;if(hit>35)Sound?.Invoke(new(BallSoundKind.Collision,Math.Min(1,hit/1600)));}}
                if(!_grid.TryGetValue((x,y),out var bucket)){if(used>=_buckets.Count)_buckets.Add([]);bucket=_buckets[used++];bucket.Clear();_grid[(x,y)]=bucket;}bucket.Add(b);
            }
            foreach(var b in _balls){if(b.Sunk)continue;Walls(b);double v=b.Velocity.Length;if(v>0)b.Velocity*=Math.Max(0,1-335*h/v);b.Impact*=Math.Exp(-h*12);b.Angle*=Math.Exp(-h*4);}
        }
        if(Pocketed==Total&&Match.Players==1){Phase=RoundPhase.Finished;return;}
        if(_balls.Where(b=>!b.Sunk).All(b=>b.Velocity.Length<7))
        {
            foreach(var b in _balls)b.Velocity=new();var result=Match.Resolve(_balls,_firstContact,_shotPockets,_railAfterContact);
            foreach(int id in result.Respot)Respot(_balls[id]);
            if(result.Finished){Phase=RoundPhase.Finished;return;}
            if(result.FreeBall){White.Sunk=true;White.ExitAge=1;Phase=RoundPhase.WhitePlacement;}
            else{Phase=White.Sunk?RoundPhase.WhitePlacement:RoundPhase.Ready;if(White.Sunk)TryAutoPlace();}
            Match.Begin(_balls);
        }
    }
    private void Respot(Ball ball)
    {
        Vec preferred=RuleId==BilliardsRules.Snooker?ball.Id switch{16=>new(400,600),17=>new(400,300),18=>new(400,450),19=>new(800,450),20=>new(1050,450),_=>new(1430,450)}:new(1100,450);
        var choices=new List<Vec>{preferred};for(double x=Right-50;x>Left+50;x-=45)for(double y=Top+60;y<Bottom-50;y+=45)choices.Add(new(x,y));
        foreach(var point in choices.OrderBy(p=>Vec.Dot(p-preferred,p-preferred)))if(_balls.All(b=>b.Id==ball.Id||b.Sunk||(b.Position-point).Length>=b.Radius+ball.Radius+2)&&Pockets.All(p=>(p-point).Length>60)){ball.Position=point;ball.Sunk=false;ball.ExitAge=1;ball.Velocity=new();_pocketOrder.Remove(ball.Id);TrayVersion++;return;}
    }
    private void TryAutoPlace(){foreach(var x in new[]{420d,320,520,220,620})for(double y=260;y<=660;y+=50)if(PlaceWhite(new(x,y)))return;}
    public bool PlaceWhite(Vec p)
    {
        if(Paused||Phase!=RoundPhase.WhitePlacement||!double.IsFinite(p.X)||!double.IsFinite(p.Y)||p.X<Left+25||p.X>Right-25||p.Y<Top+25||p.Y>Bottom-25||Pockets.Any(k=>(k-p).Length<60)||_balls.Any(b=>b.Id!=0&&!b.Sunk&&(b.Position-p).Length<b.Radius+White.Radius+5))return false;
        White.Position=p;White.Velocity=new();White.Sunk=false;White.ExitAge=1;Phase=RoundPhase.Ready;return true;
    }
    private void Walls(Ball b)
    {
        double x=b.Position.X,y=b.Position.Y,vx=b.Velocity.X,vy=b.Velocity.Y;
        if(x<Left+b.Radius){x=Left+b.Radius;if(vx<0)vx=-vx*.88;}if(x>Right-b.Radius){x=Right-b.Radius;if(vx>0)vx=-vx*.88;}
        if(y<Top+b.Radius){y=Top+b.Radius;if(vy<0)vy=-vy*.88;}if(y>Bottom-b.Radius){y=Bottom-b.Radius;if(vy>0)vy=-vy*.88;}
        double impact=Math.Max(Math.Abs(vx-b.Velocity.X),Math.Abs(vy-b.Velocity.Y));if(impact>0&&_firstContact is not null)_railAfterContact=true;if(impact>40)Sound?.Invoke(new(BallSoundKind.Cushion,Math.Min(1,impact/2200)));b.Position=new(x,y);b.Velocity=new(vx,vy);
    }
    public static double Collide(Ball a,Ball b)
    {
        Vec delta=b.Position-a.Position;double limit=a.Radius+b.Radius,dist=delta.Length;if(dist>=limit)return 0;
        Vec normal=dist>1e-6?delta/dist:new(1,0);Vec correction=normal*((limit-dist)*.5);a.Position-=correction;b.Position+=correction;
        double closing=Vec.Dot(b.Velocity-a.Velocity,normal);if(closing>=0)return 0;
        double impulse=-closing*.96;a.Velocity-=normal*impulse;b.Velocity+=normal*impulse;a.Impact=b.Impact=Math.Min(.25,impulse/2500);a.Angle-=normal.Y*12;b.Angle+=normal.Y*12;return impulse;
    }
    public AimPreview Preview(Vec target)
    {
        Vec p=White.Position,d=(target-p).Unit;double nearest=double.PositiveInfinity;Ball? hit=null;
        foreach(var b in _balls.Where(b=>b.Id!=0&&!b.Sunk)){var delta=b.Position-p;double along=Vec.Dot(delta,d),cross=Vec.Dot(delta,delta)-along*along,r=White.Radius+b.Radius;if(along<0||cross>r*r)continue;double t=along-Math.Sqrt(Math.Max(0,r*r-cross));if(t>=0&&t<nearest){nearest=t;hit=b;}}
        double wall=double.PositiveInfinity;
        foreach(var (distance,component) in new[]{(Left+White.Radius-p.X,d.X),(Right-White.Radius-p.X,d.X),(Top+White.Radius-p.Y,d.Y),(Bottom-White.Radius-p.Y,d.Y)})if(Math.Abs(component)>1e-9&&distance/component>1e-6)wall=Math.Min(wall,distance/component);
        if(wall<nearest){var contact=p+d*wall;bool side=Math.Abs(contact.X-(Left+White.Radius))<1||Math.Abs(contact.X-(Right-White.Radius))<1;return new(contact,null,side?new(-d.X,d.Y):new(d.X,-d.Y),null);}
        if(hit is not null){var contact=p+d*nearest;Vec normal=(hit.Position-contact).Unit;return new(contact,normal,d-normal*Vec.Dot(d,normal),hit.Id);}return new(p+d*100,null,null,null);
    }
}
