namespace DesktopNook.Games.Core;

public enum BallSoundKind { Cue,Collision,Cushion,Pocket }
public readonly record struct BallSound(BallSoundKind Kind,double Strength);
public static class BilliardsAudio
{
    public const int SampleRate=22050;
    public static short[] Samples(BallSound sound)
    {
        double power=double.IsFinite(sound.Strength)?Math.Clamp(sound.Strength,0,1):0;
        double duration=sound.Kind==BallSoundKind.Pocket?.095:.035+power*.04;
        var samples=new short[(int)(duration*SampleRate)];uint noise=0x312b647;
        double pitch=sound.Kind switch{BallSoundKind.Cue=>1100,BallSoundKind.Collision=>2300,BallSoundKind.Cushion=>550,_=>240};
        for(int n=0;n<samples.Length;n++)
        {
            noise=unchecked(noise*1664525+1013904223);double t=n/(double)SampleRate,random=(noise>>8)/8388607.5-1;
            double body=Math.Sin(2*Math.PI*pitch*t)*Math.Exp(-t/(duration*.18)),click=random*Math.Exp(-t/.0035);
            double envelope=Math.Min(1,n/6d)*(1-n/(double)samples.Length);double value=(body*.7+click*.3)*(.06+power*.65)*envelope;
            samples[n]=(short)(Math.Clamp(value,-.95,.95)*short.MaxValue);
        }
        return samples;
    }
}
