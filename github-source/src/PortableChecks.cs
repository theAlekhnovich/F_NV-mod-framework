using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CSharp;
using System.CodeDom.Compiler;
using FalloutModSetup;
class PortableChecks {
 static int count;static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL "+text);count++;Console.WriteLine("PASS "+text);}
 static void Reject(Action action,string text){bool no=false;try{action();}catch{no=true;}Check(no,text);}
 static void Touch(string p,string text=""){Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllText(p,text);}
 static void FakeGame(string game,bool patched) {
  var b=new byte[512];b[0]=77;b[1]=90;b[60]=0x80;b[0x80]=0x50;b[0x81]=0x45;if(patched)b[0x80+22]=0x20;
  if(patched)Encoding.ASCII.GetBytes("nvse_steam_loader.dll").CopyTo(b,300);
  Directory.CreateDirectory(game);File.WriteAllBytes(Path.Combine(game,"FalloutNV.exe"),b);Touch(Path.Combine(game,"FalloutNVLauncher.exe"),"launcher");
 }
 static string Link(string lnk,string prop){
  object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));object l=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{lnk});
  return (string)l.GetType().InvokeMember(prop,BindingFlags.GetProperty,null,l,null);
 }
 static string Ini(string game,string dest,string download) {
  string g=game.Replace('\\','/'),d=dest.Replace('\\','/');
  return "[General]\r\ngameName=New Vegas\r\ngamePath="+Engine.GamePathValue(game)+"\r\n[Settings]\r\nbase_directory="+Engine.Q(d)+"\r\nmod_directory="+Engine.Q(d+"/mods")+"\r\nprofiles_directory="+Engine.Q(d+"/profiles")+"\r\ndownload_directory="+Engine.Q(download)+"\r\n[customExecutables]\r\nsize=5\r\n"
   +"1\\title=New Vegas\r\n1\\binary="+Engine.Q(g+"/FalloutNV.exe")+"\r\n1\\workingDirectory="+Engine.Q(g)+"\r\n2\\title=Fallout Launcher\r\n2\\binary="+Engine.Q(g+"/FalloutNVLauncher.exe")+"\r\n2\\workingDirectory="+Engine.Q(g)
   +"\r\n3\\title=GECK\r\n3\\binary="+Engine.Q(g+"/GECK.exe")+"\r\n4\\title=FNVEdit\r\n4\\binary="+Engine.Q(d+"/tools/FNVEdit/FNVEdit.exe")+"\r\n4\\workingDirectory="+Engine.Q(g)
   +"\r\n5\\title=My tool\r\n5\\binary="+Engine.Q(d+"/tools/mine.exe")+"\r\n";
 }
 [STAThread]static void Main(string[] args) {
  string root=Path.GetFullPath(args[0]);if(Directory.Exists(root))Directory.Delete(root,true);Directory.CreateDirectory(root);
  // --- Game and store detection with substitute sources ---
  string steam=Path.Combine(root,"SteamLib"),steamGame=Path.Combine(steam,"steamapps","common","Fallout New Vegas custom");FakeGame(steamGame,false);
  Touch(Path.Combine(steam,"steamapps","appmanifest_22380.acf"),"\"AppState\"\r\n{\r\n\t\"installdir\"\t\t\"Fallout New Vegas custom\"\r\n}\r\n");
  string gog=Path.Combine(root,"GOG Games","Fallout New Vegas");FakeGame(gog,false);Touch(Path.Combine(gog,"goggame-1454587428.info"),"{}");
  string epic=Path.Combine(root,"Epic","FalloutNV");FakeGame(epic,false);Directory.CreateDirectory(Path.Combine(epic,".egstore"));
  string manifests=Path.Combine(root,"Manifests");Touch(Path.Combine(manifests,"a.item"),"{\"DisplayName\":\"Fallout: New Vegas Ultimate Edition\",\"InstallLocation\":\""+epic.Replace("\\","\\\\")+"\"}");
  Touch(Path.Combine(manifests,"b.item"),"{\"DisplayName\":\"Other game\",\"InstallLocation\":\""+Path.Combine(root,"Other").Replace("\\","\\\\")+"\"}");Touch(Path.Combine(manifests,"broken.item"),"{not json");
  string scanned=Path.Combine(root,"Games","Fallout - New Vegas");FakeGame(scanned,false);
  var sources=new GameSources{SteamLibraries=()=>new[]{steam,Path.Combine(root,"missing")},Gog=()=>new[]{gog,gog},Epic=()=>GameLocator.EpicManifestFolders(manifests),Bethesda=()=>new[]{steamGame},DriveRoots=()=>new[]{root}};
  var found=GameLocator.Find(sources);
  Check(found.Count==4,"finder returns four distinct copies without duplicates");
  Check(found.Single(c=>c.Path==steamGame).Store=="Steam"&&found.Single(c=>c.Path==gog).Store=="GOG"&&found.Single(c=>c.Path==epic).Store=="Epic","Steam manifest, GOG and Epic manifest copies get the right store");
  Check(found.Single(c=>c.Path==scanned).Store==null&&GameLocator.StoreFromFolder(scanned)==null,"unmarked folder found by common-folder scan has no store guess");
  Check(GameLocator.StoreFromFolder(gog)=="GOG"&&GameLocator.StoreFromFolder(epic)=="Epic"&&GameLocator.StoreFromFolder(steamGame)=="Steam","store is read from folder markers");
  Check(GameLocator.Find(new GameSources{SteamLibraries=()=>new string[0],Gog=()=>new string[0],Epic=()=>new string[0],Bethesda=()=>new string[0],DriveRoots=()=>new string[0]}).Count==0,"empty machine finds nothing");
  Check(GameLocator.Find(new GameSources{SteamLibraries=()=>{throw new Exception("boom");},Gog=()=>{throw new Exception("boom");},Epic=()=>{throw new Exception("boom");},Bethesda=()=>{throw new Exception("boom");},DriveRoots=()=>{throw new Exception("boom");}}).Count==0,"a failing source never stops detection");
  string vdf=Path.Combine(root,"vdfroot");Touch(Path.Combine(vdf,"steamapps","libraryfolders.vdf"),"\"libraryfolders\"\r\n{\r\n\t\"0\"\r\n\t{\r\n\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\r\n\t}\r\n\t\"1\"\t\t\"F:\\\\OldStyle\"\r\n}\r\n");
  var libs=GameLocator.SteamLibraryFolders(new[]{vdf}).ToList();Check(libs.Contains(@"E:\SteamLibrary")&&libs.Contains(@"F:\OldStyle")&&libs.Contains(Path.GetFullPath(vdf)),"Steam library folders are read from both vdf formats");
  Check(GameLocator.LooksLikeGameName("Fallout New Vegas enplczru")&&GameLocator.LooksLikeGameName("FalloutNewVegas")&&GameLocator.LooksLikeGameName("Fallout - New Vegas")&&!GameLocator.LooksLikeGameName("Fallout 4"),"game folder names are recognised conservatively");

  // --- MO2 settings rewrite ---
  string oldD=@"E:\Old Mods\Мод",newD=@"D:\FNV Mods",oldG=@"E:\Games\FNV",newG=@"D:\Games\FNV";
  string rewritten=PortableSupport.RewriteModOrganizerIni(Ini(oldG,oldD,"X:/elsewhere/downloads"),oldD,oldG,newD,newG);
  Check(!rewritten.Contains("Old Mods")&&!rewritten.Contains("E:/Games"),"old folders vanish from the rewritten MO2 settings");
  Check(rewritten.Contains("mod_directory=\"D:/FNV Mods/mods\"")&&rewritten.Contains("gamePath="+Engine.GamePathValue(newG)),"MO2 directories and gamePath follow the move");
  Check(rewritten.Contains("download_directory=\"X:/elsewhere/downloads\""),"a download folder the user put elsewhere is preserved");
  Check(rewritten.Contains("1\\binary=\"D:/Games/FNV/FalloutNV.exe\"")&&rewritten.Contains("4\\binary=\"D:/FNV Mods/tools/FNVEdit/FNVEdit.exe\"")&&rewritten.Contains("5\\binary=\"D:/FNV Mods/tools/mine.exe\""),"installer and user executables are repointed");
  Check(PortableSupport.RewriteModOrganizerIni(rewritten,oldD,oldG,newD,newG)==rewritten,"rewriting twice changes nothing");
  Check(PortableSupport.RewriteModOrganizerIni(Ini(@"E:\SteamLibrary2\g",@"E:\Mods2",".."),@"E:\SteamLibrary",@"E:\Mods",newD,newG).Contains("E:/Mods2"),"a folder that merely starts with the old name is not touched");

  // --- Full move of a finished setup ---
  var places=new IntegrationLocations{Desktop=Path.Combine(root,"Desktop"),Programs=Path.Combine(root,"Programs")};Directory.CreateDirectory(places.Desktop);Directory.CreateDirectory(places.Programs);
  string game=Path.Combine(root,"Game"),dest=Path.Combine(root,"Mods"),moved=Path.Combine(root,"Elsewhere","Moved Mods");FakeGame(game,true);
  Directory.CreateDirectory(Path.Combine(dest,"_setup"));File.WriteAllText(Path.Combine(dest,"_setup","owned-marker"),"x");Directory.CreateDirectory(Path.Combine(dest,"profiles","Default","saves"));File.WriteAllText(Path.Combine(dest,"profiles","Default","saves","s.fos"),"save");
  File.WriteAllText(Path.Combine(dest,"ModOrganizer.ini"),Ini(game,dest,dest+"/downloads"));
  var state=new InstallState{status="installed",game=game,destination=dest,version="t"};state.changes.Add(new Change{path="FalloutNV.exe",existed=true,touched=true,before="a",after=Engine.Hash(Path.Combine(game,"FalloutNV.exe"))});Engine.SaveState(state);
  string preexisting=Path.Combine(places.Desktop,Integrations.PlayName+".lnk");Integrations.Shortcut(preexisting,@"C:\Windows\notepad.exe","","C:\\",@"C:\Windows\notepad.exe,0");
  var o=new Options{Game=game,Destination=dest,Store="GOG",DesktopShortcut=true,StartMenuShortcut=true,LocalSaves=true};Integrations.Apply(o,s=>{},places);
  File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(dest,"_setup","Setup and Restore.exe"));
  var engine=new Engine();UninstallSupport.Capture(engine,dest);
  Check(File.Exists(Path.Combine(dest,PortableSupport.RepairCommand))&&File.ReadAllText(Path.Combine(dest,PortableSupport.RepairCommand)).Contains("%~dp0")&&!File.ReadAllText(Path.Combine(dest,PortableSupport.RepairCommand)).Contains(dest),"installation writes a repair command with only relative paths");
  Check(PortableSupport.InstalledFolderOf(Path.Combine(dest,"_setup","Setup and Restore.exe"))==dest&&PortableSupport.InstalledFolderOf(Path.Combine(root,"x.exe"))==null,"helper finds the mod folder it belongs to");
  Check(!PortableSupport.Inspect(dest).NeedsWork,"an untouched setup needs no relocation");
  Directory.CreateDirectory(Path.GetDirectoryName(moved));Directory.Move(dest,moved);
  var plan=PortableSupport.Inspect(moved);Check(plan.DestinationMoved&&!plan.GameMissing,"a moved mod folder is detected");
  Reject(()=>LaunchSupport.ReadState(moved),"launching a moved setup is refused until repaired");
  Reject(()=>PortableSupport.Relocate(moved,Path.Combine(root,"Nowhere"),null,places),"a missing game folder is rejected");
  string fresh=Path.Combine(root,"FreshGame");FakeGame(fresh,false);Reject(()=>PortableSupport.Relocate(moved,fresh,null,places),"an unpatched game folder is rejected");
  string imposter=Path.Combine(root,"Imposter");FakeGame(imposter,true);File.AppendAllText(Path.Combine(imposter,"FalloutNV.exe"),"different");Reject(()=>PortableSupport.Relocate(moved,imposter,null,places),"a patched exe other than the installed one is rejected");
  string copy=Path.Combine(root,"Copy of Mods");Directory.CreateDirectory(copy);
  var log=new List<string>();PortableSupport.Relocate(moved,null,log.Add,places);
  var after=Engine.Json().Deserialize<InstallState>(File.ReadAllText(Path.Combine(moved,"_setup","state.json")));
  Check(after.destination==moved&&after.game==game&&!PortableSupport.Inspect(moved).NeedsWork,"state record points at the new folder");
  string mo=File.ReadAllText(Path.Combine(moved,"ModOrganizer.ini"));Check(!mo.Contains(root.Replace('\\','/')+"/Mods/")&&mo.Contains(moved.Replace('\\','/')+"/mods"),"MO2 settings were rewritten on disk");
  Check(File.Exists(Path.Combine(moved,"profiles","Default","saves","s.fos")),"saves are untouched");
  var helper=Path.Combine(moved,"_setup","Setup and Restore.exe");
  foreach(var name in new[]{Integrations.PlayName+".lnk",Integrations.ManageName+".lnk",Integrations.UninstallName+".lnk"})Check(Link(Path.Combine(moved,name),"Arguments").Contains(moved.Replace('\\','/'))&&Link(Path.Combine(moved,name),"TargetPath")==helper,"in-folder shortcut repointed: "+name);
  foreach(var name in new[]{Integrations.PlayName+".lnk",Integrations.ManageName+".lnk",Integrations.UninstallName+".lnk"})Check(Link(Path.Combine(places.Desktop,name),"TargetPath")==helper&&Link(Path.Combine(places.Desktop,name),"Arguments").Contains(moved.Replace('\\','/')),"desktop shortcut repointed: "+name);
  Check(Link(Path.Combine(places.Programs,Integrations.MenuFolder,Integrations.PlayName+".lnk"),"TargetPath")==helper,"Start menu shortcut repointed");
  var journal=Engine.Json().Deserialize<IntegrationState>(File.ReadAllText(Path.Combine(moved,"_setup","integrations.json")));
  Check(journal.destination==moved&&journal.shortcuts.Where(c=>c.touched).All(c=>Engine.Hash(c.path)==c.after),"shortcut journal hashes match the rewritten shortcuts");
  var receipt=Engine.Json().Deserialize<OwnedFiles>(File.ReadAllText(Path.Combine(moved,"_setup","installed-files.json")));
  Check(receipt.destination==moved&&receipt.files.Where(f=>File.Exists(Path.Combine(moved,f.path))).All(f=>Engine.Hash(Path.Combine(moved,f.path))==f.hash),"uninstall receipt follows the move and matches installed bytes");
  Check(Directory.Exists(Path.Combine(moved,"_setup","relocate-backup")),"previous settings are kept in a backup folder");
  Check(File.Exists(Path.Combine(game,"FalloutNV.exe"))&&Engine.Hash(Path.Combine(game,"FalloutNV.exe"))==state.changes[0].after,"game files were not modified");
  PortableSupport.Relocate(moved,null,null,places);Check(true,"relocation can be repeated safely");
  Integrations.Restore(moved,false,places);
  Check(!File.Exists(Path.Combine(places.Desktop,Integrations.ManageName+".lnk"))&&!File.Exists(Path.Combine(game,Integrations.PlayName+".lnk")),"restore still removes shortcuts after a move");
  Check(Link(preexisting,"TargetPath").ToLowerInvariant().EndsWith("notepad.exe"),"a shortcut that existed before installation is restored after the move");

  // --- Copy is refused while the original exists, game folder move is followed ---
  string root2=Path.Combine(root,"Second");Directory.CreateDirectory(root2);
  string game2=Path.Combine(root2,"Game"),dest2=Path.Combine(root2,"Mods");FakeGame(game2,true);Directory.CreateDirectory(Path.Combine(dest2,"_setup"));File.WriteAllText(Path.Combine(dest2,"_setup","owned-marker"),"x");File.WriteAllText(Path.Combine(dest2,"ModOrganizer.ini"),Ini(game2,dest2,dest2+"/downloads"));
  var st2=new InstallState{status="installed",game=game2,destination=dest2,version="t"};st2.changes.Add(new Change{path="FalloutNV.exe",existed=true,touched=true,before="a",after=Engine.Hash(Path.Combine(game2,"FalloutNV.exe"))});Engine.SaveState(st2);
  var places2=new IntegrationLocations{Desktop=Path.Combine(root2,"Desktop"),Programs=Path.Combine(root2,"Programs")};Directory.CreateDirectory(places2.Desktop);Directory.CreateDirectory(places2.Programs);
  Integrations.Apply(new Options{Game=game2,Destination=dest2,Store="Steam",DesktopShortcut=true},s=>{},places2);File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(dest2,"_setup","Setup and Restore.exe"));UninstallSupport.Capture(engine,dest2);
  string cloned=Path.Combine(root2,"Clone");Directory.CreateDirectory(cloned);foreach(var f in Directory.GetFiles(dest2,"*",SearchOption.AllDirectories)){string t=Path.Combine(cloned,f.Substring(dest2.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(t));File.Copy(f,t);}
  Reject(()=>PortableSupport.Relocate(cloned,null,null,places2),"a copied setup is refused while the original still exists");
  string game2b=Path.Combine(root2,"Game on another drive");Directory.Move(game2,game2b);
  var p2=PortableSupport.Inspect(dest2);Check(p2.GameMissing&&!p2.DestinationMoved,"a moved game folder is detected");
  Check(PortableSupport.MatchingGames(st2,new GameSources{SteamLibraries=()=>new string[0],Gog=()=>new string[0],Epic=()=>new string[0],Bethesda=()=>new string[0],DriveRoots=()=>new[]{root2}}).Count==0,"finder only offers game folders that belong to the setup");
  PortableSupport.Relocate(dest2,game2b,null,places2);
  Check(Engine.Json().Deserialize<InstallState>(File.ReadAllText(Path.Combine(dest2,"_setup","state.json"))).game==game2b&&File.ReadAllText(Path.Combine(dest2,"ModOrganizer.ini")).Contains(game2b.Replace('\\','/')),"a moved game folder is followed");
  Check(Link(Path.Combine(game2b,Integrations.PlayName+".lnk"),"TargetPath")==Path.Combine(dest2,"_setup","Setup and Restore.exe"),"the game-folder shortcut that moved with the game is repointed");
  Check(!LaunchSupport.ReadState(dest2).game.Equals(game2),"launching works again after the game folder move");

  // --- Attach a setup copied from another PC to a fresh, unpatched game folder ---
  string work=Path.Combine(root,"Attach");Directory.CreateDirectory(work);Fixture(Path.Combine(work,"fixture"),args[1]);
  var fe=new Engine(Pkg,Path.Combine(work,"PackageFolder"));
  string A=Path.Combine(work,"A"),aDest=MakeSetup(A,true,"English"),aGame=FreshGame(Path.Combine(A,"PC2","Game"),false,null);var aPlaces=PlacesFor(A);
  string aLocal=Path.Combine(aDest,"mods","Local game files","DeadMoney.esm");File.WriteAllBytes(aLocal,Bytes(77,4096));File.SetLastWriteTimeUtc(aLocal,new DateTime(2000,1,5,0,0,0,DateTimeKind.Utc));
  string aSnap=Snap(aDest),aGameSnap=Snap(aGame);
  Func<string,string> tryAttach=g=>Msg(()=>PortableSupport.AttachGame(fe,aDest,g,null,aPlaces));
  string m1=tryAttach(Path.Combine(work,"nothing"));Check(m1!=null,"attach rejects a folder that is not a game folder");
  string oldExe=FreshGame(Path.Combine(work,"OldExe"),false,null);File.WriteAllText(Path.Combine(oldExe,"FalloutNV.exe"),"not a 1.4.0.525 executable");
  Check((tryAttach(oldExe)??"").Contains("1.4.0.525"),"attach rejects an executable that is not 1.4.0.525");
  string badMaster=FreshGame(Path.Combine(work,"BadMaster"),false,null);File.WriteAllBytes(Path.Combine(badMaster,"Data","DeadMoney.esm"),Bytes(99,4096));
  Check((tryAttach(badMaster)??"").Contains("Unsupported"),"attach rejects master files that match neither supported release");
  string noDlc=FreshGame(Path.Combine(work,"NoDlc"),false,null);File.Delete(Path.Combine(noDlc,"Data","Fallout - Textures.bsa"));
  Check((tryAttach(noDlc)??"").Contains("Missing game/DLC archive"),"attach rejects a game folder without its DLC archives");
  Check((tryAttach(Path.Combine(aDest,"Inside"))??"").Length>0&&(tryAttach(aDest)??"").Length>0,"attach rejects a game folder inside the mod folder");
  string ownedGame=Path.Combine(work,"OwnedGame");FakeGame(ownedGame,true);
  Check((tryAttach(ownedGame)??"").Contains("already"),"attach refuses the game folder the setup already owns");
  string toolPath=Path.Combine(aDest,"_setup","tools","FNVpatch.exe"),toolBytes=Convert.ToBase64String(File.ReadAllBytes(toolPath));
  File.AppendAllText(toolPath,"x");
  Check((tryAttach(aGame)??"").Contains("damaged"),"attach refuses a copy whose own patch tools are damaged");
  File.WriteAllBytes(toolPath,Convert.FromBase64String(toolBytes));
  string realVersion=Pkg.version;Pkg.version="other";Check((tryAttach(aGame)??"").Contains("package version"),"attach refuses a setup from another package version");Pkg.version=realVersion;
  string ruGame=FreshGame(Path.Combine(work,"RuGame"),true,null);string langMsg=tryAttach(ruGame)??"";
  Check(langMsg.Contains("English")&&langMsg.Contains("Russian"),"an English setup is not attached to a Russian game");
  Check(Snap(aDest)==aSnap&&Snap(aGame)==aGameSnap,"every rejected attach left the mod folder and the game untouched");

  string aOrigExe=Engine.Hash(Path.Combine(aGame,"FalloutNV.exe")),aFixed=Path.Combine(aDest,"mods","Fixed","FalloutNV.esm");DateTime fixedTime=File.GetLastWriteTimeUtc(aFixed);
  var aLog=new List<string>();PortableSupport.AttachGame(fe,aDest,aGame,aLog.Add,aPlaces);
  Check(Engine.HasLoaderPatch(Path.Combine(aGame,"FalloutNV.exe")),"the game executable got the 4GB flag and the xNVSE loader patch");
  Check(Support.Where(x=>x.Key.StartsWith("game/")).All(x=>H(File.ReadAllBytes(Path.Combine(aGame,x.Key.Substring(5).Replace('/','\\'))))==H(x.Value)),"xNVSE files and the optional game file were copied verified");
  var aState=Engine.Json().Deserialize<InstallState>(File.ReadAllText(Path.Combine(aDest,"_setup","state.json")));
  Check(aState.status=="installed"&&aState.game==aGame&&aState.destination==aDest&&aState.changes.All(c=>c.touched),"state record is rebuilt for the new game and mod folder");
  Check(aState.changes.Single(c=>c.path=="FalloutNV.exe").before==aOrigExe&&Engine.Hash(Path.Combine(aDest,"_setup","backup","FalloutNV.exe"))==aOrigExe,"the new game's original executable is backed up and recorded");
  Check(aState.changes.Where(c=>c.path.StartsWith("nvse_")).All(c=>!c.existed),"files that did not exist before are recorded as added");
  string kept=Directory.GetDirectories(Path.Combine(aDest,"_setup","attach-backup")).Single();
  Check(File.ReadAllText(Path.Combine(kept,"backup","FalloutNV.exe"))=="old-pc-original"&&Engine.Json().Deserialize<InstallState>(File.ReadAllText(Path.Combine(kept,"state.json"))).game.Contains("PC1"),"the other PC's backups and records are kept in attach-backup");
  string aIni=File.ReadAllText(Path.Combine(aDest,"ModOrganizer.ini"));
  Check(!aIni.Contains("PC1")&&aIni.Contains("gamePath="+Engine.GamePathValue(aGame))&&aIni.Contains("5\\binary=\""+aDest.Replace('\\','/')+"/tools/mine.exe\"")&&aIni.Contains("game_edition=Steam"),"MO2 settings follow the new folders and the detected store");
  string aHelper=Path.Combine(aDest,"_setup","Setup and Restore.exe");
  Check(Link(Path.Combine(aDest,Integrations.PlayName+".lnk"),"Arguments").Contains(aDest.Replace('\\','/'))&&Link(Path.Combine(A,"PC2","Desktop",Integrations.PlayName+".lnk"),"TargetPath")==aHelper&&Link(Path.Combine(A,"PC2","Programs",Integrations.MenuFolder,Integrations.PlayName+".lnk"),"TargetPath")==aHelper,"in-folder, desktop and Start menu shortcuts point at this PC");
  Check(Link(Path.Combine(aGame,Integrations.PlayName+".lnk"),"TargetPath")==aHelper,"the game-folder shortcut is created in the new game folder");
  Check(aLog.Any(l=>l.Contains("Fixed ESM verified"))&&!aLog.Any(l=>l.StartsWith("Rebuilt"))&&File.GetLastWriteTimeUtc(aFixed)==fixedTime,"an intact Fixed ESM is verified by hash and kept");
  Check(Engine.Hash(aLocal)==H(MDM)&&File.GetLastWriteTimeUtc(aLocal)==new DateTime(2000,1,5,0,0,0,DateTimeKind.Utc),"a plugin copy that differs from this game is refreshed with its timestamp kept");
  var aDef=Engine.Json().Deserialize<DefenderRecord>(File.ReadAllText(Path.Combine(aDest,"_setup","defender.json")));
  Check(!DefenderSupport.HasExclusions(aDest)&&aDef.status=="removed"&&aDef.destination==aDest&&aDef.game==aGame,"the other PC's Defender record is closed so uninstall cannot trip over it");
  var aReceipt=Engine.Json().Deserialize<OwnedFiles>(File.ReadAllText(Path.Combine(aDest,"_setup","installed-files.json")));
  Check(aReceipt.destination==aDest&&aReceipt.files.Where(f=>File.Exists(Path.Combine(aDest,f.path))).All(f=>Engine.Hash(Path.Combine(aDest,f.path))==f.hash)&&aReceipt.files.Any(f=>f.path=="_setup/backup/FalloutNV.exe"),"uninstall receipt matches the installed bytes and owns the new backups");
  Check(!PortableSupport.Inspect(aDest).NeedsWork&&LaunchSupport.ReadState(aDest).game==aGame&&File.Exists(Path.Combine(aDest,"profiles","Default","saves","s.fos")),"the setup launches from the new location and keeps its saves");
  Check((tryAttach(aGame)??"").Contains("already"),"attaching twice to the same game is refused");
  PortableSupport.Relocate(aDest,null,null,aPlaces);Check(PortableSupport.OwnsGame(PortableSupport.Inspect(aDest).State,aGame),"a normal repair works on an attached setup");

  // Damaged derived files and a Russian game: rebuilt with xdelta, not trusted.
  string B=Path.Combine(work,"B"),bDest=MakeSetup(B,false,"Russian");
  File.WriteAllBytes(Path.Combine(bDest,"mods","Fixed","FalloutNV.esm"),Bytes(8,100));File.WriteAllBytes(Path.Combine(bDest,"mods","Russian text compatibility","DeadMoney.esm"),Bytes(9,100));File.Delete(Path.Combine(bDest,"mods","Russian text compatibility","Fonts","f.fnt"));
  string bEn=FreshGame(Path.Combine(B,"PC2","English game"),false,null),bRu=FreshGame(Path.Combine(B,"PC2","Russian game"),true,null);
  string bMsg=Msg(()=>PortableSupport.AttachGame(fe,bDest,bEn,null,PlacesFor(B)))??"";Check(bMsg.Contains("Russian")&&bMsg.Contains("English"),"a Russian setup is not attached to an English game");
  var bLog=new List<string>();PortableSupport.AttachGame(fe,bDest,bRu,bLog.Add,PlacesFor(B));
  Check(Engine.Hash(Path.Combine(bDest,"mods","Fixed","FalloutNV.esm"))==H(FixedNV)&&bLog.Any(l=>l.StartsWith("Rebuilt and verified Data/FalloutNV.esm")),"a damaged Fixed ESM is rebuilt from the new game's master");
  Check(Engine.Hash(Path.Combine(bDest,"mods","Russian text compatibility","DeadMoney.esm"))==H(RuText)&&Engine.Hash(Path.Combine(bDest,"mods","Russian text compatibility","Fonts","f.fnt"))==H(Font),"a damaged Russian overlay is rebuilt and a missing font file is recopied");
  Check(Engine.Hash(Path.Combine(bDest,"mods","Local game files","DeadMoney.esm"))==H(MDMru),"an identical plugin copy is not rewritten");

  // A commit that fails halfway is rolled back, and the same attach works once the cause is gone.
  string C=Path.Combine(work,"C"),cDest=MakeSetup(C,false,"English"),cGame=FreshGame(Path.Combine(C,"PC2","Game"),false,"GOG"),cLoader=Path.Combine(cGame,"nvse_loader.exe");
  File.WriteAllText(cLoader,"user loader");File.SetAttributes(cLoader,FileAttributes.ReadOnly);
  string cGameSnap=Snap(cGame),cState=File.ReadAllText(Path.Combine(cDest,"_setup","state.json")),cBackups=Snap(Path.Combine(cDest,"_setup","backup")),cIni=File.ReadAllText(Path.Combine(cDest,"ModOrganizer.ini"));
  var cLog=new List<string>();Reject(()=>PortableSupport.AttachGame(fe,cDest,cGame,cLog.Add,PlacesFor(C)),"an attach that fails while copying game files is rejected");
  Check(cLog.Any(l=>l.Contains("FAILED"))&&cLog.Any(l=>l.Contains("were restored"))&&Snap(cGame)==cGameSnap&&!Engine.HasLoaderPatch(Path.Combine(cGame,"FalloutNV.exe")),"the game folder is exactly as it was after the failed attach");
  Check(File.ReadAllText(Path.Combine(cDest,"_setup","state.json"))==cState&&Snap(Path.Combine(cDest,"_setup","backup"))==cBackups&&File.ReadAllText(Path.Combine(cDest,"ModOrganizer.ini"))==cIni,"the setup record, old backups and settings are restored after the failure");
  File.SetAttributes(cLoader,FileAttributes.Normal);PortableSupport.AttachGame(fe,cDest,cGame,null,PlacesFor(C));
  string cText=File.ReadAllText(Path.Combine(cDest,"ModOrganizer.ini"));
  Check(Engine.HasLoaderPatch(Path.Combine(cGame,"FalloutNV.exe"))&&cText.Contains("game_edition=GOG")&&cText.Contains("1\\arguments=\r\n")&&cText.Contains("1\\steamAppID=\r\n"),"retrying succeeds and the GOG store is recorded");
  fe.Restore(cDest);
  Check(Snap(cGame)==cGameSnap,"Restore after an attach puts every game file back, including a file the user already had");
  Check(Engine.Json().Deserialize<InstallState>(File.ReadAllText(Path.Combine(cDest,"_setup","state.json"))).status=="restored","the restored setup is marked restored");

  // The original setup must not be copied while it still exists; messages point at --attach-game.
  string E=Path.Combine(work,"E"),eDest=MakeSetup(E,false,"English",true),eGame=FreshGame(Path.Combine(E,"PC2","Game"),false,null);
  Check((Msg(()=>PortableSupport.AttachGame(fe,eDest,eGame,null,PlacesFor(E)))??"").Contains("still exists"),"a copied setup is not attached while the original exists");
  string D=Path.Combine(work,"D"),dDest=MakeSetup(D,false,"English"),dFresh=FreshGame(Path.Combine(D,"PC2","Game"),false,null);
  Check((Msg(()=>PortableSupport.Run(dDest,"",true,false,null,null,false))??"").Contains("--attach-game"),"quiet repair of a setup without its game explains --attach-game");
  Check((Msg(()=>PortableSupport.Relocate(dDest,dFresh,null,PlacesFor(D)))??"").Contains("--attach-game"),"relocating onto an unpatched game explains --attach-game");
  // Store settings
  string s0=Ini(@"D:\G",@"D:\M","x"),epicText=PortableSupport.RewriteStoreSettings(s0,"Epic","");
  Check(epicText.Contains("game_edition=Epic Games")&&epicText.Contains("1\\arguments=-EpicPortal")&&epicText.Contains("2\\arguments=-EpicPortal")&&!epicText.Contains("3\\arguments"),"Epic gets its launch argument on the game and launcher entries only");
  string custom=epicText.Replace("1\\arguments=-EpicPortal","1\\arguments=-custom"),steamText=PortableSupport.RewriteStoreSettings(custom,"Steam","22490");
  Check(steamText.Contains("1\\arguments=-custom")&&steamText.Contains("2\\arguments=\r\n")&&steamText.Contains("1\\steamAppID=22490")&&!steamText.Contains("-EpicPortal"),"switching to Steam drops the Epic argument and keeps one the user wrote");
  Check(PortableSupport.RewriteStoreSettings(steamText,"Steam","22490")==steamText,"store settings are stable when rewritten");
  if(args.Length>2&&Directory.Exists(args[2])){var realOptions=new Options{Game=args[2],Destination=Path.Combine(work,"RealDest"),Store="Auto"};new Engine().CheckGame(realOptions);Check(true,"the real game folder passes the same game checks as an installation ("+realOptions.Language+", "+realOptions.Store+")");}
  // --- Defender trouble is explained and never leaves a record that blocks anything ---
  string clixml="#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T></TN><MS><I64 N=\"SourceId\">1</I64><PR N=\"Record\"><AV>Preparing modules for first use.</AV></PR></MS></Obj><S S=\"Error\">Get-MpPreference : Operation failed with the following error: 0x800106ba_x000D__x000A_</S><S S=\"Error\">At line:1 char:130_x000D__x000A_</S></Objs>";
  Check(DefenderSupport.CleanError(clixml)=="Get-MpPreference : Operation failed with the following error: 0x800106ba"&&DefenderSupport.CleanError("plain text ")=="plain text"&&DefenderSupport.CleanError(null)=="","PowerShell's XML error output is reduced to its first real line");
  string explained=DefenderSupport.Explain(clixml);
  Check(explained.Contains("not running")&&explained.Contains("0x800106ba")&&!explained.Contains("CLIXML")&&!explained.Contains("<"),"a stopped Defender service is explained in plain words");
  string dfDest=Path.Combine(root,"DefenderFail","Mods"),dfGame=Path.Combine(root,"DefenderFail","Game");Directory.CreateDirectory(Path.Combine(dfDest,"_setup"));File.WriteAllText(Path.Combine(dfDest,"_setup","owned-marker"),"t");FakeGame(dfGame,false);
  Reject(()=>DefenderSupport.ApplyCore(dfGame,dfDest,()=>{throw new Exception("0x800106ba");},(p,add)=>{}),"Defender that cannot even be read fails the step");
  Check(DefenderSupport.Abandon(dfDest,dfGame)&&!DefenderSupport.HasExclusions(dfDest),"nothing was added, so the failed attempt is closed without a Windows prompt");
  var dfSet=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int dfCalls=0;
  Reject(()=>DefenderSupport.ApplyCore(dfGame,dfDest,()=>dfSet,(p,add)=>{if(++dfCalls==3)throw new Exception("refused");dfSet.Add(p);}),"Defender that refuses halfway fails the step");
  Check(!DefenderSupport.Abandon(dfDest,dfGame)&&dfSet.Count==2,"exclusions already added are reported so they can be taken back");
  DefenderSupport.RemoveCore(dfDest,()=>dfSet,(p,add)=>{if(add)dfSet.Add(p);else dfSet.Remove(p);});
  Check(dfSet.Count==0&&DefenderSupport.Abandon(dfDest,dfGame),"the partial exclusions are removed and the record is closed");
  Console.WriteLine(count+" checks");
 }

 // --- Fixture for the attach checks: a tiny package, tools and game folders ---
 static string Xdelta,Stub,Patcher;static Package Pkg;static byte[] MNV,MDM,MDMru,RuText,FixedNV,Font;static Dictionary<string,byte[]> Support;
 static byte[] Bytes(int seed,int n){var r=new Random(seed);var b=new byte[n];r.NextBytes(b);return b;}
 static string H(byte[] b){using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
 static string Msg(Action a){try{a();}catch(Exception e){return e.Message;}return null;}
 static void Build(string exe,string code,string version) {
  using(var p=new CSharpCodeProvider()) {
   var r=p.CompileAssemblyFromSource(new CompilerParameters{GenerateExecutable=true,OutputAssembly=exe,CompilerOptions="/platform:x86 /nologo"},"using System;using System.IO;\r\n[assembly: System.Reflection.AssemblyFileVersion(\""+version+"\")]\r\n"+code);
   if(r.Errors.HasErrors)throw new Exception(r.Errors[0].ToString());
  }
 }
 static byte[] Delta(string dir,byte[] from,byte[] to) {
  string a=Path.Combine(dir,"a.bin"),b=Path.Combine(dir,"b.bin"),d=Path.Combine(dir,"d.vcdiff");File.WriteAllBytes(a,from);File.WriteAllBytes(b,to);if(File.Exists(d))File.Delete(d);
  Engine.Run(Xdelta,"-f -e -s a.bin b.bin d.vcdiff",dir,60,false);return File.ReadAllBytes(d);
 }
 static void Fixture(string dir,string xdelta) {
  Directory.CreateDirectory(dir);Xdelta=Path.GetFullPath(xdelta);
  Stub=Path.Combine(dir,"stub.exe");Build(Stub,"class G{static void Main(){}}","1.4.0.525");
  Patcher=Path.Combine(dir,"FNVpatch.exe");
  Build(Patcher,"class P{static int Main(){var b=File.ReadAllBytes(\"FalloutNV.exe\");int pe=BitConverter.ToInt32(b,60);b[pe+22]|=0x20;var t=System.Text.Encoding.ASCII.GetBytes(\"nvse_steam_loader.dll\");var o=new byte[b.Length+t.Length];b.CopyTo(o,0);t.CopyTo(o,b.Length);File.WriteAllBytes(\"FalloutNV.exe\",o);Console.WriteLine(\"patched!\");return 0;}}","1.0.0.0");
  MNV=Bytes(1,4096);MDM=Bytes(2,4096);MDMru=Bytes(3,4096);RuText=Bytes(4,5000);FixedNV=Bytes(5,4200);Font=Bytes(6,300);
  Support=new Dictionary<string,byte[]>();
  Support["game/nvse_1_4.dll"]=Bytes(10,500);Support["game/nvse_loader.exe"]=Bytes(11,500);Support["game/nvse_steam_loader.dll"]=Bytes(12,500);Support["game/Data/Fallout - Invalidation.bsa"]=Bytes(13,300);
  Support["tools/FNVpatch.exe"]=File.ReadAllBytes(Patcher);Support["tools/xdelta3decode.exe"]=File.ReadAllBytes(Xdelta);
  Support["patches/Data/FalloutNV.esm.vcdiff"]=Delta(dir,MNV,FixedNV);Support["patches/ru/DeadMoney.esm.vcdiff"]=Delta(dir,MDMru,RuText);
  var ru=new LanguageItem{id="Russian",masters=new[]{new MasterItem{name="Data/FalloutNV.esm",hash=H(MNV)},new MasterItem{name="Data/DeadMoney.esm",hash=H(MDMru)}},fontFiles=new[]{new FileItem{path="Data/Fonts/f.fnt",size=Font.Length,hash=H(Font)}},sourceTranslations=new FileItem[0],
   patches=new[]{new LanguagePatch{sourceKind="game",source="Data/DeadMoney.esm",sourceHash=H(MDMru),target="mods/Russian text compatibility/DeadMoney.esm",targetHash=H(RuText),patch="patches/ru/DeadMoney.esm.vcdiff",size=RuText.Length}}};
  Pkg=new Package{version="t",profile="Default",languages=new[]{ru},optionalGameFiles=new[]{"Data/Fallout - Invalidation.bsa"},required=new[]{"Data/Fallout - Textures.bsa"},archives=new ArchiveItem[0],
   masters=new[]{new MasterItem{name="Data/FalloutNV.esm",hash=H(MNV)},new MasterItem{name="Data/DeadMoney.esm",hash=H(MDM)}},
   patches=new[]{new PatchItem{source="Data/FalloutNV.esm",sourceHash=H(MNV),target="app/mods/Fixed/FalloutNV.esm",targetHash=H(FixedNV),patch="patches/Data/FalloutNV.esm.vcdiff",size=FixedNV.Length}},
   files=Support.Select(x=>new FileItem{path=x.Key,size=x.Value.Length,hash=H(x.Value)}).ToArray()};
 }
 static string FreshGame(string dir,bool russian,string store) {
  Directory.CreateDirectory(Path.Combine(dir,"Data"));File.Copy(Stub,Path.Combine(dir,"FalloutNV.exe"),true);Touch(Path.Combine(dir,"FalloutNVLauncher.exe"),"launcher");
  File.WriteAllBytes(Path.Combine(dir,"Data","FalloutNV.esm"),MNV);File.WriteAllBytes(Path.Combine(dir,"Data","DeadMoney.esm"),russian?MDMru:MDM);File.WriteAllBytes(Path.Combine(dir,"Data","Fallout - Textures.bsa"),Bytes(20,100));
  if(russian){Directory.CreateDirectory(Path.Combine(dir,"Data","Fonts"));File.WriteAllBytes(Path.Combine(dir,"Data","Fonts","f.fnt"),Font);}
  if(store=="GOG")Touch(Path.Combine(dir,"goggame-1.info"),"{}");
  return dir;
 }
 static IntegrationLocations PlacesFor(string x){var p=new IntegrationLocations{Desktop=Path.Combine(x,"PC2","Desktop"),Programs=Path.Combine(x,"PC2","Programs")};Directory.CreateDirectory(p.Desktop);Directory.CreateDirectory(p.Programs);return p;}
 static string Snap(string dir){return String.Join("|",Directory.GetFiles(dir,"*",SearchOption.AllDirectories).OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).Select(f=>f.Substring(dir.Length)+":"+Engine.Hash(f)).ToArray());}
 static void CopyTree(string from,string to){foreach(var f in Directory.GetFiles(from,"*",SearchOption.AllDirectories)){string t=Path.Combine(to,f.Substring(from.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(t));File.Copy(f,t);}}
 // Builds a finished setup on "another PC" (PC1) and copies it to PC2. The original disappears unless keepOriginal.
 static string MakeSetup(string x,bool shortcuts,string language,bool keepOriginal=false) {
  string pc1=Path.Combine(x,"PC1"),game1=Path.Combine(pc1,"Game"),dest=Path.Combine(pc1,"Mods"),setup=Path.Combine(dest,"_setup");FakeGame(game1,true);
  Directory.CreateDirectory(setup);File.WriteAllText(Path.Combine(setup,"owned-marker"),"t");
  foreach(var kv in Support){string p=Path.Combine(setup,kv.Key.Replace('/','\\'));Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllBytes(p,kv.Value);}
  bool ru=language=="Russian";
  Action<string,byte[]> put=delegate(string rel,byte[] b){string p=Path.Combine(dest,rel);Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllBytes(p,b);};
  put("mods\\Fixed\\FalloutNV.esm",FixedNV);put("mods\\Local game files\\FalloutNV.esm",MNV);put("mods\\Local game files\\DeadMoney.esm",ru?MDMru:MDM);
  foreach(var f in Directory.GetFiles(Path.Combine(dest,"mods","Local game files")))File.SetLastWriteTimeUtc(f,new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc));
  if(ru){put("mods\\Russian text compatibility\\DeadMoney.esm",RuText);put("mods\\Russian text compatibility\\Fonts\\f.fnt",Font);}
  put("profiles\\Default\\saves\\s.fos",Encoding.ASCII.GetBytes("save"));
  File.WriteAllText(Path.Combine(dest,"ModOrganizer.ini"),Ini(game1,dest,dest+"/downloads"));
  string oldBackup=Path.Combine(setup,"backup","FalloutNV.exe");Directory.CreateDirectory(Path.GetDirectoryName(oldBackup));File.WriteAllText(oldBackup,"old-pc-original");
  var st=new InstallState{status="installed",game=game1,destination=dest,version="t"};
  st.changes.Add(new Change{path="FalloutNV.exe",existed=true,touched=true,before=Engine.Hash(oldBackup),after=Engine.Hash(Path.Combine(game1,"FalloutNV.exe"))});Engine.SaveState(st);
  var places1=new IntegrationLocations{Desktop=Path.Combine(pc1,"Desktop"),Programs=Path.Combine(pc1,"Programs")};Directory.CreateDirectory(places1.Desktop);Directory.CreateDirectory(places1.Programs);
  Integrations.Apply(new Options{Game=game1,Destination=dest,Store="Steam",DesktopShortcut=shortcuts,StartMenuShortcut=shortcuts},s=>{},places1);
  File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(setup,"Setup and Restore.exe"));
  UninstallSupport.Capture(new Engine(Pkg,x),dest);
  var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);DefenderSupport.ApplyCore(game1,dest,()=>set,(p,add)=>{if(add)set.Add(p);else set.Remove(p);});
  string dest2=Path.Combine(x,"PC2","Copied Mods");Directory.CreateDirectory(Path.GetDirectoryName(dest2));
  if(keepOriginal)CopyTree(dest,dest2);else{Directory.Move(dest,dest2);Directory.Delete(pc1,true);}
  return dest2;
 }
}
