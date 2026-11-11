using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace DesktopNook.Core;

/// <summary>Durable, single-writer migration engine. Never moves installed software.</summary>
public sealed class NookStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly IRecycleBin _recycle;
    private readonly string[] _desktops;
    private readonly Action<string>? _checkpoint;
    public string DataDirectory { get; }
    public IReadOnlyList<string> DesktopRoots => _desktops;
    public NookState State { get; private set; } = new();
    public bool StoreAllPayloads {get;}
    public List<string> RecoveryMessages { get; } = [];
    private string StatePath => Path.Combine(DataDirectory, "state.json");
    private string JournalPath => Path.Combine(DataDirectory, "migration.json");
    private string Vault => Path.Combine(DataDirectory, "vault");
    private string CategoryPolicyPath=>Path.Combine(DataDirectory,"category-policy.json");
    private string OrderPath=>Path.Combine(DataDirectory,"item-order.json");
    private sealed class ItemOrder {public Dictionary<string,List<string>> Categories {get;set;}=[];}
    private void SaveOrder(NookState state)=>WriteAtomic(OrderPath,new ItemOrder{Categories=state.Categories.ToDictionary(c=>c.Id,c=>state.Items.Where(i=>i.CategoryId==c.Id).Select(i=>i.Id).ToList())},true);
    private bool ApplyOrder(bool recovering)
    {
        ItemOrder? saved=null;
        foreach(var path in recovering?new[]{OrderPath,OrderPath+".bak"}:new[]{OrderPath})
        {
            if(!File.Exists(path))continue;
            try
            {
                var order=Read<ItemOrder>(path);
                if(order.Categories is null||order.Categories.Values.Any(ids=>ids is null||ids.Count!=ids.Distinct(StringComparer.Ordinal).Count()||ids.Any(id=>!Guid.TryParseExact(id,"N",out _))))throw new JsonException("排序记录无效。");
                saved=order;break;
            }
            catch(Exception ex) when(ex is IOException or JsonException){RecoveryMessages.Add("排序记录不可用，保留索引中的现有顺序。");}
        }
        if(saved is null)return false;
        bool changed=false;
        foreach(var category in State.Categories)
        {
            if(!saved.Categories.TryGetValue(category.Id,out var ids))continue;
            var ranks=ids.Select((id,index)=>(id,index)).ToDictionary(pair=>pair.id,pair=>pair.index);
            var siblings=State.Items.Where(i=>i.CategoryId==category.Id).OrderBy(i=>ranks.GetValueOrDefault(i.Id,int.MaxValue)).ToArray();
            int position=0;
            for(int n=0;n<State.Items.Count;n++)if(State.Items[n].CategoryId==category.Id){var item=siblings[position++];changed|=State.Items[n].Id!=item.Id;State.Items[n]=item;}
        }
        return changed;
    }
    private sealed class CategoryPolicy {public bool UncategorizedRemoved {get;set;}}
    private bool UncategorizedRemoved()
    {
        foreach(var path in new[]{CategoryPolicyPath,CategoryPolicyPath+".bak"})
            if(File.Exists(path))try{return Read<CategoryPolicy>(path).UncategorizedRemoved;}catch(JsonException){}
        return false;
    }
    private void RememberCategoryDeletion()
    {
        if(!State.Categories.Any(c=>c.Id=="uncategorized")&&!UncategorizedRemoved())WriteAtomic(CategoryPolicyPath,new CategoryPolicy{UncategorizedRemoved=true},true);
    }

    public NookStore(string dataDirectory, IEnumerable<string> desktopRoots, IRecycleBin recycle, Action<string>? checkpoint = null,bool storeAllPayloads=false)
    {
        StoreAllPayloads=storeAllPayloads;
        DataDirectory = Path.GetFullPath(dataDirectory);
        _desktops = desktopRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_desktops.Length == 0) throw new ArgumentException("找不到可用桌面目录。");
        _recycle = recycle; _checkpoint = checkpoint;
        Directory.CreateDirectory(Vault);
        if ((File.GetAttributes(DataDirectory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(Vault) & FileAttributes.ReparsePoint) != 0) throw new IOException("数据目录不能是符号链接。");
        Load();
    }

    public string ManagedPath(NookItem item)
    {
        if(!string.IsNullOrEmpty(item.PayloadPath))
        {
            var expected=LocalPayload(item);if(!string.Equals(Path.GetFullPath(item.PayloadPath),expected,StringComparison.OrdinalIgnoreCase))throw new IOException("托管位置无效。");return expected;
        }
        return Mutable(item)&&item.PayloadLayout=="content-v1"?Path.Combine(ItemDirectory(item),"content",item.FileName):Path.Combine(ItemDirectory(item),item.FileName);
    }
    private static bool Mutable(NookItem item)=>item.Kind is "file" or "folder";
    private static bool Exists(string path)=>File.Exists(path)||Directory.Exists(path);
    private string LocalPayload(NookItem item)=>Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(item.OriginalPath))!,".DesktopNookVault",item.Id,item.FileName));
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string source,string destination,uint flags);
    private void MovePayload(string source,string destination,bool folder)
    {
        if(OperatingSystem.IsWindows()){if(!MoveFileExW(source,destination,8)){var error=Marshal.GetLastWin32Error();if(error==17&&StoreAllPayloads){VerifiedContent.MoveAcrossVolumes(source,destination,_checkpoint);return;}throw new IOException(new System.ComponentModel.Win32Exception(error).Message,unchecked((int)(0x80070000u|(uint)error)));}}
        else if(folder)Directory.Move(source,destination);else File.Move(source,destination,false);
    }
    private string ItemDirectory(NookItem item)
    {
        if (!Guid.TryParseExact(item.Id, "N", out _)) throw new IOException("入口标识无效。");
        return Path.Combine(Vault, item.Id);
    }
    private string ManifestPath(NookItem item) => Path.Combine(ItemDirectory(item), "entry.json");
    private IEnumerable<string> ManifestFiles()=>Directory.EnumerateDirectories(Vault).Where(dir=>Guid.TryParseExact(Path.GetFileName(dir),"N",out _)&&(File.GetAttributes(dir)&FileAttributes.ReparsePoint)==0).Select(dir=>Path.Combine(dir,"entry.json")).Where(File.Exists);
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void CopyDurable(string source,string destination)
    {
        // Native File.Copy preserves Windows alternate streams, including Mark-of-the-Web.
        try{File.Copy(source,destination,false);}
        catch(IOException ex) when(((ex.HResult&0xffff) is 6000 or 6007 or 50) && !File.Exists(destination))
        {
            // Some desktop filesystem providers reject copying EFS metadata from the vault.
            // Create under destination policy and preserve data streams without changing either policy.
            using(var input=File.OpenRead(source))using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){input.CopyTo(output);output.Flush(true);}
            WindowsStreams.Copy(source,destination);
        }
        var attributes=File.GetAttributes(destination);var readOnly=(attributes&FileAttributes.ReadOnly)!=0;
        if(readOnly)File.SetAttributes(destination,attributes&~FileAttributes.ReadOnly);
        try {using var target=new FileStream(destination,FileMode.Open,FileAccess.ReadWrite,FileShare.None);target.Flush(true);}
        finally {if(readOnly)File.SetAttributes(destination,attributes);}
    }
    public static void CopyPreservingStreams(string source,string destination)=>CopyDurable(source,destination);
    internal static void CopyNamedStreams(string source,string destination)=>WindowsStreams.Copy(source,destination);
    private bool IsDesktopPath(string path) => _desktops.Any(root => string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    private static bool IsShortcut(string path) => new[] { ".lnk", ".url" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private void ValidateItem(NookItem item)
    {
        _ = ItemDirectory(item);
        if (string.IsNullOrWhiteSpace(item.Name)) throw new IOException("入口名称无效。");
        if (item.Kind == "recycle-bin")
        {
            if (item.RecycleSnapshot is null || item.RecycleSnapshot.Count != 2 || !item.RecycleSnapshot.ContainsKey("NewStartPanel") || !item.RecycleSnapshot.ContainsKey("ClassicStartMenu")) throw new IOException("回收站恢复记录无效。");
        }
        else if(Mutable(item))
        {
            if(!IsDesktopPath(item.OriginalPath)||Path.GetFileName(item.FileName)!=item.FileName||item.FileName is "" or "." or ".."||Path.GetFileName(item.OriginalPath)!=item.FileName)throw new IOException("文件或文件夹记录无效。");
            _=ManagedPath(item);
        }
        else if (item.Kind != "shortcut" || !IsDesktopPath(item.OriginalPath) || Path.GetFileName(item.FileName) != item.FileName || !IsShortcut(item.FileName) || !System.Text.RegularExpressions.Regex.IsMatch(item.Hash, "^[0-9A-F]{64}$")) throw new IOException("快捷方式记录无效。");
    }
    private void Validate(NookState state)
    {
        if (state.Version is not (1 or 2) || state.Categories.Count == 0 || (state.Version==1 && state.Categories[^1].Id!="uncategorized") || (state.Categories.Any(c=>c.Id=="uncategorized") && state.Categories[^1].Id!="uncategorized") || state.Categories.Select(c => c.Id).Distinct().Count() != state.Categories.Count || state.Items.Select(i => i.Id).Distinct().Count() != state.Items.Count || state.Items.Count(i => i.Kind == "recycle-bin") > 1) throw new IOException("配置结构无效。");
        foreach (var item in state.Items) { ValidateItem(item); if (!state.Categories.Any(c => c.Id == item.CategoryId)) throw new IOException("入口所属分类不存在。"); }
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new IOException("记录为空：" + path);
    private static void WriteAtomic<T>(string path, T value, bool backup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var backupTemp = path + ".bak.tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var existed=File.Exists(path);
            // Cloning the old file retains its encryption and other filesystem attributes.
            // New files inherit the directory policy. Never decrypt a user's configuration.
            if(existed)File.Copy(path,temp,false);
            using (var file = new FileStream(temp, existed?FileMode.Truncate:FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
            if(existed && backup)
            {
                CopyDurable(path,backupTemp);
                File.Move(backupTemp,path+".bak",true);
            }
            // Same-directory rename avoids ReplaceFile's failing metadata/encryption merge.
            // The old state is never deleted before a durable replacement and backup exist.
            File.Move(temp,path,true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if(File.Exists(backupTemp))File.Delete(backupTemp); }
    }
    private void Save(NookState next) { Validate(next); SaveOrder(next);WriteAtomic(StatePath, next, true); State = next;RememberCategoryDeletion(); }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private void SaveManifest(NookItem item, bool restored) => WriteAtomic(ManifestPath(item), new Manifest { Item = item, Restored = restored });
    private void Begin(Migration migration)
    {
        if (File.Exists(JournalPath)) throw new IOException("有未完成的迁移，请重新打开程序完成恢复后再操作。");
        WriteAtomic(JournalPath, migration); _checkpoint?.Invoke("journal");
    }
    private void End() { File.Delete(JournalPath); }
    private void Load()
    {
        bool reconcile = false;
        if (File.Exists(StatePath))
        {
            try { var loaded = Read<NookState>(StatePath); Validate(loaded); State = loaded; }
            catch
            {
                // Preserve the invalid file before recovering; never quietly replace it.
                File.Copy(StatePath, StatePath + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
                try { var backup = Read<NookState>(StatePath + ".bak"); Validate(backup); State = backup; RecoveryMessages.Add("配置异常，已读取上一份有效配置并核对托管记录。"); }
                catch { State = new(); RecoveryMessages.Add("配置异常，正在从每个入口的恢复记录重建分类归属。"); }
                reconcile = true;
            }
        }
        else if (ManifestFiles().Any()) reconcile = true;
        if (reconcile) ReconcileManifests();
        if(UncategorizedRemoved()&&State.Categories.Any(c=>c.Id=="uncategorized")&&State.Categories.Count>1)
        {
            string destination=State.Categories.First(c=>c.Id!="uncategorized").Id;
            foreach(var item in State.Items.Where(i=>i.CategoryId=="uncategorized"))item.CategoryId=destination;
            State.Categories.RemoveAll(c=>c.Id=="uncategorized");State.Version=2;reconcile=true;
            RecoveryMessages.Add("已保留删除未分类的选择，收纳内容保留在现有分类。");
        }
        reconcile|=ApplyOrder(reconcile);
        RecoverPending();
        if (!File.Exists(StatePath) || reconcile) Save(State);
        else SaveOrder(State);
        RememberCategoryDeletion();
    }
    private void ReconcileManifests()
    {
        foreach (var path in ManifestFiles())
        {
            var manifest = Read<Manifest>(path); ValidateItem(manifest.Item);
            var item = manifest.Item;
            if (manifest.Restored) { State.Items.RemoveAll(i => i.Id == item.Id); continue; }
            var active = item.Kind == "recycle-bin" ? !_recycle.IsVisible : Mutable(item)?Exists(ManagedPath(item)):File.Exists(ManagedPath(item)) && (!File.Exists(item.OriginalPath) || HashFile(item.OriginalPath) != item.Hash);
            if (active && !State.Items.Any(i => i.Id == item.Id))
            {
                if (!State.Categories.Any(c => c.Id == item.CategoryId)) item.CategoryId = FallbackCategory(State);
                State.Items.Add(item);
            }
        }
    }
    private void RecoverPending()
    {
        if (!File.Exists(JournalPath)) return;
        var operation = Read<Migration>(JournalPath); var item = operation.Item; ValidateItem(item);
        var next = Clone(State);
        if (item.Kind == "recycle-bin")
        {
            if (operation.Operation == "restore") { _recycle.Restore(item.RecycleSnapshot!); next.Items.RemoveAll(i => i.Id == item.Id); SaveManifest(item, true); }
            else if (operation.Operation == "collect" && !next.Items.Any(i => i.Id == item.Id)) { _recycle.Restore(item.RecycleSnapshot!); SaveManifest(item, true); }
            else if (operation.Operation != "collect") throw new IOException("迁移类型无效。");
        }
        else if (operation.Operation == "collect")
        {
            if (Exists(item.OriginalPath)&&(!Mutable(item)||!Exists(ManagedPath(item)))) { next.Items.RemoveAll(i => i.Id == item.Id); SaveManifest(item, true); }
            else if (Exists(ManagedPath(item)) && (Mutable(item)||HashFile(ManagedPath(item)) == item.Hash))
            {
                if (!next.Items.Any(i => i.Id == item.Id)) { if (!next.Categories.Any(c => c.Id == item.CategoryId)) item.CategoryId = FallbackCategory(next); next.Items.Add(item); }
                SaveManifest(item, false);
            }
            else throw new IOException("未完成迁移的快捷方式需要人工恢复。原记录已保留：" + ItemDirectory(item));
        }
        else if (operation.Operation == "restore")
        {
            if (!IsDesktopPath(operation.Destination) || (!Mutable(item)&&!IsShortcut(operation.Destination))) throw new IOException("恢复目标无效。");
            if (Mutable(item)?Exists(operation.Destination)&&!Exists(ManagedPath(item)):File.Exists(operation.Destination)&&HashFile(operation.Destination)==item.Hash) { next.Items.RemoveAll(i => i.Id == item.Id); SaveManifest(item, true); }
            else if (!Exists(ManagedPath(item)) || (!Mutable(item)&&HashFile(ManagedPath(item)) != item.Hash)) throw new IOException("恢复来源需要检查，已保留全部记录。");
        }
        else throw new IOException("未知迁移类型。");
        Save(next); End(); RecoveryMessages.Add("已恢复上次中断的操作，快捷方式副本完整保留。");
    }
    private void CategoryExists(string id) { if (!State.Categories.Any(c => c.Id == id)) throw new ArgumentException("分类不存在。"); }
    public NookItem Collect(string path, string categoryId)
    {
        CategoryExists(categoryId); path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(path.Equals(DataDirectory,StringComparison.OrdinalIgnoreCase)||DataDirectory.StartsWith(path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||path.StartsWith(DataDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||path.Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("不能收纳正在运行的轻匣程序、数据目录或其上级目录。");
        if(Directory.Exists(path)||!IsShortcut(path))return CollectMutable(path,categoryId);
        if (!IsDesktopPath(path) || !IsShortcut(path)) throw new IOException("请选择桌面上的 .lnk 或 .url 快捷方式；程序本体、文档和文件夹不会被移动。");
        if (!File.Exists(path)) throw new FileNotFoundException("桌面入口已不存在。", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("此入口不是可直接托管的普通快捷方式文件。");
        if (State.Items.Any(i => string.Equals(i.OriginalPath, path, StringComparison.OrdinalIgnoreCase))) throw new IOException("这个桌面入口已收纳。");
        var item = new NookItem { Name = Path.GetFileNameWithoutExtension(path), OriginalPath = path, FileName = Path.GetFileName(path), CategoryId = categoryId, Hash = HashFile(path) };
        Begin(new() { Operation = "collect", Item = item });
        Directory.CreateDirectory(ItemDirectory(item));
        CopyDurable(path,ManagedPath(item));
        if (HashFile(ManagedPath(item)) != item.Hash) throw new IOException("托管文件校验失败，桌面入口未移除。");
        SaveManifest(item, false); _checkpoint?.Invoke("vault");
        var next = Clone(State); next.Items.Add(item); Save(next); _checkpoint?.Invoke("collect-state");
        if (HashFile(path) != item.Hash) throw new IOException("收纳时原入口发生变化，已保留两边文件，请重新打开进行核对。");
        File.Delete(path); _checkpoint?.Invoke("source-removed"); End(); return item;
    }
    private NookItem CollectMutable(string path,string categoryId)
    {
        if(!IsDesktopPath(path)||!Exists(path))throw new IOException("请选择桌面上实际存在的文件或文件夹。");
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("此入口是链接或云占位符，请使用实际文件／文件夹。");
        if(State.Items.Any(i=>string.Equals(i.OriginalPath,path,StringComparison.OrdinalIgnoreCase)))throw new IOException("此桌面项目已收纳。");
        var item=new NookItem{Name=Path.GetFileName(path),FileName=Path.GetFileName(path),OriginalPath=path,CategoryId=categoryId,Kind=Directory.Exists(path)?"folder":"file",PayloadLayout="content-v1"};
        Begin(new(){Operation="collect",Item=item});Directory.CreateDirectory(Path.GetDirectoryName(ManagedPath(item))!);SaveManifest(item,false);
        try{MovePayload(path,ManagedPath(item),item.Kind=="folder");}
        catch(IOException ex) when((ex.HResult&0xffff)==17)
        {
            item.PayloadPath=LocalPayload(item);var parent=Path.GetDirectoryName(item.PayloadPath)!;Directory.CreateDirectory(parent);
            var vaultRoot=Path.GetDirectoryName(parent)!;
            if((File.GetAttributes(vaultRoot)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("本地托管目录不能是符号链接。");
            File.SetAttributes(vaultRoot,File.GetAttributes(vaultRoot)|FileAttributes.Hidden);
            WriteAtomic(JournalPath,new Migration{Operation="collect",Item=item});SaveManifest(item,false);
            MovePayload(path,ManagedPath(item),item.Kind=="folder");
        }
        _checkpoint?.Invoke("mutable-moved");var next=Clone(State);next.Items.Add(item);Save(next);_checkpoint?.Invoke("collect-state");End();return item;
    }
    public NookItem CollectRecycleBin(string categoryId)
    {
        CategoryExists(categoryId);
        if (State.Items.Any(i => i.Kind == "recycle-bin")) throw new IOException("回收站已在小筑中。");
        if (!_recycle.IsVisible) throw new IOException("系统回收站当前不在桌面显示，请先在桌面图标设置中显示它。");
        var item = new NookItem { Name = "回收站", Kind = "recycle-bin", CategoryId = categoryId, RecycleSnapshot = _recycle.Capture() };
        Begin(new() { Operation = "collect", Item = item }); SaveManifest(item, false);
        _recycle.Hide(); _checkpoint?.Invoke("recycle-hidden");
        var next = Clone(State); next.Items.Add(item); Save(next); _checkpoint?.Invoke("collect-state"); End(); return item;
    }
    public string Restore(string id, bool renameConflict = false, string? desktopOverride = null)
    {
        var item = State.Items.Single(i => i.Id == id);
        if (item.Kind == "recycle-bin")
        {
            Begin(new() { Operation = "restore", Item = item });
            _recycle.Restore(item.RecycleSnapshot!); _checkpoint?.Invoke("recycle-restored");
            SaveManifest(item, true); var next = Clone(State); next.Items.RemoveAll(i => i.Id == id); Save(next); End(); return "回收站";
        }
        var root = desktopOverride ?? Path.GetDirectoryName(item.OriginalPath)!;
        if (!_desktops.Contains(Path.GetFullPath(root), StringComparer.OrdinalIgnoreCase) || !Directory.Exists(root)) throw new IOException("原桌面目录不可用，请选择可用的桌面位置。");
        var destination = Path.Combine(root, item.FileName);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            if (!renameConflict) throw new NameConflictException(destination);
            var number = 2;
            do { destination = Path.Combine(root, (item.Kind=="folder"?item.FileName:Path.GetFileNameWithoutExtension(item.FileName)) + " (" + number++ + ")" + (item.Kind=="folder"?"":Path.GetExtension(item.FileName))); } while (File.Exists(destination) || Directory.Exists(destination));
        }
        if(Mutable(item))
        {
            if(!Exists(ManagedPath(item)))throw new IOException("托管文件或文件夹不在原位置，请先检查后再恢复。");
            Begin(new(){Operation="restore",Item=item,Destination=destination});
            MovePayload(ManagedPath(item),destination,item.Kind=="folder");_checkpoint?.Invoke("destination-created");
            SaveManifest(item,true);var next=Clone(State);next.Items.RemoveAll(i=>i.Id==id);Save(next);_checkpoint?.Invoke("restore-state");End();return destination;
        }
        if (!File.Exists(ManagedPath(item)) || HashFile(ManagedPath(item)) != item.Hash) throw new IOException("托管快捷方式不存在或已改变，请检查数据目录中的副本。");
        Begin(new() { Operation = "restore", Item = item, Destination = destination });
        // overwrite:false ensures a concurrent desktop write cannot be overwritten.
        CopyDurable(ManagedPath(item),destination);
        if (HashFile(destination) != item.Hash) throw new IOException("桌面恢复校验失败，托管副本保留。");
        _checkpoint?.Invoke("destination-created");
        SaveManifest(item, true); var updated = Clone(State); updated.Items.RemoveAll(i => i.Id == id); Save(updated); _checkpoint?.Invoke("restore-state"); End();
        // Retain the vault backup so recovery never depends on a single desktop file.
        return destination;
    }
    private static string FallbackCategory(NookState state)=>state.Categories.FirstOrDefault(c=>c.Id=="uncategorized")?.Id??state.Categories[0].Id;
    public string DefaultCategoryId=>FallbackCategory(State);
    public string DeleteDestination(string id)
    {
        CategoryExists(id);var remaining=State.Categories.Where(c=>c.Id!=id).ToList();
        if(remaining.Count==0)throw new ArgumentException("至少保留一个分类，请先新建分类再删除。");
        return remaining.FirstOrDefault(c=>c.Id=="uncategorized")?.Id??remaining[0].Id;
    }
    public void AddCategory(string name) { name = CheckName(name); var next = Clone(State);var index=next.Categories.FindIndex(c=>c.Id=="uncategorized");next.Categories.Insert(index<0?next.Categories.Count:index, new() { Name = name }); Save(next); }
    public int RestoreCatalog(IEnumerable<NookItem> recovered)
    {
        var next=Clone(State);var added=new List<NookItem>();
        foreach(var source in recovered)
        {
            var item=Clone(source);ValidateItem(item);CategoryExists(item.CategoryId);
            if(next.Items.Any(i=>i.Id==item.Id || (item.Kind=="recycle-bin"?i.Kind=="recycle-bin":i.Kind!="recycle-bin"&&string.Equals(i.OriginalPath,item.OriginalPath,StringComparison.OrdinalIgnoreCase))))continue;
            if(item.Kind!="recycle-bin"&&(!Exists(ManagedPath(item))||(!Mutable(item)&&HashFile(ManagedPath(item))!=item.Hash)))throw new IOException("恢复文件校验未通过："+item.Name);
            next.Items.Add(item);added.Add(item);
        }
        Save(next);foreach(var item in added)SaveManifest(item,false);return added.Count;
    }
    private string CheckName(string name, string? except = null)
    {
        name = name.Trim(); if (name.Length is < 1 or > 20) throw new ArgumentException("分类名称需为 1–20 个字符。");
        if (State.Categories.Any(c => c.Id != except && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已有同名分类。"); return name;
    }
    public void RenameCategory(string id, string name) { if (id == "uncategorized") throw new ArgumentException("未分类不能重命名。"); name = CheckName(name, id); var next = Clone(State); next.Categories.Single(c => c.Id == id).Name = name; Save(next); }
    public void DeleteCategory(string id,string? destination=null)
    {
        string fallback=DeleteDestination(id);destination??=fallback;CategoryExists(destination);
        if(destination==id)throw new ArgumentException("接收分类不能是被删除的分类。");
        var next=Clone(State);var moved=next.Items.Where(i=>i.CategoryId==id).ToList();next.Items.RemoveAll(i=>i.CategoryId==id);next.Categories.RemoveAll(c=>c.Id==id);
        if(id=="uncategorized")next.Version=2;
        foreach(var item in moved)item.CategoryId=destination;next.Items.AddRange(moved);
        // Commit the authoritative index first. A locked recovery manifest must
        // not leave half the catalog pointing at a removed category.
        Save(next);
        foreach(var item in moved){try{SaveManifest(item,false);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){RecoveryMessages.Add("分类已删除，部分恢复清单未同步；收纳内容保留，请备份数据目录。");}}
    }
    public void MoveItem(string id, string categoryId) { CategoryExists(categoryId); var next = Clone(State); var item = next.Items.Single(i => i.Id == id); next.Items.Remove(item); item.CategoryId = categoryId; next.Items.Add(item); Save(next); SaveManifest(item, false); }
    public void RenameItem(string id, string name) { name = name.Trim(); if (name.Length is < 1 or > 40) throw new ArgumentException("名称需为 1–40 个字符。"); var next = Clone(State); var item = next.Items.Single(i => i.Id == id); item.Name = name; Save(next); SaveManifest(item, false); }
    public void ArchiveItem(string id){var item=State.Items.Single(i=>i.Id==id);if(item.Kind=="recycle-bin")throw new IOException("系统回收站请使用移回桌面。");SaveManifest(item,true);var next=Clone(State);next.Items.RemoveAll(i=>i.Id==id);Save(next);}
    public void OrderCategory(string id, int delta) { var next = Clone(State); var index = next.Categories.FindIndex(c => c.Id == id); var target = index + delta;int limit=next.Categories.Count-(next.Categories.Any(c=>c.Id=="uncategorized")?1:0);if (index < 0 || id == "uncategorized" || target < 0 || target >= limit || Math.Abs(delta) != 1) return; (next.Categories[index], next.Categories[target]) = (next.Categories[target], next.Categories[index]); Save(next); }
    public void OrderItem(string id, int delta) { var next = Clone(State); var item = next.Items.Single(i => i.Id == id); var siblings = next.Items.Where(i => i.CategoryId == item.CategoryId).ToList(); var target = siblings.IndexOf(item) + delta; if (target < 0 || target >= siblings.Count || Math.Abs(delta) != 1) return; var a = next.Items.IndexOf(item); var b = next.Items.IndexOf(siblings[target]); (next.Items[a], next.Items[b]) = (next.Items[b], next.Items[a]); Save(next); }
    public IReadOnlyList<NookItem> OrderedItems(string? categoryId=null) => categoryId is null
        ? State.Categories.SelectMany(c=>State.Items.Where(i=>i.CategoryId==c.Id)).ToList()
        : State.Items.Where(i=>i.CategoryId==categoryId).ToList();
    public void ReorderItems(string categoryId,IReadOnlyList<string> ids)
    {
        CategoryExists(categoryId);
        var siblings=State.Items.Where(i=>i.CategoryId==categoryId).ToList();
        if(ids.Count!=siblings.Count||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Count||!ids.ToHashSet(StringComparer.Ordinal).SetEquals(siblings.Select(i=>i.Id)))
            throw new ArgumentException("排序项目已变化，请取消后重新调整。");
        var next=Clone(State);var byId=next.Items.Where(i=>i.CategoryId==categoryId).ToDictionary(i=>i.Id);int position=0;
        for(int i=0;i<next.Items.Count;i++)if(next.Items[i].CategoryId==categoryId)next.Items[i]=byId[ids[position++]];
        Save(next);
    }
}
