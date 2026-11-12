using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
// Recorded transients overlap on one device: six voices, two 20 ms output blocks.
// The worker belongs to the temporary game and sleeps when no sound is pending.
internal sealed class BilliardsSound : IDisposable
{
    [StructLayout(LayoutKind.Sequential,Pack=2)]private struct Format{public ushort Tag,Channels;public uint Rate,Bytes;public ushort Align,Bits,Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Header{public IntPtr Data;public uint Length,Recorded;public UIntPtr User;public uint Flags,Loops;public IntPtr Next;public UIntPtr Reserved;}
    [DllImport("winmm.dll")]private static extern uint waveOutOpen(out IntPtr handle,uint device,ref Format format,IntPtr callback,IntPtr instance,uint flags);
    [DllImport("winmm.dll")]private static extern uint waveOutPrepareHeader(IntPtr device,IntPtr header,uint size);
    [DllImport("winmm.dll")]private static extern uint waveOutWrite(IntPtr device,IntPtr header,uint size);
    [DllImport("winmm.dll")]private static extern uint waveOutUnprepareHeader(IntPtr device,IntPtr header,uint size);
    [DllImport("winmm.dll")]private static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")]private static extern uint waveOutClose(IntPtr device);
    private readonly List<(IntPtr Header,IntPtr Data)> _buffers=[];
    private sealed class Voice(short[] samples){internal readonly short[] Samples=samples;internal int Position;}
    private readonly List<Voice> _voices=[];
    private readonly ConcurrentQueue<(BallSound Sound,int Epoch)> _queue=new();
    private readonly AutoResetEvent _signal=new(false);
    private Thread? _worker;
    private IntPtr _device;
    private volatile bool _failed,_disposed,_enabled=true;
    private int _epoch,_submitted,_pending,_opened,_variation;
    private long _lastImpact;
    internal bool Enabled {get=>_enabled;set=>_enabled=value;}
    internal int Submitted=>Volatile.Read(ref _submitted);
    internal int Pending=>Volatile.Read(ref _pending);
    internal bool DeviceOpened=>Volatile.Read(ref _opened)!=0;
    internal bool Unavailable=>_failed;
    internal void Play(BallSound sound)
    {
        if(!Enabled||_disposed||_failed)return;
        long now=Environment.TickCount64;if(sound.Kind!=BallSoundKind.Cue&&now-Interlocked.Read(ref _lastImpact)<30)return;
        if(_queue.Count>=24)return;Interlocked.Exchange(ref _lastImpact,now);_queue.Enqueue((sound,Volatile.Read(ref _epoch)));
        if(_worker is null){_worker=new Thread(Run){IsBackground=true,Name="Billiards recorded audio"};_worker.Start();}_signal.Set();
    }
    private void Run()
    {
        int epoch=Volatile.Read(ref _epoch);
        try
        {
            while(!_disposed)
            {
                int current=Volatile.Read(ref _epoch);if(epoch!=current){Reset();epoch=current;}
                while(_queue.TryDequeue(out var queued))
                {
                    if(queued.Epoch!=epoch||!Enabled)continue;
                    if(_device==IntPtr.Zero)
                    {
                        var format=new Format{Tag=1,Channels=1,Rate=BilliardsAudio.SampleRate,Bytes=BilliardsAudio.SampleRate*2,Align=2,Bits=16};
                        if(waveOutOpen(out _device,uint.MaxValue,ref format,IntPtr.Zero,IntPtr.Zero,0)!=0){_device=IntPtr.Zero;_failed=true;return;}Volatile.Write(ref _opened,1);
                    }
                    if(_voices.Count>=6)_voices.RemoveAt(0);_voices.Add(new(BilliardsAudio.Samples(queued.Sound,_variation++)));
                }
                if(epoch!=Volatile.Read(ref _epoch)||_disposed)continue;
                Reap();while(!_failed&&_buffers.Count<2&&_voices.Count>0)SubmitMix();if(_failed)return;
                Volatile.Write(ref _pending,_buffers.Count+_voices.Count);
                _signal.WaitOne(_pending>0?5:Timeout.Infinite);
            }
        }
        catch(Exception){_failed=true;}
        finally{Reset();if(_device!=IntPtr.Zero){waveOutClose(_device);_device=IntPtr.Zero;}Volatile.Write(ref _opened,0);Volatile.Write(ref _pending,0);}
    }
    private void SubmitMix()
    {
        int count=BilliardsAudio.SampleRate/50;var sum=new int[count];
        for(int v=_voices.Count-1;v>=0;v--)
        {
            var voice=_voices[v];int length=Math.Min(count,voice.Samples.Length-voice.Position);
            for(int n=0;n<length;n++)sum[n]+=voice.Samples[voice.Position+n];voice.Position+=length;if(voice.Position>=voice.Samples.Length)_voices.RemoveAt(v);
        }
        var samples=new short[count];for(int n=0;n<count;n++){double x=sum[n]/32768d;if(Math.Abs(x)>.85)x=Math.CopySign(.85+.1*(1-Math.Exp(-(Math.Abs(x)-.85)*3)),x);samples[n]=(short)Math.Clamp((int)(x*32767),short.MinValue,short.MaxValue);}
        var data=Marshal.AllocHGlobal(samples.Length*2);Marshal.Copy(samples,0,data,samples.Length);
        var header=Marshal.AllocHGlobal(Marshal.SizeOf<Header>());Marshal.StructureToPtr(new Header{Data=data,Length=(uint)(samples.Length*2)},header,false);
        uint size=(uint)Marshal.SizeOf<Header>();
        if(waveOutPrepareHeader(_device,header,size)!=0){Marshal.FreeHGlobal(header);Marshal.FreeHGlobal(data);_failed=true;return;}
        if(waveOutWrite(_device,header,size)!=0){waveOutUnprepareHeader(_device,header,size);Marshal.FreeHGlobal(header);Marshal.FreeHGlobal(data);_failed=true;return;}
        _buffers.Add((header,data));Interlocked.Increment(ref _submitted);
    }
    private void Reap()
    {
        for(int n=_buffers.Count-1;n>=0;n--){var b=_buffers[n];if((Marshal.PtrToStructure<Header>(b.Header).Flags&1)==0)continue;if(waveOutUnprepareHeader(_device,b.Header,(uint)Marshal.SizeOf<Header>())!=0)continue;Marshal.FreeHGlobal(b.Header);Marshal.FreeHGlobal(b.Data);_buffers.RemoveAt(n);}
    }
    private void Reset(){_voices.Clear();if(_device!=IntPtr.Zero){waveOutReset(_device);Reap();}}
    internal void Stop(){_queue.Clear();Interlocked.Increment(ref _epoch);if(!_disposed)_signal.Set();}
    public void Dispose(){if(_disposed)return;_disposed=true;_signal.Set();if(_worker is null||_worker.Join(1000))_signal.Dispose();}
}
