using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FalloutModSetup;
class IntegrationChecks {
 static int count;
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);count++;Console.WriteLine("PASS "+text);}
 static void Reject(Action action,string text){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,text);}
 static string Link(string file,string name){object s=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));object l=s.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,s,new object[]{file});try{return (string)l.GetType().InvokeMember(name,BindingFlags.GetProperty,null,l,null);}finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(l);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(s);}}
 // A setup installed by 1.7.2 or earlier: old names in the game folder, on the desktop, in the Start menu and in the mod folder, with a journal that has no "kind" field.
 static IntegrationLocations Legacy(string root,string tag,out string dest,out string game,bool edit){
  var places=new IntegrationLocations{Desktop=Path.Combine(root,tag,"Desktop"),Programs=Path.Combine(root,tag,"Programs"),RequireSteamClosed=delegate{}};
  dest=Path.Combine(root,tag,"Mods");game=Path.Combine(root,tag,"Game");Directory.CreateDirectory(Path.Combine(dest,"_setup"));Directory.CreateDirectory(game);Directory.CreateDirectory(places.Desktop);
  string helper=LaunchSupport.Helper(dest),menu=Path.Combine(places.Programs,"Fallout New Vegas - Modification Framework"),lucky=Path.Combine(dest,"_setup","Lucky38.ico")+",0";
  string play=LaunchSupport.Arguments(dest,true),manage=LaunchSupport.Arguments(dest,false),un="--uninstall-ui --destination \""+dest+"\"";
  var specs=new[]{new[]{game,"Play Modded New Vegas.lnk",play},new[]{places.Desktop,"Fallout New Vegas - Modded.lnk",play},new[]{places.Desktop,"Fallout New Vegas - Mod Organizer.lnk",manage},new[]{places.Desktop,"Fallout New Vegas - Uninstall.lnk",un},new[]{menu,"Play New Vegas.lnk",play},new[]{menu,"Manage mods.lnk",manage},new[]{menu,"Uninstall Framework.lnk",un}};
  var state=new IntegrationState{destination=dest,game=game,status="installed"};
  foreach(var s in specs){string path=Path.Combine(s[0],s[1]);Integrations.Shortcut(path,helper,s[2],dest,lucky);state.shortcuts.Add(new ShortcutRecord{path=path,touched=true,existed=false,after=Engine.Hash(path),backup="shortcut-"+state.shortcuts.Count+".lnk"});}
  foreach(var s in new[]{new[]{"Play New Vegas.lnk",play},new[]{"Manage mods.lnk",manage},new[]{"Uninstall Framework.lnk",un}})Integrations.Shortcut(Path.Combine(dest,s[0]),helper,s[1],dest,lucky);
  File.WriteAllText(Path.Combine(dest,"_setup","integrations.json"),Engine.Json().Serialize(state),Engine.Utf8);
  if(edit)File.WriteAllText(Path.Combine(places.Desktop,"Fallout New Vegas - Uninstall.lnk"),"user edit");
  return places;
 }
 static void LegacyNames(string root){
  string dest,game;
  // Restore still accepts every old name.
  var places=Legacy(root,"legacy-restore",out dest,out game,false);
  Integrations.Restore(dest,false,places);
  Check(Directory.GetFiles(places.Desktop).Length==0&&!File.Exists(Path.Combine(game,"Play Modded New Vegas.lnk"))&&!Directory.Exists(Path.Combine(places.Programs,"Fallout New Vegas - Modification Framework")),"restore accepts and removes shortcuts that carry the old names and the old Start menu folder");
  // The update route renames them.
  places=Legacy(root,"legacy-update",out dest,out game,false);
  Check(Integrations.HasLegacyNames(dest),"a setup with old shortcut names is detected");
  var log=new System.Collections.Generic.List<string>();Integrations.MigrateNames(dest,game,log.Add,places);
  string menuNew=Path.Combine(places.Programs,Integrations.MenuFolder);
  Check(new[]{"Fallout New Vegas - mod framework.lnk","Mod Organizer 2.lnk","Uninstall F-NV MF.lnk"}.All(n=>File.Exists(Path.Combine(places.Desktop,n))&&File.Exists(Path.Combine(menuNew,n))&&File.Exists(Path.Combine(dest,n)))&&File.Exists(Path.Combine(game,"Fallout New Vegas - mod framework.lnk")),"old shortcuts are renamed on the desktop, in the Start menu, in the mod folder and in the game folder");
  Check(Directory.GetFiles(places.Desktop).Length==3&&Directory.GetFiles(menuNew).Length==3&&!Directory.Exists(Path.Combine(places.Programs,"Fallout New Vegas - Modification Framework"))&&!File.Exists(Path.Combine(game,"Play Modded New Vegas.lnk"))&&!File.Exists(Path.Combine(dest,"Play New Vegas.lnk"))&&!File.Exists(Path.Combine(dest,"Manage mods.lnk"))&&!File.Exists(Path.Combine(dest,"Uninstall Framework.lnk")),"nothing is left under the old names and the old Start menu folder is removed");
  Check(Link(Path.Combine(places.Desktop,"Mod Organizer 2.lnk"),"IconLocation")==Path.Combine(dest,"ModOrganizer.exe")+",0"&&Link(Path.Combine(places.Desktop,"Mod Organizer 2.lnk"),"Arguments")==LaunchSupport.Arguments(dest,false)&&Link(Path.Combine(places.Desktop,"Uninstall F-NV MF.lnk"),"IconLocation").Contains("Lucky38.ico"),"renamed shortcuts get the Mod Organizer icon for Mod Organizer 2 and keep their arguments");
  var journal=Engine.Json().Deserialize<IntegrationState>(File.ReadAllText(Path.Combine(dest,"_setup","integrations.json")));
  Check(journal.shortcuts.Count==7&&journal.shortcuts.All(c=>File.Exists(c.path)&&c.kind!=null)&&!Integrations.HasLegacyNames(dest),"the journal follows the renamed shortcuts");
  Integrations.Restore(dest,false,places);
  Check(Directory.GetFiles(places.Desktop).Length==0&&!Directory.Exists(menuNew)&&!File.Exists(Path.Combine(game,"Fallout New Vegas - mod framework.lnk")),"restore after the rename removes the renamed shortcuts");
  // A shortcut the user edited keeps its old name and content.
  places=Legacy(root,"legacy-edited",out dest,out game,true);
  log.Clear();Integrations.MigrateNames(dest,game,log.Add,places);
  Check(File.ReadAllText(Path.Combine(places.Desktop,"Fallout New Vegas - Uninstall.lnk"))=="user edit"&&!File.Exists(Path.Combine(places.Desktop,"Uninstall F-NV MF.lnk"))&&File.Exists(Path.Combine(places.Desktop,"Mod Organizer 2.lnk"))&&log.Any(x=>x.Contains("Kept your edited shortcut")),"an edited old shortcut is left alone while the others are renamed");
  // A setup moved to another PC: names and places both change.
  string moved=Path.Combine(root,"legacy-moved");places=Legacy(root,"legacy-moved-src",out dest,out game,false);
  var places2=new IntegrationLocations{Desktop=Path.Combine(moved,"Desktop"),Programs=Path.Combine(moved,"Programs"),RequireSteamClosed=delegate{}};Directory.CreateDirectory(places2.Desktop);
  Integrations.Rehome(dest,Path.Combine(moved,"Game"),log.Add,places2);
  Check(File.Exists(Path.Combine(places2.Desktop,"Mod Organizer 2.lnk"))&&File.Exists(Path.Combine(places2.Programs,Integrations.MenuFolder,"Uninstall F-NV MF.lnk"))&&File.Exists(Path.Combine(moved,"Game","Fallout New Vegas - mod framework.lnk")),"relocating a setup with old names creates the new names at the new places");
 }
 static void Invoke(Engine e,string name,params object[] args){typeof(Engine).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(e,args);}
 [STAThread] static void Main(string[] args){
  string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
  string original="// account settings\r\n\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"22380\" { \"LaunchOptions\" \"-old\" \"LastPlayed\" \"123\" } \"42\" { \"LaunchOptions\" \"keep me\" } } } } } \"friends\" { \"note\" \"unchanged\" } }";
  string option=Integrations.LaunchOption(Path.Combine(root,"Моды с пробелами"));bool exists;
  string edited=Vdf.Set(original,"22380",option,true);
  Check(Vdf.Get(edited,"22380",out exists)==option&&exists,"VDF escaped Unicode launch command");
  Check(Vdf.Set(edited,"22380","-old",true)==original,"VDF exact restoration preserves unrelated text");
  foreach(string initial in new[]{"\"UserLocalConfigStore\" {}",original.Replace("\"LaunchOptions\" \"-old\"","")}){
   string added=Vdf.Set(initial,"22490",option,true);Check(Vdf.Get(added,"22490",out exists)==option&&exists,"VDF creates missing PCR app route");
   string removed=Vdf.Set(added,"22490",null,false);Vdf.Get(removed,"22490",out exists);Check(!exists,"VDF restores absent launch option");
  }
  Reject(()=>Vdf.Set(original.Replace("\"LaunchOptions\" \"-old\"","\"LaunchOptions\" \"-old\" \"launchoptions\" \"duplicate\""),"22380",option,true),"duplicate option rejected");
  Reject(()=>Vdf.Set("\"UserLocalConfigStore\" {","22380",option,true),"malformed VDF rejected");
  var places=new IntegrationLocations{Desktop=Path.Combine(root,"Desktop"),Programs=Path.Combine(root,"Programs"),RequireSteamClosed=delegate{}};
  string game=Path.Combine(root,"Game"),dest=Path.Combine(root,"Mods"),config=Path.Combine(root,"Steam","userdata","123","config","localconfig.vdf");
  Directory.CreateDirectory(game);Directory.CreateDirectory(Path.Combine(dest,"_setup"));Directory.CreateDirectory(places.Desktop);Directory.CreateDirectory(Path.GetDirectoryName(config));File.WriteAllText(Path.Combine(game,"FalloutNV.exe"),"fixture game");File.WriteAllText(config,original);
  string desktop=Path.Combine(places.Desktop,Integrations.PlayName+".lnk");File.WriteAllText(desktop,"previous shortcut");
  var o=new Options{Game=game,Destination=dest,Store="Steam",DesktopShortcut=true,StartMenuShortcut=true,SteamPlay=false,SteamConfig=config};
  o.SteamPlay=true;Reject(()=>Integrations.Preflight(o,places),"automatic Steam editing rejected");o.SteamPlay=false;
  Integrations.Apply(o,Console.WriteLine,places);
  Check(File.ReadAllText(config)==original,"Steam configuration remains untouched");
  Check(Directory.GetFiles(places.Desktop,"*.lnk").Length==3,"three desktop shortcuts created");
  Check(new[]{"Fallout New Vegas - mod framework.lnk","Mod Organizer 2.lnk","Uninstall F-NV MF.lnk"}.All(n=>File.Exists(Path.Combine(places.Desktop,n))&&File.Exists(Path.Combine(places.Programs,"Fallout New Vegas - mod framework",n))&&File.Exists(Path.Combine(dest,n))),"play, Mod Organizer 2 and uninstall shortcuts use the new names on the desktop, in the Start menu folder and in the mod folder");
  Check(!new[]{"Play New Vegas.lnk","Manage mods.lnk","Uninstall Framework.lnk"}.Any(n=>File.Exists(Path.Combine(dest,n))),"no shortcut with an old name is created in the mod folder");
  Check(File.Exists(Path.Combine(places.Desktop,"Uninstall F-NV MF.lnk")),"desktop uninstall entry created");
  Check(File.Exists(Path.Combine(game,"Fallout New Vegas - mod framework.lnk")),"game-folder shortcut created");
  Check(Integrations.LaunchArguments=="\"moshortcut://:New Vegas\"","MO2 2.5.2 command has no empty instance argument");
  Check(File.Exists(Path.Combine(dest,"_setup","Lucky38.ico")),"Lucky 38 shortcut icon installed");
  Check(Directory.GetFiles(Path.Combine(places.Programs,Integrations.MenuFolder)).Length==3,"three Start menu shortcuts created");
  object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));object link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{desktop});
  Func<string,string> prop=name=>(string)link.GetType().InvokeMember(name,BindingFlags.GetProperty,null,link,null);
  Check(prop("Arguments")==LaunchSupport.Arguments(dest,true)&&prop("TargetPath")==LaunchSupport.Helper(dest),"shortcut uses this installation launch helper");
  Check(prop("IconLocation").Contains("Lucky38.ico"),"shortcut uses supplied icon");
  Func<string,string> icon=file=>{object s2=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));object l2=s2.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,s2,new object[]{file});try{return (string)l2.GetType().InvokeMember("IconLocation",BindingFlags.GetProperty,null,l2,null);}finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(l2);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(s2);}};
  foreach(string where in new[]{places.Desktop,Path.Combine(places.Programs,Integrations.MenuFolder),dest}){
   Check(icon(Path.Combine(where,"Mod Organizer 2.lnk"))==Path.Combine(dest,"ModOrganizer.exe")+",0","Mod Organizer 2 shortcut keeps the Mod Organizer icon ("+Path.GetFileName(where)+")");
   Check(icon(Path.Combine(where,"Uninstall F-NV MF.lnk")).Contains("Lucky38.ico")&&icon(Path.Combine(where,"Fallout New Vegas - mod framework.lnk")).Contains("Lucky38.ico"),"play and uninstall shortcuts keep the Lucky 38 icon ("+Path.GetFileName(where)+")");}
  System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
  byte[] shortcut=File.ReadAllBytes(desktop);File.WriteAllText(desktop,"later shortcut edit");Reject(()=>Integrations.Restore(dest,false,places),"restore protects later shortcut edit");File.WriteAllBytes(desktop,shortcut);
  Integrations.Restore(dest,false,places);Check(File.ReadAllText(desktop)=="previous shortcut","restore returns previous shortcut");Check(!Directory.Exists(Path.Combine(places.Programs,Integrations.MenuFolder)),"restore removes new empty Start menu folder");
  Check(File.ReadAllText(config)==original,"restore leaves Steam untouched");Check(!File.Exists(Path.Combine(game,"Fallout New Vegas - mod framework.lnk")),"restore removes added game-folder shortcut");Integrations.Restore(dest,false,places);
  LegacyNames(root);
  // Backward compatibility: restore a v1.1 Steam journal while keeping unrelated edits.
  string legacy=Path.Combine(root,"LegacyMods");Directory.CreateDirectory(Path.Combine(legacy,"_setup"));
  File.WriteAllText(config,Vdf.Set(original.Replace("\"123\"","\"456\""),"22380",option,true));
  var legacyState=new IntegrationState{destination=legacy,status="installed",steam=new SteamRecord{path=config,appId="22380",before="-old",after=option,existed=true,touched=true}};
  File.WriteAllText(Path.Combine(legacy,"_setup","integrations.json"),Engine.Json().Serialize(legacyState));
  Integrations.Restore(legacy,false,places);Check(File.ReadAllText(config)==original.Replace("\"123\"","\"456\""),"legacy Steam journal restores prior option and preserves unrelated updates");
  var engine=new Engine();string profile=Path.Combine(dest,"profiles","Default");Directory.CreateDirectory(profile);File.WriteAllText(Path.Combine(profile,"settings.ini"),"[General]\nLocalSaves=true\n");File.WriteAllText(Path.Combine(profile,"fallout.ini"),"[General]\nsLocalSavePath=Saves\\\n");File.WriteAllText(Path.Combine(profile,"falloutprefs.ini"),"[Display]\niSize W=1920\niSize H=1080\n");
  File.WriteAllText(Path.Combine(profile,"falloutcustom.ini"),"[Display]\nbFull Screen=1\n");
  foreach(bool local in new[]{true,false}){o.LocalSaves=local;Invoke(engine,"Configure",o);Check(File.ReadAllText(Path.Combine(profile,"settings.ini")).Contains("LocalSaves="+local.ToString().ToLowerInvariant()),"profile local saves "+local);Check(File.ReadAllText(Path.Combine(profile,"fallout.ini")).Contains("sLocalSavePath="+(local?"__MO_Saves\\":"Saves\\")),"save redirection "+local);}
  o.Language="Russian";Invoke(engine,"Configure",o);string configured=File.ReadAllText(Path.Combine(dest,"ModOrganizer.ini"));Check(configured.Contains("MainWindow_executablesListBox_index=0"),"MO2 selects New Vegas instead of the launcher");Check(configured.Contains("3\\title=GECK")&&configured.Contains("4\\title=FNVEdit")&&configured.Contains("4\\arguments=-cp:1251"),"both editors retained with Russian text encoding");
  string saves=Path.Combine(root,"OldSaves");Directory.CreateDirectory(saves);foreach(string n in new[]{"Save.fos","Save.nvse","Save.fos.bak","ignore.txt"})File.WriteAllText(Path.Combine(saves,n),n);o.LocalSaves=true;o.ImportSaves=saves;Invoke(engine,"Configure",o);Invoke(engine,"CopySaves",o,(Action<string>)Console.WriteLine);
  Check(Directory.GetFiles(Path.Combine(profile,"saves")).Length==3&&Directory.GetFiles(saves).Length==4,"save import copies saves and sidecars and keeps originals");
  Console.WriteLine("ALL "+count+" INTEGRATION CHECKS PASSED");
 }
}
