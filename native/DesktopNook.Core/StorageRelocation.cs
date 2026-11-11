using System.Text.Json;

namespace DesktopNook.Core;

public sealed class StorageReceipt
{
    public string Layout {get;set;}="installation-v1";
    public string SourceRoot {get;set;}="";
    public string SourceFingerprint {get;set;}="";
    public string TargetFingerprint {get;set;}="";
    public DateTime CompletedUtc {get;set;}=DateTime.UtcNow;
}

public static class StorageRelocation
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static string Fingerprint(NookState state)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state,Json))));
    public static void WriteJson<T>(string path,T value)
    {
        var temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        if(File.Exists(path))NookStore.CopyPreservingStreams(path,temporary);
        using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,value,Json);stream.Flush(true);}
        File.Move(temporary,path,true);
    }
    public static StorageReceipt Migrate(string source,string target,IEnumerable<string> desktops,IRecycleBin recycle,Action<string>? checkpoint=null)
    {
        source=Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if(source.Equals(target,StringComparison.OrdinalIgnoreCase)||target.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("新数据目录不能位于旧数据目录内部。");
        if(Directory.Exists(target)||File.Exists(target))throw new IOException("新数据目录已存在，未覆盖已有内容。");
        if(File.Exists(Path.Combine(source,"migration.json")))throw new IOException("旧目录有未完成的收纳操作，请先用原版本正常打开并完成恢复，再迁移。");
        var before=VerifiedContent.Snapshot(source);
        var stage=target+".migrating-"+Guid.NewGuid().ToString("N");
        VerifiedContent.Copy(source,stage,StorageSecurity.Protect);checkpoint?.Invoke("storage-copied");
        var staged=new NookStore(stage,desktops,recycle);
        var receipt=new StorageReceipt{SourceRoot=source,SourceFingerprint=Fingerprint(staged.State)};
        var external=new Dictionary<string,SortedDictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
        void Rewrite(NookItem item,bool active)
        {
            if(!Guid.TryParseExact(item.Id,"N",out _)||Path.GetFileName(item.FileName)!=item.FileName||(item.Kind!="recycle-bin"&&(item.FileName is "" or "." or ".."||item.FileName.IndexOfAny(Path.GetInvalidFileNameChars())>=0)))throw new IOException("恢复记录的路径或标识无效，迁移停止。");
            if(item.Kind is not ("file" or "folder"))return;
            var oldPath=staged.ManagedPath(item);
            var next=Path.Combine(stage,"vault",item.Id,"content",item.FileName);
            if(!oldPath.Equals(next,StringComparison.OrdinalIgnoreCase)&&(File.Exists(oldPath)||Directory.Exists(oldPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(next)!);
                if(File.Exists(next)||Directory.Exists(next))
                {
                    if(!VerifiedContent.Equal(VerifiedContent.Snapshot(oldPath),VerifiedContent.Snapshot(next)))throw new IOException("旧文件库与迁移目录存在内容冲突，未覆盖任何一处。");
                }
                else VerifiedContent.Copy(oldPath,next);
                if(!oldPath.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))external[oldPath]=VerifiedContent.Snapshot(oldPath);
            }
            else if(active&&!File.Exists(next)&&!Directory.Exists(next))throw new IOException("收纳内容缺失，未发布新数据目录："+item.Name);
            item.PayloadPath="";item.PayloadLayout="content-v1";
        }
        foreach(var item in staged.State.Items)Rewrite(item,true);
        WriteJson(Path.Combine(stage,"state.json"),staged.State);
        var backup=Path.Combine(stage,"state.json.bak");
        if(File.Exists(backup))
        {
            try{var state=JsonSerializer.Deserialize<NookState>(File.ReadAllText(backup))!;foreach(var item in state.Items)Rewrite(item,false);WriteJson(backup,state);}
            catch(JsonException){/* Retain a damaged historical backup unchanged. */}
        }
        foreach(var directory in Directory.EnumerateDirectories(Path.Combine(stage,"vault")))
        {
            if(!Guid.TryParseExact(Path.GetFileName(directory),"N",out _))continue;
            var path=Path.Combine(directory,"entry.json");if(!File.Exists(path))continue;
            var manifest=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))??throw new IOException("恢复清单为空。");
            if(manifest.Item.Id!=Path.GetFileName(directory))throw new IOException("恢复清单与目录标识不一致。");
            Rewrite(manifest.Item,false);WriteJson(path,manifest);
        }
        var verified=new NookStore(stage,desktops,recycle,storeAllPayloads:true);
        foreach(var item in verified.State.Items)
            if(item.Kind!="recycle-bin"&&!File.Exists(verified.ManagedPath(item))&&!Directory.Exists(verified.ManagedPath(item)))throw new IOException("新目录的实际内容核验失败。");
        receipt.TargetFingerprint=Fingerprint(verified.State);
        WriteJson(Path.Combine(stage,"storage.json"),receipt);
        checkpoint?.Invoke("storage-verified");
        if(!VerifiedContent.Equal(before,VerifiedContent.Snapshot(source))||external.Any(p=>!VerifiedContent.Equal(p.Value,VerifiedContent.Snapshot(p.Key))))throw new IOException("迁移过程中旧数据发生变化，旧目录已保留，新目录尚未启用。");
        Directory.Move(stage,target);checkpoint?.Invoke("storage-published");
        return receipt;
    }
}
