using DesktopNook.Core;
using System.Text.Json;

var results = new List<object>();
var runRoot = Path.Combine(Path.GetTempPath(), "DesktopNook-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action<string> run)
{
    var directory = Path.Combine(runRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try { run(directory); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
(string desktop, string data, SimulatedRecycleBin recycle) Setup(string root)
{
    var desktop = Path.Combine(root, "Desktop 中文"); Directory.CreateDirectory(desktop);
    return (desktop, Path.Combine(root, "Data"), new());
}
string FileAt(string desktop, string name = "应用 空格.lnk", string contents = "shortcut-with-arguments") { var file = Path.Combine(desktop, name); File.WriteAllText(file, contents); return file; }
Test("Roundtrip preserves shortcut bytes and persistent category", root => {
    var (desktop,data,bin) = Setup(root); var source = FileAt(desktop); var bytes = File.ReadAllBytes(source);
    var engine = new NookStore(data, [desktop], bin); var entry = engine.Collect(source, "office");
    Assert(!File.Exists(source) && File.Exists(engine.ManagedPath(entry)), "collect must remove desktop entry only after vault exists");
    engine = new(data, [desktop], bin); Assert(engine.State.Items.Single().CategoryId == "office", "persistence");
    var restored = engine.Restore(entry.Id); Assert(bytes.SequenceEqual(File.ReadAllBytes(restored)) && engine.State.Items.Count == 0, "exact roundtrip");
    Assert(File.Exists(engine.ManagedPath(entry)), "recovery backup retained");
});
Test("Conflict never overwrites, renamed restore preserves both files", root => {
    var (desktop,data,bin)=Setup(root); var source=FileAt(desktop); var engine=new NookStore(data,[desktop],bin); var item=engine.Collect(source,"office"); File.WriteAllText(source,"new-user-file");
    try { engine.Restore(item.Id); throw new Exception("expected conflict"); } catch(NameConflictException) {}
    Assert(File.ReadAllText(source)=="new-user-file" && engine.State.Items.Count==1,"conflict preserved");
    var restored=engine.Restore(item.Id,true); Assert(restored!=source && File.ReadAllText(restored)=="shortcut-with-arguments", "renamed restore");
});
Test("Desktop roots and file types restricted", root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);
    foreach(var path in new[]{FileAt(root,"outside.lnk"),FileAt(root,"outside.txt")}) { try {engine.Collect(path,"office");throw new Exception("accepted outside source");}catch(IOException){} Assert(File.Exists(path),"outside file unchanged"); }
});
Test("Category reassignment, deletion and duplicate validation", root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);engine.AddCategory("通讯");var cat=engine.State.Categories.Single(c=>c.Name=="通讯");
    var item=engine.Collect(FileAt(desktop),"office");engine.MoveItem(item.Id,cat.Id);engine.RenameItem(item.Id,"自定义名称");engine.DeleteCategory(cat.Id);
    Assert(engine.State.Items.Single().CategoryId=="uncategorized" && engine.State.Items.Single().Name=="自定义名称","delete preserves item");
    try{engine.AddCategory("办公效率");throw new Exception("duplicate accepted");}catch(ArgumentException){}
    engine.OrderCategory("dev",-1);Assert(engine.State.Categories[^1].Id=="uncategorized","system category remains last");
});
foreach(var stage in new[]{"journal","vault","collect-state","source-removed"}) Test("Interrupted collect recovery: "+stage, root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);var engine=new NookStore(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected crash");});
    try{engine.Collect(source,"office");}catch(IOException){}
    engine=new(data,[desktop],bin);
    var exists=File.Exists(source);Assert(exists?engine.State.Items.Count==0:engine.State.Items.Count==1,"one active location after recovery");
    Assert(exists || File.Exists(engine.ManagedPath(engine.State.Items.Single())),"never lose unique copy");
});
foreach(var stage in new[]{"journal","destination-created","restore-state"}) Test("Interrupted restore recovery: "+stage,root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);var engine=new NookStore(data,[desktop],bin);var item=engine.Collect(source,"office");
    engine=new(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected crash");});try{engine.Restore(item.Id);}catch(IOException){}
    engine=new(data,[desktop],bin);Assert(File.Exists(source)?engine.State.Items.Count==0:engine.State.Items.Count==1,"restored state matches desktop");Assert(File.Exists(engine.ManagedPath(item)),"backup survives all interruptions");
});
Test("Source changes during collection are not deleted",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);var engine=new NookStore(data,[desktop],bin,s=>{if(s=="collect-state")File.WriteAllText(source,"changed externally");});try{engine.Collect(source,"office");}catch(IOException){}
    engine=new(data,[desktop],bin);Assert(File.ReadAllText(source)=="changed externally"&&engine.State.Items.Count==0,"changed source preserved");
});
Test("Recycle Bin distinct state roundtrip, no file emulation",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);var entry=engine.CollectRecycleBin("dev");Assert(!bin.IsVisible&&entry.Kind=="recycle-bin","system hidden and tracked");
    engine=new(data,[desktop],bin);engine.Restore(entry.Id);Assert(bin.IsVisible&&engine.State.Items.Count==0,"system restored");Assert(!Directory.EnumerateFiles(desktop).Any(),"no fake shortcut created");
});
Test("Recycle Bin interrupted hide restores original state",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin,s=>{if(s=="recycle-hidden")throw new IOException("crash");});try{engine.CollectRecycleBin("office");}catch(IOException){}
    engine=new(data,[desktop],bin);Assert(bin.IsVisible&&engine.State.Items.Count==0,"original registry snapshot restored");
});
Test("Corrupt config rebuilt from manifests, damaged originals retained",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);var entry=engine.Collect(FileAt(desktop),"office");
    File.WriteAllText(Path.Combine(data,"state.json"),"{broken");File.WriteAllText(Path.Combine(data,"state.json.bak"),"{broken");
    engine=new(data,[desktop],bin);Assert(engine.State.Items.Any(i=>i.Id==entry.Id),"manifest recovered entry");Assert(Directory.EnumerateFiles(data,"state.json.damaged-*").Any(),"damaged config retained");engine.Restore(entry.Id);
});
Test("Corrupt backup does not resurrect restored entry",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);var item=engine.Collect(FileAt(desktop),"office");engine.Restore(item.Id);
    File.WriteAllText(Path.Combine(data,"state.json"),"broken");engine=new(data,[desktop],bin);Assert(engine.State.Items.Count==0,"manifest tombstone overrides old backup");
});
Test("Different shortcuts to same application stay independent",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);engine.Collect(FileAt(desktop,"one.lnk"),"office");engine.Collect(FileAt(desktop,"two.lnk"),"dev");Assert(engine.State.Items.Count==2,"do not merge by target");
});
if(OperatingSystem.IsWindows())Test("Windows security alternate streams preserved on roundtrip",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);const string zone="[ZoneTransfer]\r\nZoneId=3\r\n";File.WriteAllText(source+":Zone.Identifier",zone);
    var engine=new NookStore(data,[desktop],bin);var item=engine.Collect(source,"office");Assert(File.ReadAllText(engine.ManagedPath(item)+":Zone.Identifier")==zone,"vault retains security stream");
    var restored=engine.Restore(item.Id);Assert(File.ReadAllText(restored+":Zone.Identifier")==zone,"desktop retains security stream");
});
Test("Repeated category and item renames in actual LocalAppData retain attributes and backup",root=>{
    var regressionRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook-SaveRegression-"+Guid.NewGuid().ToString("N"));
    var(desktop,data,bin)=Setup(regressionRoot);var engine=new NookStore(data,[desktop],bin);var statePath=Path.Combine(data,"state.json");
    var attributes=File.GetAttributes(statePath);
    engine.RenameCategory("office","办公重命名");engine.AddCategory("临时分类");engine.RenameCategory("office","第二次办公重命名");
    var item=engine.Collect(FileAt(desktop),"office");engine.RenameItem(item.Id,"入口重命名");
    engine=new(data,[desktop],bin);Assert(engine.State.Categories.Single(c=>c.Id=="office").Name=="第二次办公重命名","category rename persisted after reopen");
    Assert(engine.State.Items.Single().Name=="入口重命名","item rename persisted after reopen");
    Assert((File.GetAttributes(statePath)&FileAttributes.Encrypted)==(attributes&FileAttributes.Encrypted),"encryption attribute retained");
    Assert(File.Exists(statePath+".bak"),"previous state backup retained");
    engine.Restore(item.Id);Console.WriteLine("LocalAppData regression: "+regressionRoot+"; attributes="+attributes);
});
Test("Removing a menu entry retains recovery copy and system item is protected",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var item=store.Collect(FileAt(desktop),"office");store.ArchiveItem(item.Id);
    Assert(store.State.Items.Count==0&&File.Exists(store.ManagedPath(item)),"archive retains complete recovery file");
    store=new(data,[desktop],bin);Assert(store.State.Items.Count==0,"removed entry does not reappear after restart");
    var system=store.CollectRecycleBin("office");try{store.ArchiveItem(system.Id);throw new Exception("system archive should be rejected");}catch(IOException){}
    Assert(store.State.Items.Count==1&&!bin.IsVisible,"system item still recoverable");store.Restore(system.Id);
});
Test("Regular documents remain editable and restore latest content",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop,"报告 中文.txt","original");var store=new NookStore(data,[desktop],bin);var item=store.Collect(source,"office");Assert(item.Kind=="file"&&!File.Exists(source),"document moved");File.WriteAllText(store.ManagedPath(item),"edited in collection");store=new(data,[desktop],bin);var restored=store.Restore(item.Id);Assert(File.ReadAllText(restored)=="edited in collection","latest changes restored");
});
Test("Nested folders including empty directories roundtrip without recursive deletion",root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"项目.v1");Directory.CreateDirectory(Path.Combine(folder,"空目录"));Directory.CreateDirectory(Path.Combine(folder,"nested"));FileAt(Path.Combine(folder,"nested"),"资料.bin","important bytes");var store=new NookStore(data,[desktop],bin);var item=store.Collect(folder,"office");Assert(item.Kind=="folder"&&!Directory.Exists(folder),"folder leaves desktop");File.WriteAllText(Path.Combine(store.ManagedPath(item),"新增.txt"),"new content");store.Restore(item.Id);Assert(Directory.Exists(Path.Combine(folder,"空目录"))&&File.ReadAllText(Path.Combine(folder,"nested","资料.bin"))=="important bytes"&&File.Exists(Path.Combine(folder,"新增.txt")),"complete edited folder restored");
});
Test("Folder same-name conflicts never overwrite either tree",root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"项目.v1");Directory.CreateDirectory(folder);FileAt(folder,"original.txt","stored");var store=new NookStore(data,[desktop],bin);var item=store.Collect(folder,"office");Directory.CreateDirectory(folder);FileAt(folder,"new.txt","desktop");try{store.Restore(item.Id);throw new Exception("expected conflict");}catch(NameConflictException){}var restored=store.Restore(item.Id,true);Assert(Path.GetFileName(restored)=="项目.v1 (2)"&&File.Exists(Path.Combine(folder,"new.txt"))&&File.Exists(Path.Combine(restored,"original.txt")),"both trees preserved");
});
foreach(var stage in new[]{"journal","mutable-moved","collect-state"})Test("Folder collect interruption recovery: "+stage,root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"目录");Directory.CreateDirectory(folder);FileAt(folder,"data.txt","bytes");var store=new NookStore(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected interruption");});try{store.Collect(folder,"office");}catch(IOException){}store=new(data,[desktop],bin);Assert(Directory.Exists(folder)?store.State.Items.Count==0:store.State.Items.Count==1,"one managed state");if(store.State.Items.Count>0)store.Restore(store.State.Items.Single().Id);Assert(File.ReadAllText(Path.Combine(folder,"data.txt"))=="bytes","contents recovered");
});
foreach(var stage in new[]{"journal","destination-created","restore-state"})Test("Folder restore interruption recovery: "+stage,root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"目录");Directory.CreateDirectory(folder);FileAt(folder,"data.txt","bytes");var store=new NookStore(data,[desktop],bin);var item=store.Collect(folder,"office");store=new(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected interruption");});try{store.Restore(item.Id);}catch(IOException){}store=new(data,[desktop],bin);if(store.State.Items.Count>0)store.Restore(item.Id);Assert(File.ReadAllText(Path.Combine(folder,"data.txt"))=="bytes"&&store.State.Items.Count==0,"restore completed safely");
});
Test("Mutable restore destination created concurrently is retained",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop,"file.txt","stored");var store=new NookStore(data,[desktop],bin);var item=store.Collect(source,"office");store=new(data,[desktop],bin,s=>{if(s=="journal")File.WriteAllText(source,"concurrent desktop content");});try{store.Restore(item.Id);}catch(IOException){}store=new(data,[desktop],bin);Assert(store.State.Items.Count==1&&File.ReadAllText(source)=="concurrent desktop content"&&File.ReadAllText(store.ManagedPath(item))=="stored","both files retained");
});
Test("Cross-volume folder payload remains on source volume and restores",root=>{
    var desktop=Path.Combine(Environment.CurrentDirectory,"test-results","cross-volume-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(desktop);var folder=Path.Combine(desktop,"目录");Directory.CreateDirectory(folder);FileAt(folder,"数据.txt","cross-volume");var data=Path.Combine(root,"Data");var store=new NookStore(data,[desktop],new SimulatedRecycleBin());var item=store.Collect(folder,"office");store=new(data,[desktop],new SimulatedRecycleBin());store.Restore(item.Id);Assert(File.ReadAllText(Path.Combine(folder,"数据.txt"))=="cross-volume","source volume restored");
});
Test("User entry.json files never collide with or become recovery manifests",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop,"entry.json","ordinary user content");var folder=Path.Combine(desktop,"project");Directory.CreateDirectory(folder);FileAt(folder,"entry.json","not a recovery manifest");var store=new NookStore(data,[desktop],bin);store.Collect(source,"office");store.Collect(folder,"office");File.WriteAllText(Path.Combine(data,"state.json"),"broken");File.WriteAllText(Path.Combine(data,"state.json.bak"),"broken");store=new(data,[desktop],bin);Assert(store.State.Items.Count==2,"only actual manifests scanned");foreach(var item in store.State.Items.ToArray())store.Restore(item.Id);Assert(File.ReadAllText(source)=="ordinary user content"&&File.ReadAllText(Path.Combine(folder,"entry.json"))=="not a recovery manifest","user JSON preserved");
});
Test("Lost collection index can be merged from intact records without moving payloads",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var first=store.Collect(FileAt(desktop,"one.lnk"),"office");var second=store.Collect(FileAt(desktop,"two.lnk"),"dev");store.State.Items.Clear();File.WriteAllText(Path.Combine(data,"state.json"),JsonSerializer.Serialize(store.State));store=new(data,[desktop],bin);Assert(store.RestoreCatalog([first,second])==2,"both records recovered");Assert(store.RestoreCatalog([first,second])==0,"recovery is idempotent");store=new(data,[desktop],bin);Assert(store.State.Items.Count==2&&store.State.Items[0].CategoryId=="office"&&File.Exists(store.ManagedPath(first)),"classification and files retained");
});
Test("Custom category order persists without changing other categories or payloads",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);
    var a=store.Collect(FileAt(desktop,"A.lnk"),"office");var x=store.Collect(FileAt(desktop,"X.lnk"),"dev");var b=store.Collect(FileAt(desktop,"B.lnk"),"office");var y=store.Collect(FileAt(desktop,"Y.lnk"),"dev");
    var hashes=store.State.Items.ToDictionary(i=>i.Id,i=>NookStore.HashFile(store.ManagedPath(i)));
    store.ReorderItems("office",new[]{b.Id,a.Id});store=new(data,[desktop],bin);
    Assert(store.OrderedItems("office").Select(i=>i.Id).SequenceEqual(new[]{b.Id,a.Id}),"persisted category order");
    Assert(store.OrderedItems("dev").Select(i=>i.Id).SequenceEqual(new[]{x.Id,y.Id}),"other category unchanged");
    Assert(store.State.Items.All(i=>NookStore.HashFile(store.ManagedPath(i))==hashes[i.Id]&&!File.Exists(i.OriginalPath)),"only metadata changes");
});
Test("All view follows category order and each category custom sequence",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);
    var d=store.Collect(FileAt(desktop,"Dev.lnk"),"dev");var a=store.Collect(FileAt(desktop,"OfficeA.lnk"),"office");var z=store.Collect(FileAt(desktop,"Design.lnk"),"design");var b=store.Collect(FileAt(desktop,"OfficeB.lnk"),"office");
    store.ReorderItems("office",new[]{b.Id,a.Id});Assert(store.OrderedItems().Select(i=>i.Id).SequenceEqual(new[]{b.Id,a.Id,z.Id,d.Id}),"grouped all view");
    store.OrderCategory("dev",-1);Assert(store.OrderedItems().Select(i=>i.Id).SequenceEqual(new[]{b.Id,a.Id,d.Id,z.Id}),"category move reflected");
    var added=store.Collect(FileAt(desktop,"OfficeNew.lnk"),"office");Assert(store.OrderedItems("office")[^1].Id==added.Id,"new item appended");
    store.MoveItem(d.Id,"office");Assert(store.OrderedItems("office")[^1].Id==d.Id,"moved item appended");
});
Test("Invalid reorder refuses duplicate missing and foreign identifiers atomically",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var a=store.Collect(FileAt(desktop,"One.lnk"),"office");var b=store.Collect(FileAt(desktop,"Two.lnk"),"office");var x=store.Collect(FileAt(desktop,"Other.lnk"),"dev");var hash=NookStore.HashFile(Path.Combine(data,"state.json"));
    foreach(var ids in new[]{new[]{a.Id,a.Id},new[]{a.Id},new[]{a.Id,x.Id},new[]{a.Id,b.Id,Guid.NewGuid().ToString("N")}}){try{store.ReorderItems("office",ids);throw new Exception("invalid order accepted");}catch(ArgumentException){}Assert(NookStore.HashFile(Path.Combine(data,"state.json"))==hash,"file unchanged");}
});
Test("Uncategorized deletion preserves contents and remains deleted after reload",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var existing=store.Collect(FileAt(desktop,"Existing.lnk"),"office");var a=store.Collect(FileAt(desktop,"A.lnk"),"uncategorized");var b=store.Collect(FileAt(desktop,"B.lnk"),"uncategorized");var hash=NookStore.HashFile(store.ManagedPath(a));
    Assert(store.DeleteDestination("uncategorized")=="office","fallback identifies existing category");store.DeleteCategory("uncategorized");store=new(data,[desktop],bin);
    Assert(store.State.Version==2&&!store.State.Categories.Any(c=>c.Id=="uncategorized"),"absence persists");Assert(store.OrderedItems("office").Select(i=>i.Id).SequenceEqual(new[]{existing.Id,a.Id,b.Id}),"moved items append in order");Assert(NookStore.HashFile(store.ManagedPath(a))==hash&&!File.Exists(a.OriginalPath),"payload never moved or deleted");
    var added=store.Collect(FileAt(desktop,"New.lnk"),store.DefaultCategoryId);Assert(added.CategoryId=="office","collect still works");store.CollectRecycleBin(store.DefaultCategoryId);Assert(store.State.Items.Single(i=>i.Kind=="recycle-bin").CategoryId=="office","recycle collect still works");
});
Test("Categories can be added reordered and removed without uncategorized",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);store.DeleteCategory("uncategorized");store.AddCategory("新分类");var cat=store.State.Categories[^1];Assert(cat.Name=="新分类","append new category");store.OrderCategory(cat.Id,-1);Assert(store.State.Categories[^2].Id==cat.Id,"last category can move");store.OrderCategory(cat.Id,1);Assert(store.State.Categories[^1].Id==cat.Id,"can move to last");var item=store.Collect(FileAt(desktop),cat.Id);store.DeleteCategory(cat.Id);store=new(data,[desktop],bin);Assert(store.State.Items.Single().CategoryId==store.DefaultCategoryId,"subsequent delete remains usable");
});
Test("Last category deletion and invalid destination never alter state",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var hash=NookStore.HashFile(Path.Combine(data,"state.json"));foreach(var target in new[]{"office","missing"}){try{store.DeleteCategory("office",target);throw new Exception("invalid target accepted");}catch(ArgumentException){}Assert(NookStore.HashFile(Path.Combine(data,"state.json"))==hash,"invalid destination unchanged");}store.DeleteCategory("office");store.DeleteCategory("design");store.DeleteCategory("dev");hash=NookStore.HashFile(Path.Combine(data,"state.json"));try{store.DeleteCategory("uncategorized");throw new Exception("last category deleted");}catch(ArgumentException){}Assert(store.State.Categories.Count==1&&NookStore.HashFile(Path.Combine(data,"state.json"))==hash,"last category protected");
});
Test("Locked manifest cannot leave a half deleted category index",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var item=store.Collect(FileAt(desktop),"uncategorized");using(var locked=File.Open(Path.Combine(data,"vault",item.Id,"entry.json"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){store.DeleteCategory("uncategorized");Assert(store.RecoveryMessages.Count>0,"manifest sync warning");}store=new(data,[desktop],bin);Assert(!store.State.Categories.Any(c=>c.Id=="uncategorized")&&store.State.Items.Single().CategoryId==store.DefaultCategoryId&&File.Exists(store.ManagedPath(item)),"authoritative state and content valid");
});
Test("Recovery remaps stale uncategorized manifests to an existing category",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var item=store.Collect(FileAt(desktop),"uncategorized");store.DeleteCategory("uncategorized");var manifestPath=Path.Combine(data,"vault",item.Id,"entry.json");var manifest=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!;manifest.Item.CategoryId="uncategorized";File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest));store.State.Items.Clear();File.WriteAllText(Path.Combine(data,"state.json.bak"),JsonSerializer.Serialize(store.State));File.WriteAllText(Path.Combine(data,"state.json"),"broken");store=new(data,[desktop],bin);Assert(store.State.Items.Single().CategoryId==store.DefaultCategoryId&&!store.State.Categories.Any(c=>c.Id=="uncategorized"),"recovered into real category without recreating default");
});
Test("Uncategorized deletion survives recovery from both damaged indexes",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var item=store.Collect(FileAt(desktop),"uncategorized");store.DeleteCategory("uncategorized");
    File.WriteAllText(Path.Combine(data,"state.json"),"broken");File.WriteAllText(Path.Combine(data,"state.json.bak"),"broken");store=new(data,[desktop],bin);
    Assert(!store.State.Categories.Any(c=>c.Id=="uncategorized")&&store.State.Items.Count==1&&store.State.Items[0].CategoryId!="uncategorized"&&File.Exists(store.ManagedPath(item)),"deletion marker prevents default resurrection during recovery");
});
Test("Independent category order survives stale and damaged indexes",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var expected=new Dictionary<string,string[]>();
    foreach(var category in new[]{"office","design","dev"}){var items=Enumerable.Range(0,4).Select(n=>store.Collect(FileAt(desktop,category+n+".lnk"),category)).ToArray();var ids=items.Reverse().Select(i=>i.Id).ToArray();store.ReorderItems(category,ids);expected[category]=ids;}
    var hashes=store.State.Items.ToDictionary(i=>i.Id,i=>NookStore.HashFile(store.ManagedPath(i)));
    var stale=JsonSerializer.Deserialize<NookState>(File.ReadAllText(Path.Combine(data,"state.json")))!;stale.Items.Reverse();File.WriteAllText(Path.Combine(data,"state.json"),JsonSerializer.Serialize(stale));
    store=new(data,[desktop],bin);foreach(var category in expected.Keys)Assert(store.OrderedItems(category).Select(i=>i.Id).SequenceEqual(expected[category]),"stale valid index must use independent category order");
    File.WriteAllText(Path.Combine(data,"state.json"),"broken");File.WriteAllText(Path.Combine(data,"state.json.bak"),"broken");
    store=new(data,[desktop],bin);foreach(var category in expected.Keys)Assert(store.OrderedItems(category).Select(i=>i.Id).SequenceEqual(expected[category]),"manifest recovery must retain each category custom order");
    Assert(store.State.Items.All(i=>NookStore.HashFile(store.ManagedPath(i))==hashes[i.Id]),"sorting never changes payloads");
});
Test("Order record follows membership and never resurrects moved or removed entries",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var a=store.Collect(FileAt(desktop,"a.lnk"),"office");var b=store.Collect(FileAt(desktop,"b.lnk"),"office");store.ReorderItems("office",new[]{b.Id,a.Id});
    var c=store.Collect(FileAt(desktop,"c.lnk"),"office");store.MoveItem(b.Id,"dev");store.MoveItem(b.Id,"office");store=new(data,[desktop],bin);
    Assert(store.OrderedItems("office").Select(i=>i.Id).SequenceEqual(new[]{a.Id,c.Id,b.Id}),"new and moved-back entries append without stale rank");
    store.ArchiveItem(c.Id);store=new(data,[desktop],bin);Assert(store.OrderedItems("office").Select(i=>i.Id).SequenceEqual(new[]{a.Id,b.Id}),"removed entry stays removed");
});
Test("Relocation preserves source, orders, preferences, empty folders and streams",root=>{
    var(desktop,data,bin)=Setup(root);var old=new NookStore(data,[desktop],bin);
    var a=old.Collect(FileAt(desktop,"a.lnk"),"office");var b=old.Collect(FileAt(desktop,"b.lnk"),"office");old.ReorderItems("office",[b.Id,a.Id]);old.DeleteCategory("uncategorized");
    var folder=Path.Combine(desktop,"folder");Directory.CreateDirectory(Path.Combine(folder,"empty"));FileAt(folder,"entry.json","user JSON");FileAt(folder,"nested.txt","edited");
    if(OperatingSystem.IsWindows())File.WriteAllText(Path.Combine(folder,"nested.txt")+":Zone.Identifier","zone");
    var item=old.Collect(folder,"dev");File.WriteAllText(Path.Combine(data,"preferences.json"),"{\"CloseEndsProcess\":false,\"CollectView\":\"details\",\"MotionMode\":\"off\"}");
    var before=VerifiedContent.Snapshot(data);var target=Path.Combine(root,"Installed","Data");
    StorageRelocation.Migrate(data,target,[desktop],bin);var next=new NookStore(target,[desktop],bin,storeAllPayloads:true);
    Assert(VerifiedContent.Equal(before,VerifiedContent.Snapshot(data)),"old directory unchanged");
    Assert(next.OrderedItems("office").Select(i=>i.Id).SequenceEqual(new[]{b.Id,a.Id}),"order retained");
    Assert(File.ReadAllText(Path.Combine(target,"preferences.json"))==File.ReadAllText(Path.Combine(data,"preferences.json")),"preferences exact");
    Assert(VerifiedContent.Equal(VerifiedContent.Snapshot(old.ManagedPath(item)),VerifiedContent.Snapshot(next.ManagedPath(next.State.Items.Single(i=>i.Id==item.Id)))) ,"payload and ADS retained");
    Assert(!next.State.Categories.Any(c=>c.Id=="uncategorized"),"deleted category retained");
});
foreach(var point in new[]{"storage-copied","storage-verified"})Test("Interrupted relocation leaves original authoritative: "+point,root=>{
    var(desktop,data,bin)=Setup(root);var old=new NookStore(data,[desktop],bin);old.Collect(FileAt(desktop),"office");var before=VerifiedContent.Snapshot(data);var target=Path.Combine(root,"Installed","Data");
    try{StorageRelocation.Migrate(data,target,[desktop],bin,p=>{if(p==point)throw new IOException("injected");});throw new Exception("did not interrupt");}catch(IOException){}
    Assert(!Directory.Exists(target)&&VerifiedContent.Equal(before,VerifiedContent.Snapshot(data)),"no incomplete publication or source change");
});
Test("Relocation refuses existing targets and source changes",root=>{
    var(desktop,data,bin)=Setup(root);var old=new NookStore(data,[desktop],bin);old.Collect(FileAt(desktop),"office");var target=Path.Combine(root,"Installed","Data");Directory.CreateDirectory(target);FileAt(target,"keep.txt","keep");
    try{StorageRelocation.Migrate(data,target,[desktop],bin);throw new Exception("overwrote target");}catch(IOException){}Assert(File.ReadAllText(Path.Combine(target,"keep.txt"))=="keep","foreign target preserved");
    target=Path.Combine(root,"Other","Data");try{StorageRelocation.Migrate(data,target,[desktop],bin,p=>{if(p=="storage-verified")FileAt(data,"new-content.txt","new");});throw new Exception("accepted source change");}catch(IOException){}
    Assert(!Directory.Exists(target)&&File.ReadAllText(Path.Combine(data,"new-content.txt"))=="new","changed source retained");
});
Test("Installation storage moves mutable files and folders across real volumes",root=>{
    var(desktop,unused,bin)=Setup(root);var data=Path.Combine(Environment.CurrentDirectory,"test-results","local-storage-"+Guid.NewGuid().ToString("N"));
    var store=new NookStore(data,[desktop],bin,storeAllPayloads:true);var folder=Path.Combine(desktop,"Folder");Directory.CreateDirectory(Path.Combine(folder,"empty"));FileAt(folder,"entry.json","payload");
    var file=FileAt(desktop,"document.txt","before");if(OperatingSystem.IsWindows()){File.WriteAllText(file+":Zone.Identifier","zone");File.WriteAllText(folder+":Folder.Info","directory stream");}
    var f=store.Collect(folder,"office");var d=store.Collect(file,"dev");Assert(store.State.Items.All(i=>i.PayloadPath==""&&store.ManagedPath(i).StartsWith(data,StringComparison.OrdinalIgnoreCase)),"all primary content inside data");
    if(OperatingSystem.IsWindows())Assert(File.ReadAllText(store.ManagedPath(d)+":Zone.Identifier")=="zone","collect preserved ADS");
    using(var stream=new FileStream(store.ManagedPath(d),FileMode.Open,FileAccess.Write)){stream.SetLength(0);stream.Write(System.Text.Encoding.UTF8.GetBytes("after"));}store.Restore(f.Id);store.Restore(d.Id);
    Assert(File.ReadAllText(file)=="after"&&File.ReadAllText(Path.Combine(folder,"entry.json"))=="payload"&&Directory.Exists(Path.Combine(folder,"empty")),"cross volume restore preserves edits and empty folders");
    if(OperatingSystem.IsWindows())Assert(File.ReadAllText(file+":Zone.Identifier")=="zone"&&File.ReadAllText(folder+":Folder.Info")=="directory stream","cross volume ADS retained");
});
foreach(var point in new[]{"cross-volume-copied","cross-volume-published"})Test("Interrupted cross-volume transfer retains complete source: "+point,root=>{
    var(desktop,unused,bin)=Setup(root);var data=Path.Combine(Environment.CurrentDirectory,"test-results","cross-safe-"+Guid.NewGuid().ToString("N"));var source=FileAt(desktop,"doc.txt","keep me");
    var store=new NookStore(data,[desktop],bin,p=>{if(p==point)throw new IOException("injected");},storeAllPayloads:true);
    try{store.Collect(source,"office");throw new Exception("did not interrupt");}catch(IOException){}
    Assert(File.ReadAllText(source)=="keep me","source preserved");store=new(data,[desktop],bin,storeAllPayloads:true);
    Assert(point=="cross-volume-copied"?store.State.Items.Count==0:store.State.Items.Count==1,"recover only a fully published payload");
});
Test("External legacy content is copied inside new data and survives backup recovery",root=>{
    var desktop=Path.Combine(Environment.CurrentDirectory,"test-results","legacy-desktop-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(desktop);var data=Path.Combine(root,"Legacy");var bin=new SimulatedRecycleBin();var old=new NookStore(data,[desktop],bin);
    var folder=Path.Combine(desktop,"Folder");Directory.CreateDirectory(folder);FileAt(folder,"actual.txt","content");var item=old.Collect(folder,"office");Assert(!string.IsNullOrEmpty(item.PayloadPath),"test exercises external storage");
    var original=old.ManagedPath(item);var target=Path.Combine(root,"Installed","Data");StorageRelocation.Migrate(data,target,[desktop],bin);
    var next=new NookStore(target,[desktop],bin,storeAllPayloads:true);Assert(next.State.Items.Single().PayloadPath==""&&Directory.Exists(original),"original retained as backup");
    File.WriteAllText(Path.Combine(target,"state.json"),"damaged");File.WriteAllText(Path.Combine(target,"state.json.bak"),"damaged");next=new(target,[desktop],bin,storeAllPayloads:true);
    Assert(next.State.Items.Count==1&&next.ManagedPath(next.State.Items[0]).StartsWith(target,StringComparison.OrdinalIgnoreCase),"manifest recovery never falls back to old external file");
});
Test("Installation data cannot collect itself or copy recursively into itself",root=>{
    var desktop=Path.Combine(root,"Desktop");Directory.CreateDirectory(desktop);var parent=Path.Combine(desktop,"Launcher");var data=Path.Combine(parent,"Data");var store=new NookStore(data,[desktop],new SimulatedRecycleBin(),storeAllPayloads:true);var before=VerifiedContent.Snapshot(data);
    try{store.Collect(parent,"office");throw new Exception("collected data parent");}catch(IOException){}
    try{VerifiedContent.Copy(data,Path.Combine(data,"nested"));throw new Exception("recursive copy accepted");}catch(IOException){}
    Assert(VerifiedContent.Equal(before,VerifiedContent.Snapshot(data))&&!Directory.Exists(Path.Combine(data,"nested")),"self operations rejected before mutation");
});

Test("Notes category moves and permanent deletes persist without archive", root=>{
    var store=new NotesStore(root);var draft=store.Draft();var a=NotesStore.AddProject(draft,"项目A");var b=NotesStore.AddProject(draft,"项目B");
    var first=new ProjectNote{ProjectId=a.Id,Kind="todo",Title="自定义清单",Tasks=[new(){Text="待办",Done=true},new(){Text="待删细项"}]};
    draft.Notes.Add(first);store.Commit(draft);
    first.Tasks.RemoveAt(1);store.Commit(draft);
    Assert(new NotesStore(root).State.Notes.Single().Tasks.Count==1,"task permanently removed");
    NotesStore.RenameProject(draft,a,"改名项目");Assert(first.ProjectId==a.Id,"rename uses stable IDs");
    NotesStore.DeleteProject(draft,a,b.Id);store.Commit(draft);var loaded=new NotesStore(root).State;
    Assert(!loaded.Projects.Any(p=>p.Id==a.Id)&&loaded.Notes.Single().ProjectId==b.Id&&loaded.Notes.Single().Tasks[0].Done,"move keeps content and deletes category");
    NotesStore.DeleteProject(draft,b);store.Commit(draft);loaded=new NotesStore(root).State;
    Assert(loaded.Projects.Count==0&&loaded.Notes.Count==0,"category and records removed instead of archived");
    var disk=File.ReadAllText(store.FilePath);Assert(!disk.Contains(first.Id)&&!disk.Contains(a.Id),"deleted content absent in current snapshot");
});
Test("Notes format one migrates into grouped categories and preserves legacy archive",root=>{
    var old=new NotesState{Format=1};var p=NotesStore.AddProject(old,"旧分类");
    old.Notes.Add(new(){ProjectId=p.Id,Kind="todo",Title="原清单",Tasks=[new(){Text="旧任务",Done=true}]});
    var trash=NotesStore.AddProject(old,"原回收分类");trash.DeletedUtc=DateTime.UtcNow;
    old.Notes.Add(new(){ProjectId=trash.Id,Title="旧删除内容",DeletedUtc=DateTime.UtcNow});
    var dir=Path.Combine(root,"Notes");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"notes.json"),JsonSerializer.Serialize(old));
    var store=new NotesStore(root);var draft=store.Draft();
    Assert(draft.Format==2&&draft.Projects.Last().Archived&&draft.Notes.Last().Archived,"legacy trash preserved as archive");
    Assert(draft.Projects.All(p=>p.DeletedUtc is null)&&draft.Notes.All(n=>n.DeletedUtc is null),"no live trash markers remain");
    store.Commit(draft);using var disk=JsonDocument.Parse(File.ReadAllText(store.FilePath));
    Assert(disk.RootElement.GetProperty("Categories").GetArrayLength()==2&&!disk.RootElement.TryGetProperty("Notes",out _),"disk groups records by category");
    var loaded=new NotesStore(root).State;Assert(loaded.Notes.First().Tasks.Single().Done&&loaded.Notes.First().Id==old.Notes.First().Id,"migration preserves IDs and completion");
    var moved=NotesStore.AddProject(draft,"迁移目标");draft.Notes.First().ProjectId=moved.Id;store.Commit(draft);
    loaded=new NotesStore(root).State;Assert(loaded.Notes.First(n=>n.Id==old.Notes.First().Id).ProjectId==moved.Id,"category grouping follows moves");
});
Test("Markdown exports names, categories, text and standard task checkboxes",root=>{
    var state=new NotesState();var p=NotesStore.AddProject(state,"工作分类");var other=NotesStore.AddProject(state,"其他分类");
    var n=new ProjectNote{ProjectId=p.Id,Kind="todo",Title="自定义清单",Tasks=[new(){Text="完成任务",Done=true},new(){Text="后续任务"}]};state.Notes.Add(n);
    state.Notes.Add(new(){ProjectId=other.Id,Title="正文",Text="多行文字\n继续记录"});
    var all=NotesMarkdown.Export(state);Assert(all.Contains("## 工作分类")&&all.Contains("## 其他分类")&&all.Contains("- [x] 完成任务")&&all.Contains("- [ ] 后续任务")&&all.Contains("多行文字"),"all data exported as markdown");
    var category=NotesMarkdown.Export(state,project:p);Assert(!category.Contains("其他分类"),"category export is scoped");
    var single=NotesMarkdown.Export(state,n);Assert(single.StartsWith("# 自定义清单")&&!single.Contains("工作分类"),"single record uses custom title");
});
Test("Partial task strike ranges persist independently and export in display order", root =>
{
    var store = new NotesStore(root);
    var draft = store.Draft();
    var category = NotesStore.AddProject(draft, "项目 A");
    var completed = new ProjectNote { ProjectId = category.Id, Title = "完成清单", Kind = "todo", Tasks = [new() { Text = "已验收", Done = true }] };
    var pending = new ProjectNote
    {
        ProjectId = category.Id, Title = "处理中", Kind = "todo", Tasks =
        [
            new() { Text = "已提交", Done = true },
            new() { Text = "确认设计方案", Strikes = [new(2, 2)] }
        ]
    };
    draft.Notes.AddRange([completed, pending]);
    store.Commit(draft);
    var reopened = new NotesStore(root);
    var saved = reopened.State.Notes.Last();
    Assert(completed.IsCompletedChecklist() && !saved.IsCompletedChecklist() &&
        saved.TasksInDisplayOrder().Select(task => task.Done).SequenceEqual([false, true]), "completion groups are derived without changing saved order");
    Assert(saved.Tasks.Last().Strikes?.SequenceEqual([new NoteStrikeRange(2, 2)]) == true, "partial formatting survives reload");
    var export = NotesMarkdown.Export(reopened.State);
    Assert(export.Contains("- [ ] 确认~~设计~~方案") && export.IndexOf("处理中", StringComparison.Ordinal) < export.IndexOf("完成清单", StringComparison.Ordinal) &&
        export.IndexOf("确认~~设计~~方案", StringComparison.Ordinal) < export.IndexOf("已提交", StringComparison.Ordinal),
        "Markdown preserves exact struck text and pending-first order");

    var invalid = reopened.Draft();
    invalid.Notes.Last().Tasks.Last().Strikes![0] = new(2, 100);
    Assert(saved.Tasks.Last().Strikes![0] == new NoteStrikeRange(2, 2), "draft formatting is isolated");
    try { reopened.Commit(invalid); throw new Exception("invalid formatting accepted"); }
    catch (IOException) { }
    Assert(new NotesStore(root).State.Notes.Last().Tasks.Last().Strikes![0].Length == 2, "invalid range leaves saved text intact");
});
Test("Notes duplicate names, dangling references and stale revisions are rejected",root=>{
    var store=new NotesStore(root);var draft=store.Draft();var a=NotesStore.AddProject(draft," 项目A ");store.Commit(draft);var stale=store.Draft();
    try{NotesStore.AddProject(draft,"项目A");throw new Exception("duplicate accepted");}catch(ArgumentException){}
    draft.Projects[0].Archived=true;store.Commit(draft);
    try{store.Commit(stale);throw new Exception("stale accepted");}catch(IOException){}
    var before=File.ReadAllBytes(store.FilePath);draft.Notes.Add(new(){ProjectId=Guid.NewGuid().ToString("N")});
    try{store.Commit(draft);throw new Exception("orphan accepted");}catch(IOException){}
    Assert(before.SequenceEqual(File.ReadAllBytes(store.FilePath)),"rejected state never changes disk");
});
Test("Notes preserve the previous display order on upgrade and store manual order by category", root =>
{
    var store = new NotesStore(root);
    var draft = store.Draft();
    var firstCategory = NotesStore.AddProject(draft, "分类 A");
    var otherCategory = NotesStore.AddProject(draft, "分类 B");
    var first = new ProjectNote { ProjectId = firstCategory.Id, Title = "最早" };
    var favorite = new ProjectNote { ProjectId = firstCategory.Id, Title = "常用", Favorite = true, CreatedUtc = first.CreatedUtc.AddSeconds(1) };
    var other = new ProjectNote { ProjectId = otherCategory.Id, Title = "其他" };
    draft.Notes.AddRange([first, favorite, other]);
    store.Commit(draft);
    var legacyDisk = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
    legacyDisk.Remove("RecordsInDisplayOrder");
    File.WriteAllText(store.FilePath, legacyDisk.ToJsonString());

    store = new NotesStore(root);
    draft = store.Draft();
    var categoryNotes = draft.Notes.Where(note => note.ProjectId == firstCategory.Id).ToList();
    Assert(categoryNotes.Select(note => note.Id).SequenceEqual(new[] { favorite.Id, first.Id }), "legacy seed respects the original visible order");
    NotesStore.ReorderNote(draft, categoryNotes[1], categoryNotes[0], after: false);
    store.Commit(draft);
    var reloaded = new NotesStore(root).State;
    Assert(reloaded.Notes.Select(note => note.Id).SequenceEqual(new[] { first.Id, favorite.Id, other.Id }), "manual order wins and other category retains its records");
    Assert(NotesMarkdown.Export(reloaded, project: firstCategory).IndexOf("### 最早", StringComparison.Ordinal) <
        NotesMarkdown.Export(reloaded, project: firstCategory).IndexOf("### 常用", StringComparison.Ordinal), "Markdown follows the saved sequence");
});
Test("Changing note kind keeps text, duplicates and matching completion states", root =>
{
    var note = new ProjectNote
    {
        Kind = "todo",
        Tasks = [new() { Text = "相同", Done = true }, new() { Text = "相同" }, new() { Text = "第三项", Done = true }]
    };
    note.ChangeKind("text");
    Assert(note.Kind == "text" && note.Text == "相同\n相同\n第三项", "plain text has one line per original task");
    note.Text = "第三项\r\n相同\r\n\r\n相同\r\n新增";
    note.ChangeKind("todo");
    Assert(note.Tasks.Select(task => task.Text).SequenceEqual(new[] { "第三项", "相同", "", "相同", "新增" }) &&
        note.Tasks.Select(task => task.Done).SequenceEqual(new[] { true, true, false, false, false }), "edited text becomes tasks and retained lines keep checkmarks");
});
Test("Notes recover valid backup and preserve damaged bytes without rotating corruption",root=>{
    var store=new NotesStore(root);var draft=store.Draft();var project=NotesStore.AddProject(draft,"测试项目");draft.Notes.Add(new(){ProjectId=project.Id,Text="应恢复正文"});store.Commit(draft);
    File.WriteAllText(store.FilePath,"corrupt bytes");store=new(root);Assert(store.State.Notes.Single().Text=="应恢复正文"&&store.RecoveryMessage is not null,"valid backup recovered");
    Assert(Directory.GetFiles(store.DirectoryPath,"*.damaged-*").Any(),"broken original preserved");
    draft=store.Draft();draft.Notes[0].Text="恢复后继续";store.Commit(draft);
    Assert(new NotesStore(root).State.Notes.Single().Text=="恢复后继续","recovery can safely commit");
    using(var backup=JsonDocument.Parse(File.ReadAllText(store.FilePath+".bak"))) Assert(backup.RootElement.GetProperty("Categories")[0].GetProperty("Records").GetArrayLength()==1,"backup stays valid");
    File.WriteAllText(store.FilePath,"bad");File.WriteAllText(store.FilePath+".bak","bad backup");store=new(root);
    draft=store.Draft();NotesStore.AddProject(draft,"不可覆盖");try{store.Commit(draft);throw new Exception("overwritten");}catch(IOException){}
    Assert(File.ReadAllText(store.FilePath)=="bad"&&File.ReadAllText(store.FilePath+".bak")=="bad backup","double corruption blocks all writes");
});
Test("Notes mutable draft records do not alter committed snapshots", root =>
{
    var store = new NotesStore(root);
    var draft = store.Draft();
    var project = NotesStore.AddProject(draft, "测试分类");
    draft.Notes.Add(new ProjectNote
    {
        ProjectId = project.Id, Kind = "todo", Title = "原清单",
        Tasks = [new NoteTask { Text = "第一项" }]
    });
    store.Commit(draft);
    draft.Projects[0].Search = "未保存";
    draft.Notes[0].Tasks[0].Done = true;
    Assert(store.State.Projects[0].Search == "" && !store.State.Notes[0].Tasks[0].Done,
        "editing the previous draft leaves committed state unchanged");
    var later = store.Draft();
    later.Notes[0].Tasks[0].Text = "未保存文字";
    later.Notes[0].Tasks.Add(new NoteTask { Text = "第二项" });
    Assert(store.Draft().Notes[0].Tasks.Count == 1 && store.State.Notes[0].Tasks[0].Text == "第一项",
        "both mutable task records and lists are isolated");
});
Test("Notes failed writes leave committed state and draft available for retry",root=>{
    var store=new NotesStore(root);var draft=store.Draft();var p=NotesStore.AddProject(draft,"项目");draft.Notes.Add(new(){ProjectId=p.Id,Text="first"});store.Commit(draft);
    var before=File.ReadAllBytes(store.FilePath);draft.Notes[0].Text="unsaved latest";
    using(var locked=new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.None)){
        try{store.Commit(draft);throw new Exception("write not blocked");}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        Assert(store.State.Notes[0].Text=="first"&&draft.Notes[0].Text=="unsaved latest","both states retained");
    }
    Assert(before.SequenceEqual(File.ReadAllBytes(store.FilePath)),"failed publication preserves main file");store.Commit(draft);
    Assert(new NotesStore(root).State.Notes[0].Text=="unsaved latest","retry succeeds");
});
var failed = results.Count(r => JsonSerializer.Serialize(r).Contains("\"passed\":false"));
var output = args.FirstOrDefault() ?? Path.Combine(runRoot,"results.json");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output,JsonSerializer.Serialize(new{passed=results.Count-failed,failed,runRoot,checks=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failed} passed, {failed} failed. {output}");Environment.ExitCode=failed==0?0:1;
