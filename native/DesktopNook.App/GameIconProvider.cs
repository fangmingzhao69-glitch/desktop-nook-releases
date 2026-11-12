using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;
internal sealed class GameIconProvider : IDisposable
{
    private volatile bool _stopped;
    internal void Start(IEnumerable<GameIcon> icons,Action<string,ImageSource> ready)
    {
        var sources=icons.DistinctBy(i=>i.Recycle?"::recycle":i.Path,StringComparer.OrdinalIgnoreCase).Take(45).ToArray();
        var worker=new Thread(()=>
        {
            foreach(var icon in sources)
            {
                if(_stopped)return;
                try
                {
                    if(SharpIcons.Load(icon.Recycle?"::"+WindowsRecycleBin.RecycleId:icon.Path,128) is not BitmapSource original)continue;
                    double scale=Math.Min(1,128d/Math.Max(original.PixelWidth,original.PixelHeight));
                    BitmapSource resized=scale<1?new TransformedBitmap(original,new ScaleTransform(scale,scale)):original;
                    if(resized.Format!=PixelFormats.Pbgra32)resized=new FormatConvertedBitmap(resized,PixelFormats.Pbgra32,null,0);
                    int stride=resized.PixelWidth*4;var pixels=new byte[stride*resized.PixelHeight];resized.CopyPixels(pixels,stride,0);
                    // An independent pixel copy drops references to larger Shell images.
                    var image=BitmapSource.Create(resized.PixelWidth,resized.PixelHeight,96,96,PixelFormats.Pbgra32,null,pixels,stride);image.Freeze();
                    if(!_stopped)ready(icon.Recycle?"::recycle":icon.Path,image);
                }
                catch(Exception){/* Keep the lightweight brand placeholder. */}
            }
        }){IsBackground=true,Name="Billiards icon loader"};worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
    public void Dispose()=>_stopped=true;
}
