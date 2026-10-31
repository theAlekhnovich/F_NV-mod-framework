using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using FalloutModSetup;
class V13Checks {
 static int count;static void Check(bool ok,string text){if(!ok)throw new Exception(text);count++;Console.WriteLine("PASS "+text);}
 static void Reject(Action action,string text){bool no=false;try{action();}catch{no=true;}Check(no,text);}
 static IEnumerable<Control> All(Control p){yield return p;foreach(Control c in p.Controls)foreach(var x in All(c))yield return x;}
 [STAThread]static void Main(string[] args) {
  string root=args[0],project=args[1];Directory.CreateDirectory(root);var e=new Engine();
  Check(e.P.archives.Length==18&&Enumerable.Range(1,16).All(i=>e.P.archives[i-1].name=="Fallout Mod Setup-"+i.ToString("00")+".bin")&&e.P.archives[16].name=="Fallout Mod Setup-17.bin"&&e.P.archives[17].name=="Fallout Mod Setup-18.bin","original 16-part manifest retained plus update parts 17 and 18 (contiguous 01-18)");
  Check(Prerequisites.Packages().Any(p=>p.id=="vc11x86")&&Prerequisites.Packages().Any(p=>p.id=="vc9x86"),"GOG Galaxy and launcher runtime packages included");
  foreach(var p in Prerequisites.Packages()) {Prerequisites.Verify(p,Path.Combine(project,"add-software"));Check(true,"pinned runtime package verifies: "+p.title);}
  Func<bool,List<RuntimeStatus>> statuses=(isReady)=>new List<RuntimeStatus>{new RuntimeStatus{Id="vc14x86",Title="VC test",Ready=isReady},new RuntimeStatus{Id="net48",Ready=true}};
  int calls=0;Prerequisites.Ensure(project,true,s=>{},()=>statuses(true),(p,f)=>{calls++;return 0;});Check(calls==0,"already-installed runtimes skip installation");
  Reject(()=>Prerequisites.Ensure(project,false,s=>{},()=>statuses(false),(p,f)=>{calls++;return 0;}),"missing runtime cannot be silently skipped");
  bool ready=false;Prerequisites.Ensure(project,true,s=>{},()=>statuses(ready),(p,f)=>{calls++;ready=true;return 0;});Check(calls==1&&ready,"missing runtime installed once and rechecked");
  Reject(()=>Prerequisites.Ensure(project,true,s=>{},()=>statuses(false),(p,f)=>3010),"restart-required runtime stops before game changes");
  Reject(()=>Prerequisites.Ensure(project,true,s=>{},()=>statuses(false),(p,f)=>5),"failed runtime setup stops installation");
  Reject(()=>Prerequisites.Ensure(project,true,s=>{},()=>statuses(false),(p,f)=>0),"successful exit without installed runtime is rejected");
  Reject(()=>Prerequisites.Ensure(project,true,s=>{},()=>new List<RuntimeStatus>{new RuntimeStatus{Id="net48",Ready=false}},(p,f)=>0),"missing .NET gets explicit preparation error");
  string tampered=Path.Combine(root,"bad-runtime");Directory.CreateDirectory(tampered);File.WriteAllText(Path.Combine(tampered,"VC_redist.x86.exe"),"changed installer");Reject(()=>Prerequisites.Verify(Prerequisites.Packages().First(),tampered),"changed runtime installer rejected");
  string game=Path.Combine(root,"Game"),dest=Path.Combine(root,"Mods");Directory.CreateDirectory(game);Directory.CreateDirectory(Path.Combine(dest,"_setup"));File.WriteAllText(Path.Combine(dest,"_setup","owned-marker"),"test");
  string unicode=(string)typeof(DefenderSupport).GetMethod("PowerShell",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"'C:\\Games\\Моды'"});Check(unicode=="C:\\Games\\Моды","Defender helper preserves Unicode paths through PowerShell output");
  var targets=DefenderSupport.Targets(game,dest);Check(targets.Length==8&&targets.All(p=>Path.HasExtension(p)),"Defender scope is eight exact files, no directories or processes");
  var existing=new HashSet<string>(new[]{targets[0],targets[7]},StringComparer.OrdinalIgnoreCase);var initial=existing.ToArray();int changed=0;
  Action<string,bool> change=(p,add)=>{changed++;if(add)existing.Add(p);else existing.Remove(p);};
  DefenderSupport.ApplyCore(game,dest,()=>existing,change);Check(existing.Count==8&&changed==6,"Defender adds only missing file exclusions");
  DefenderSupport.RemoveCore(dest,()=>existing,change);Check(existing.SetEquals(initial),"uninstall preserves pre-existing Defender exclusions");int once=changed;DefenderSupport.RemoveCore(dest,()=>existing,change);Check(changed==once,"Defender cleanup is idempotent");
  int attempted=0;Reject(()=>DefenderSupport.ApplyCore(game,dest,()=>existing,(p,add)=>{if(++attempted==3)throw new Exception("simulated policy rejection");existing.Add(p);}),"partial Defender failure is recorded");
  DefenderSupport.RemoveCore(dest,()=>existing,change);Check(existing.SetEquals(initial),"partial Defender changes can be rolled back");
  string dj=Path.Combine(dest,"_setup","defender.json");var dr=Engine.Json().Deserialize<DefenderRecord>(File.ReadAllText(dj));dr.added.Add(@"C:\Windows");File.WriteAllText(dj,Engine.Json().Serialize(dr));Reject(()=>DefenderSupport.RemoveCore(dest,()=>existing,change),"tampered broad Defender exclusion rejected");File.Delete(dj);
  string profile=Path.Combine(dest,"profiles","Default");Directory.CreateDirectory(profile);File.WriteAllText(Path.Combine(profile,"settings.ini"),"[General]\nLocalSettings=false\n");File.WriteAllText(Path.Combine(profile,"falloutprefs.ini"),"[Display]\nsD3DDevice=\niSize W=1600\niSize H=900\n");File.WriteAllText(Path.Combine(profile,"fallout.ini"),"[General]\nbUseMyGamesDirectory=0\n");File.WriteAllText(Path.Combine(dest,"ModOrganizer.ini"),"[customExecutables]\n1\\title=New Vegas\n1\\binary=Wrong/FalloutNVLauncher.exe\n1\\workingDirectory=Wrong\n");
  LaunchSupport.Prepare(dest,game);
  Check(LaunchSupport.FirstRunStatus(Path.Combine(root,"never-launched.ini")).StartsWith("No usable launcher"),"first-run check handles missing Documents settings");
  Check(LaunchSupport.FirstRunStatus(Path.Combine(profile,"falloutprefs.ini")).StartsWith("Existing launcher"),"first-run check recognizes populated settings");
  Check(File.ReadAllText(Path.Combine(profile,"settings.ini")).Contains("LocalSettings=true"),"launch explicitly enables MO2 profile INIs");
  Check(File.ReadAllText(Path.Combine(profile,"falloutprefs.ini")).Contains("sD3DDevice="+Engine.GraphicsAdapterName()),"blank first-run GPU setting repaired");
  Check(File.ReadAllText(Path.Combine(profile,"falloutprefs.ini")).Contains("iSize W=1600"),"launch preparation retains chosen resolution");
  Check(File.ReadAllText(Path.Combine(dest,"ModOrganizer.ini")).Contains("FalloutNV.exe")&&!File.ReadAllText(Path.Combine(dest,"ModOrganizer.ini")).Contains("FalloutNVLauncher.exe"),"stale launcher binding repaired");
  Check(LaunchSupport.DirectArguments(game).Contains("-p Default")&&LaunchSupport.DirectArguments(game).Contains("FalloutNV.exe")&&!LaunchSupport.DirectArguments(game).Contains("moshortcut"),"game target and profile are explicit");
  Check(LaunchSupport.DirectArguments(game,true).EndsWith(" -EpicPortal")&&!LaunchSupport.DirectArguments(game).Contains("EpicPortal"),"Epic launch argument is retained only for Epic");
  string normalized=Engine.Ini("[Display]\nsD3DDevice =old\nsD3DDevice=duplicate\nOther=keep\n[General]\nsD3DDevice=outside\n","Display","sD3DDevice","GPU");
  Check(normalized.Contains("sD3DDevice=GPU")&&!normalized.Contains("duplicate")&&!normalized.Contains("old")&&normalized.Contains("Other=keep")&&normalized.Contains("sD3DDevice=outside"),"INI repair handles whitespace and duplicate values without changing other sections");
  using(var form=new SetupForm(e)) {
   Check(All(form).OfType<CheckBox>().Single(c=>c.Text.Contains("Microsoft Defender file")).Checked==true,"Defender exclusions default on in the wizard (xNVSE needs them)");
   Check(All(form).OfType<CheckBox>().Single(c=>c.Text=="Install missing required software").Checked,"required runtime installation defaults on");
   Check(All(form).Any(c=>c.Text=="Create three desktop shortcuts"),"wizard offers three desktop shortcuts");
   var pages=(System.Collections.ICollection)typeof(SetupForm).GetField("pages",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);Check(pages.Count==8,"six setup steps plus progress and completion");
  }
  string owned=Path.Combine(dest,"mods","Fixture","asset.txt");Directory.CreateDirectory(Path.GetDirectoryName(owned));File.WriteAllText(owned,"installed mod file");
  string backup=Path.Combine(dest,"_setup","backup","FalloutNV.exe");Directory.CreateDirectory(Path.GetDirectoryName(backup));File.WriteAllText(backup,"original game");File.WriteAllText(Path.Combine(game,"FalloutNV.exe"),"patched game");
  var state=new InstallState{game=game,destination=dest,status="installed",version="test"};state.changes.Add(new Change{path="FalloutNV.exe",before=Engine.Hash(backup),after=Engine.Hash(Path.Combine(game,"FalloutNV.exe")),existed=true,touched=true});Engine.SaveState(state);
  string saves=Path.Combine(profile,"saves");Directory.CreateDirectory(saves);File.WriteAllText(Path.Combine(saves,"keep.fos"),"save");UninstallSupport.Capture(e,dest);
  var captured=Engine.Json().Deserialize<OwnedFiles>(File.ReadAllText(Path.Combine(dest,"_setup","installed-files.json")));Check(captured.files.All(f=>Engine.Hash(Engine.SafeJoin(dest,f.path))==f.hash),"uninstall receipt records actual installed bytes");
  string fresh=Path.Combine(dest,"user-added.txt");File.WriteAllText(fresh,"new personal file");File.AppendAllText(Path.Combine(profile,"falloutprefs.ini"),"\n; user edit");
  string receipt=Path.Combine(dest,"_setup","installed-files.json"),receiptText=File.ReadAllText(receipt);var bad=Engine.Json().Deserialize<OwnedFiles>(receiptText);bad.files.Add(new FileItem{path="../escape",hash="x"});File.WriteAllText(receipt,Engine.Json().Serialize(bad));Reject(()=>UninstallSupport.Remove(e,dest),"tampered uninstall path rejected before restoring game");Check(File.ReadAllText(Path.Combine(game,"FalloutNV.exe"))=="patched game","rejected uninstall leaves game untouched");File.WriteAllText(receipt,receiptText);
  bad=Engine.Json().Deserialize<OwnedFiles>(receiptText);bad.files.Add(new FileItem{path="mods/../profiles/Default/saves/keep.fos",hash=Engine.Hash(Path.Combine(saves,"keep.fos"))});File.WriteAllText(receipt,Engine.Json().Serialize(bad));Reject(()=>UninstallSupport.Remove(e,dest),"path aliases cannot bypass personal-save protection");File.WriteAllText(receipt,receiptText);
  UninstallSupport.Remove(e,dest);Check(File.ReadAllText(Path.Combine(game,"FalloutNV.exe"))=="original game","uninstall restores original game");Check(!File.Exists(owned),"uninstall removes unchanged installed mods");Check(File.Exists(fresh)&&File.Exists(Path.Combine(saves,"keep.fos")),"uninstall retains saves and new personal files");Check(File.ReadAllText(Path.Combine(profile,"falloutprefs.ini")).Contains("user edit"),"uninstall retains edited configuration");UninstallSupport.Remove(e,dest);Check(true,"repeated uninstall is safe");
  Console.WriteLine("ALL "+count+" V1.3 CHECKS PASSED");
 }
}
