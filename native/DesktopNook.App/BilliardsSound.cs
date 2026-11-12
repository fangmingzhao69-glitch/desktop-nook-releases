using System.Runtime.InteropServices;
using System.Windows.Threading;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
// One device and at most three short PCM buffers, all owned by the game process.
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
    private readonly DispatcherTimer _cleanup=new(){Interval=TimeSpan.FromMilliseconds(25)};
    private IntPtr _device;
    private bool _failed,_disposed;
    private long _lastImpact;
    internal bool Enabled {get;set;}=true;
    internal int Submitted {get;private set;}
    internal int Pending=>_buffers.Count;
    internal bool DeviceOpened=>_device!=IntPtr.Zero;
    internal bool Unavailable=>_failed;
    internal BilliardsSound(){_cleanup.Tick+=(_,_)=>Reap();}
    internal void Play(BallSound sound)
    {
        if(!Enabled||_disposed||_failed)return;
        Reap();long now=Environment.TickCount64;
        if(sound.Kind!=BallSoundKind.Cue&&now-_lastImpact<45)return;
        if(_buffers.Count>=3)return;
        if(_device==IntPtr.Zero)
        {
            var format=new Format{Tag=1,Channels=1,Rate=BilliardsAudio.SampleRate,Bytes=BilliardsAudio.SampleRate*2,Align=2,Bits=16};
            if(waveOutOpen(out _device,uint.MaxValue,ref format,IntPtr.Zero,IntPtr.Zero,0)!=0){_device=IntPtr.Zero;_failed=true;return;}
        }
        var samples=BilliardsAudio.Samples(sound);var data=Marshal.AllocHGlobal(samples.Length*2);Marshal.Copy(samples,0,data,samples.Length);
        var header=Marshal.AllocHGlobal(Marshal.SizeOf<Header>());Marshal.StructureToPtr(new Header{Data=data,Length=(uint)(samples.Length*2)},header,false);
        uint size=(uint)Marshal.SizeOf<Header>();
        if(waveOutPrepareHeader(_device,header,size)!=0){Marshal.FreeHGlobal(header);Marshal.FreeHGlobal(data);_failed=true;return;}
        if(waveOutWrite(_device,header,size)!=0){waveOutUnprepareHeader(_device,header,size);Marshal.FreeHGlobal(header);Marshal.FreeHGlobal(data);_failed=true;return;}
        _buffers.Add((header,data));Submitted++;_lastImpact=now;_cleanup.Start();
    }
    private void Reap()
    {
        for(int n=_buffers.Count-1;n>=0;n--){var b=_buffers[n];if((Marshal.PtrToStructure<Header>(b.Header).Flags&1)==0)continue;if(waveOutUnprepareHeader(_device,b.Header,(uint)Marshal.SizeOf<Header>())!=0)continue;Marshal.FreeHGlobal(b.Header);Marshal.FreeHGlobal(b.Data);_buffers.RemoveAt(n);}
        if(_buffers.Count==0)_cleanup.Stop();
    }
    internal void Stop(){if(_device!=IntPtr.Zero){waveOutReset(_device);Reap();}_cleanup.Stop();}
    public void Dispose(){if(_disposed)return;_disposed=true;Stop();if(_device!=IntPtr.Zero){waveOutClose(_device);_device=IntPtr.Zero;}}
}
