using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopNook.Core;

// Bounded streaming copies, including named data streams. Publish only verified
// copies; delete source entries individually, never recursively follow links.
public static class VerifiedContent
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref int information,uint size);
    private static FileAttributes Attributes(string path)
    {
        var value=File.GetAttributes(path);
        if((value&FileAttributes.ReparsePoint)!=0)throw new IOException("迁移不处理链接、联接或未落地的云占位项："+path);
        return value;
    }
    private static string DigestFile(string path)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string stream){hash.AppendData(Encoding.UTF8.GetBytes(stream+"\0"));using var input=new FileStream(path+stream,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);hash.AppendData(SHA256.HashData(input));}
        Add("");foreach(var stream in WindowsStreams.Names(path).Order(StringComparer.Ordinal))Add(stream);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string DigestDirectory(string path)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var stream in WindowsStreams.Names(path).Order(StringComparer.Ordinal)){hash.AppendData(Encoding.UTF8.GetBytes(stream+"\0"));using var input=File.OpenRead(path+stream);hash.AppendData(SHA256.HashData(input));}
        return "directory:"+Convert.ToHexString(hash.GetHashAndReset());
    }
    public static SortedDictionary<string,string> Snapshot(string path)
    {
        var result=new SortedDictionary<string,string>(StringComparer.Ordinal);
        void Scan(string entry,string relative)
        {
            if((Attributes(entry)&FileAttributes.Directory)!=0)
            {
                result.Add(relative,DigestDirectory(entry));
                foreach(var child in Directory.EnumerateFileSystemEntries(entry).Order(StringComparer.Ordinal))Scan(child,relative.Length==0?Path.GetFileName(child):relative+"/"+Path.GetFileName(child));
            }
            else result.Add(relative,DigestFile(entry));
        }
        Scan(Path.GetFullPath(path),"");return result;
    }
    public static bool Equal(IReadOnlyDictionary<string,string> a,IReadOnlyDictionary<string,string> b)=>a.Count==b.Count&&a.All(p=>b.TryGetValue(p.Key,out var value)&&value==p.Value);
    public static void Copy(string source,string destination,Action<string>? initializeRoot=null)
    {
        source=Path.GetFullPath(source);destination=Path.GetFullPath(destination);
        if(Directory.Exists(source)&&destination.StartsWith(Path.TrimEndingDirectorySeparator(source)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("复制目标不能位于源目录内部。");
        if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("迁移目标已存在，未覆盖："+destination);
        var before=Snapshot(source);
        void CopyEntry(string from,string to)
        {
            var attributes=Attributes(from);
            if((attributes&FileAttributes.Directory)!=0)
            {
                Directory.CreateDirectory(to);
                if(from.Equals(source,StringComparison.OrdinalIgnoreCase))initializeRoot?.Invoke(to);
                if((attributes&FileAttributes.Encrypted)!=0){if(!OperatingSystem.IsWindows())throw new IOException("目标系统不支持原文件的加密。");File.Encrypt(to);}
                foreach(var child in Directory.EnumerateFileSystemEntries(from))CopyEntry(child,Path.Combine(to,Path.GetFileName(child)));
                NookStore.CopyNamedStreams(from,to);
                Directory.SetCreationTimeUtc(to,Directory.GetCreationTimeUtc(from));Directory.SetLastWriteTimeUtc(to,Directory.GetLastWriteTimeUtc(from));
                File.SetAttributes(to,File.GetAttributes(to)|(attributes&(FileAttributes.Hidden|FileAttributes.System|FileAttributes.ReadOnly|FileAttributes.NotContentIndexed)));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                // Native CopyFile preserves EFS or fails. Never encrypt an external
                // parent (e.g. the user's desktop), and never use plaintext fallback.
                if((attributes&FileAttributes.Encrypted)!=0)File.Copy(from,to,false);
                else NookStore.CopyPreservingStreams(from,to);
                if((attributes&FileAttributes.Encrypted)!=0&&(File.GetAttributes(to)&FileAttributes.Encrypted)==0)throw new IOException("目标未保留文件加密，迁移停止。");
                var copiedAttributes=File.GetAttributes(to);if((copiedAttributes&FileAttributes.ReadOnly)!=0)File.SetAttributes(to,copiedAttributes&~FileAttributes.ReadOnly);
                File.SetCreationTimeUtc(to,File.GetCreationTimeUtc(from));File.SetLastWriteTimeUtc(to,File.GetLastWriteTimeUtc(from));File.SetAttributes(to,copiedAttributes);
            }
        }
        CopyEntry(source,destination);
        if(!Equal(before,Snapshot(source))||!Equal(before,Snapshot(destination)))throw new IOException("迁移前后内容或附加流不一致，原位置已保留。");
    }
    public static void MoveAcrossVolumes(string source,string destination,Action<string>? checkpoint=null)
    {
        var before=Snapshot(source);var folder=Directory.Exists(source);
        var temporary=destination+".transfer-"+Guid.NewGuid().ToString("N");
        Copy(source,temporary);checkpoint?.Invoke("cross-volume-copied");
        if(!Equal(before,Snapshot(source))||!Equal(before,Snapshot(temporary)))throw new IOException("复制期间源内容变化，已保留两处副本。");
        if(folder)Directory.Move(temporary,destination);else File.Move(temporary,destination,false);
        checkpoint?.Invoke("cross-volume-published");
        foreach(var pair in before.Where(p=>!p.Value.StartsWith("directory:",StringComparison.Ordinal)))
        {
            var from=pair.Key.Length==0?source:Path.Combine(source,pair.Key.Replace('/',Path.DirectorySeparatorChar));
            var to=pair.Key.Length==0?destination:Path.Combine(destination,pair.Key.Replace('/',Path.DirectorySeparatorChar));
            Attributes(from);
            if(OperatingSystem.IsWindows())
            {
                // Delete through the verified inode handle, never a path that
                // another process could replace after the final checksum.
                using var handle=CreateFile(from,0x80010000,1,IntPtr.Zero,3,0,IntPtr.Zero);
                if(handle.IsInvalid)throw new IOException("源文件正在使用或不可删除，已保留两处内容。",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                if(DigestFile(from)!=pair.Value||DigestFile(to)!=pair.Value)throw new IOException("源或目标文件已变化，未删除该源文件。");
                int remove=1;
                if(!SetFileInformationByHandle(handle,4,ref remove,sizeof(int)))throw new IOException("源文件未移除，两处内容已保留。",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            else
            {
                using var guard=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                if(DigestFile(from)!=pair.Value||DigestFile(to)!=pair.Value)throw new IOException("源或目标文件已变化，未删除该源文件。");
                File.Delete(from);
            }
        }
        if(folder)foreach(var pair in before.Where(p=>p.Value.StartsWith("directory:",StringComparison.Ordinal)).OrderByDescending(p=>p.Key.Length))
        {
            var from=pair.Key.Length==0?source:Path.Combine(source,pair.Key.Replace('/',Path.DirectorySeparatorChar));
            var to=pair.Key.Length==0?destination:Path.Combine(destination,pair.Key.Replace('/',Path.DirectorySeparatorChar));
            if(DigestDirectory(from)!=pair.Value||DigestDirectory(to)!=pair.Value)throw new IOException("目录附加流发生变化，原目录已保留。");
            Directory.Delete(from,false);
        }
        checkpoint?.Invoke("cross-volume-source-removed");
    }
}
