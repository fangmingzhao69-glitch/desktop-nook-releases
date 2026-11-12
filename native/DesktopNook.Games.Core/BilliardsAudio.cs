namespace DesktopNook.Games.Core;

public enum BallSoundKind { Cue,Collision,Cushion,Pocket }
public readonly record struct BallSound(BallSoundKind Kind,double Strength);
public static class BilliardsAudio
{
    public const int SampleRate=44100;
    private static readonly Dictionary<string,short[]> Bank=[];
    private static readonly object Gate=new();
    private static short[] Recording(string name)
    {
        lock(Gate)
        {
            if(Bank.TryGetValue(name,out var saved))return saved;
            using var input=typeof(BilliardsAudio).Assembly.GetManifestResourceStream("DesktopNook.Audio."+name+".pcm")??throw new InvalidDataException("台球录音资源缺失。");
            if(input.Length is <2 or >44100||input.Length%2!=0)throw new InvalidDataException("台球录音资源格式无效。");
            using var reader=new BinaryReader(input);var pcm=new short[input.Length/2];for(int n=0;n<pcm.Length;n++)pcm[n]=reader.ReadInt16();Bank[name]=pcm;return pcm;
        }
    }
    public static short[] Samples(BallSound sound,int variant=0)
    {
        double power=double.IsFinite(sound.Strength)?Math.Clamp(sound.Strength,0,1):0;
        string name=sound.Kind switch{BallSoundKind.Cue=>power<.48?"cue-soft":"cue-hard",BallSoundKind.Collision=>variant%2==0?"collision-a":"collision-b",BallSoundKind.Cushion=>"cushion",_=>"pocket"};
        var recording=Recording(name);var samples=new short[recording.Length];double gain=.12+.78*Math.Pow(power,.75);
        for(int n=0;n<samples.Length;n++)
        {
            samples[n]=(short)Math.Clamp((int)Math.Round(recording[n]*gain),short.MinValue,short.MaxValue);
        }
        return samples;
    }
}
