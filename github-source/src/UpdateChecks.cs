using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FalloutModSetup;
// Package revision 1: list edits, extraction of a package with an update part, updating an existing setup (with rollback), GECK profile INIs and
// the GECK (NVSE) entry, and the consistency of the real embedded package and of data part 17.
class UpdateChecks {
 static int count;static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL "+text);count++;Console.WriteLine("PASS "+text);}
 static void Reject(Action a,string text){bool no=false;try{a();}catch{no=true;}Check(no,text);}
 static string Msg(Action a){try{a();}catch(Exception e){return e.Message;}return null;}
 static byte[] B(string s){return Encoding.UTF8.GetBytes(s);}
 static string H(byte[] b){using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
 static void Put(string p,byte[] b){Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllBytes(p,b);}
 static void Put(string p,string t){Put(p,B(t));}
 static string Text(string p){return File.ReadAllText(p);}
 static void Invoke(Engine e,string name,params object[] args){typeof(Engine).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(e,args);}
 static string[] Lines(params string[] l){return l;}

 // ---- A small package: part 01 is the frozen snapshot, part 02 the update ----
 class Fix {public string Folder;public Package Old,New;public Dictionary<string,byte[]> OldFiles,NewFiles;}
 static Fix Build(string dir,bool tamper) {
  Directory.CreateDirectory(dir);var f=new Fix{Folder=dir,OldFiles=new Dictionary<string,byte[]>(),NewFiles=new Dictionary<string,byte[]>()};
  var o=f.OldFiles;
  o["app/mods/Keep/keep.esp"]=B("keep plugin");o["app/mods/Keep/keep.txt"]=B("keep");o["app/mods/Changed/a.esm"]=B("old a");o["app/mods/Changed/user.ini"]=B("old ini");
  o["app/mods/Changed/gone.dds"]=B("gone texture");o["app/mods/Retired/r.esp"]=B("retired plugin");o["app/mods/Retired/meta.ini"]=B("meta");
  o["app/profiles/Default/modlist.txt"]=B("# header\n+Keep\n+Changed\n+Retired\n");
  o["app/profiles/Default/plugins.txt"]=B("# header\nkeep.esp\na.esm\nr.esp\n");o["app/profiles/Default/loadorder.txt"]=B("# header\nkeep.esp\na.esm\nr.esp\n");
  o["tools/xdelta3decode.exe"]=B("tool");
  var n=f.NewFiles;
  n["app/mods/Changed/a.esm"]=B("new a");n["app/mods/Changed/user.ini"]=B("new ini");n["app/mods/New/n.esp"]=B("new plugin");n["app/mods/New/meta.ini"]=B("new meta");
  string p1=Path.Combine(dir,"Fallout Mod Setup-01.bin"),p2=Path.Combine(dir,"Fallout Mod Setup-02.bin");
  Zip(p1,o);Zip(p2,n,tamper?"app/mods/New/n.esp":null);
  Func<string,ArchiveItem> arch=p=>new ArchiveItem{name=Path.GetFileName(p),size=new FileInfo(p).Length,hash=Engine.Hash(p)};
  Func<string,byte[],FileItem> item=(path,b)=>new FileItem{path=path,size=b.Length,hash=H(b)};
  var oldItems=o.Select(x=>item(x.Key,x.Value)).ToList();
  var baseLang=new LanguageItem{id="Russian",masters=new MasterItem[0],fontFiles=new FileItem[0],sourceTranslations=new FileItem[0],patches=new[]{new LanguagePatch{sourceKind="app",source="mods/Keep/keep.esp",sourceHash=H(o["app/mods/Keep/keep.esp"]),target="mods/Russian text compatibility/keep.esp",targetHash="x",patch="p",size=1}}};
  f.Old=new Package{version="t",profile="Default",archives=new[]{arch(p1)},files=oldItems.ToArray(),masters=new MasterItem[0],patches=new PatchItem[0],required=new string[0],languages=new[]{baseLang},installedBytes=oldItems.Sum(x=>x.size)};
  var files=new List<FileItem>();
  foreach(var x in oldItems){
   if(x.path=="app/mods/Retired/r.esp"||x.path=="app/mods/Retired/meta.ini"||x.path=="app/mods/Changed/gone.dds")continue;
   if(n.ContainsKey(x.path)){files.Add(new FileItem{path=x.path,size=n[x.path].Length,hash=H(n[x.path]),part=Path.GetFileName(p2),was=x.path.EndsWith("meta.ini")?null:x.hash});continue;}
   files.Add(x);
  }
  foreach(var x in n)if(!o.ContainsKey(x.Key))files.Add(new FileItem{path=x.Key,size=x.Value.Length,hash=H(x.Value),part=Path.GetFileName(p2)});
  Func<string,string[],string[],ProfileEdit> edit=(name,from,to)=>new ProfileEdit{file=name,remove=new[]{from.Last()},place=new[]{new PlaceItem{line=to[to.Length-1],after=new[]{to[to.Length-2],"# header"}}},result=ListPatch.Digest(to)};
  var ml=edit("modlist.txt",Lines("# header","+Keep","+Changed","+Retired"),Lines("# header","+Keep","+Changed","+New"));
  var pl=edit("plugins.txt",Lines("# header","keep.esp","a.esm","r.esp"),Lines("# header","keep.esp","a.esm","n.esp"));
  var lo=edit("loadorder.txt",Lines("# header","keep.esp","a.esm","r.esp"),Lines("# header","keep.esp","a.esm","n.esp"));
  var lang=new LanguageItem{id="Russian",masters=new MasterItem[0],fontFiles=new FileItem[0],sourceTranslations=new FileItem[0],patches=baseLang.patches,retiredPatches=new[]{new LanguagePatch{target="mods/Russian text compatibility/Retired.esm",targetHash=H(B("old overlay"))}}};
  f.New=new Package{version="t",profile="Default",revision=1,archives=new[]{arch(p1),arch(p2)},files=files.ToArray(),masters=new MasterItem[0],patches=new PatchItem[0],required=new string[0],languages=new[]{lang},
   retired=new[]{item("app/mods/Retired/r.esp",o["app/mods/Retired/r.esp"]),item("app/mods/Retired/meta.ini",o["app/mods/Retired/meta.ini"]),item("app/mods/Changed/gone.dds",o["app/mods/Changed/gone.dds"])},
   profileEdits=new[]{ml,pl,lo},installedBytes=files.Sum(x=>x.size)};
  return f;
 }
 static void Zip(string path,Dictionary<string,byte[]> files,string corrupt=null) {
  if(File.Exists(path))File.Delete(path);
  using(var fs=new FileStream(path,FileMode.CreateNew))using(var z=new ZipArchive(fs,ZipArchiveMode.Create))
   foreach(var kv in files){var e=z.CreateEntry(kv.Key);using(var s=e.Open()){var b=kv.Value;if(kv.Key==corrupt){b=(byte[])b.Clone();b[0]^=0xFF;}s.Write(b,0,b.Length);}}
 }
 static string Snap(string dir){return String.Join("|",Directory.GetFiles(dir,"*",SearchOption.AllDirectories).Where(x=>x.IndexOf("update-backup",StringComparison.OrdinalIgnoreCase)<0).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).Select(x=>x.Substring(dir.Length)+":"+Engine.Hash(x)).ToArray());}
 static void FakeGame(string game){
  var b=new byte[512];b[0]=77;b[1]=90;b[60]=0x80;b[0x80]=0x50;b[0x81]=0x45;b[0x80+22]=0x20;Encoding.ASCII.GetBytes("nvse_steam_loader.dll").CopyTo(b,300);
  Put(Path.Combine(game,"FalloutNV.exe"),b);Put(Path.Combine(game,"FalloutNVLauncher.exe"),"launcher");
 }
 // A finished setup of the old package revision (what a 1.6.1 installation looks like).
 static string Setup(string root,Fix fix,string game,bool russian,bool localized) {
  string dest=Path.Combine(root,"Mods"),setup=Path.Combine(dest,"_setup");Directory.CreateDirectory(setup);FakeGame(game);
  var e=new Engine(fix.Old,fix.Folder);e.ExtractFiles(dest,setup);
  Put(Path.Combine(dest,"ModOrganizer.ini"),"[General]\r\ngameName=New Vegas\r\n[customExecutables]\r\nsize=3\r\n1\\title=New Vegas\r\n1\\binary=\""+game.Replace('\\','/')+"/FalloutNV.exe\"\r\n2\\title=Fallout Launcher\r\n3\\title=My tool\r\n3\\binary=\"C:/tools/x.exe\"\r\n[Settings]\r\nbase_directory=\""+dest.Replace('\\','/')+"\"\r\n");
  Put(Path.Combine(dest,"profiles","Default","fallout.ini"),"[General]\r\n");
  Put(Path.Combine(dest,"mods","Local game files","meta.ini"),"[General]\r\n");
  var state=new InstallState{status="installed",game=game,destination=dest,version="t"};state.changes.Add(new Change{path="FalloutNV.exe",existed=true,touched=true,before="a",after=Engine.Hash(Path.Combine(game,"FalloutNV.exe"))});Engine.SaveState(state);
  if(russian){Put(Path.Combine(dest,"mods","Russian text compatibility","keep.esp"),"ru keep");Put(Path.Combine(dest,"mods","Russian text compatibility","Retired.esm"),"old overlay");}
  if(localized){Put(Path.Combine(dest,"mods",Localization.ModName,"a.esm"),"stale translated a");Put(Path.Combine(dest,"mods",Localization.ModName,"keep.esp"),"fine");}
  UninstallSupport.Capture(e,dest);
  return dest;
 }

 [STAThread]static void Main(string[] args) {
  string root=Path.GetFullPath(args[0]);if(Directory.Exists(root))Directory.Delete(root,true);Directory.CreateDirectory(root);
  string dist=args.Length>1?Path.GetFullPath(args[1]):null;

  // --- List edits ---
  var edit=new ProfileEdit{remove=new[]{"+Gone"},place=new[]{new PlaceItem{line="+Moved",after=new[]{"+Nowhere","+B"}},new PlaceItem{line="+New",after=new[]{"+Moved"}}}};
  var src=Lines("# h","+A","+Moved","+B","+Gone","+C");
  var done=ListPatch.Apply(src,edit);
  Check(String.Join(",",done)=="# h,+A,+B,+Moved,+New,+C","an edit removes lines, moves a line and inserts after an anchor that is itself placed");
  Check(String.Join(",",ListPatch.Apply(done,edit))==String.Join(",",done),"applying the same edit again changes nothing");
  Check(String.Join(",",ListPatch.Apply(Lines("# h","+mine","+b","+C"),edit))=="# h,+mine,+b,+Moved,+New,+C","a user's own entries stay and a case-different anchor still matches");
  Check(ListPatch.Apply(Lines("# h","+z"),new ProfileEdit{place=new[]{new PlaceItem{line="+X",after=new[]{"+nothing"}}}}).Last()=="+X","a line whose anchors are all missing goes to the end");
  Check(ListPatch.Digest(Lines("a","b"))==ListPatch.Digest(Lines("a","b"))&&ListPatch.Digest(Lines("a","b"))!=ListPatch.Digest(Lines("b","a")),"the list digest depends on order");

  // --- Mod moves (revision 2). modlist.txt is written highest priority first, Mod Organizer's pane shows the lowest first. ---
  Func<string[],ModMove[],int,string> mv=(l,m,have)=>{var x=l.ToList();int n=ModlistMoves.Apply(x,m,have);return n+":"+String.Join(",",x);};
  var after1=new ModMove{mod="M",after=new[]{"Anchor"},revision=2};
  Check(mv(Lines("# h","+A","+M","+B","+Anchor","+C"),new[]{after1},0)=="1:# h,+A,+B,+M,+Anchor,+C","\"after\" in the pane puts the mod directly above the anchor in the file");
  Check(mv(Lines("# h","+A","+B","+M","+Anchor","+C"),new[]{after1},0)=="0:# h,+A,+B,+M,+Anchor,+C","a mod that already sits there is not touched");
  Check(mv(Lines("# h","-M","+A","*Anchor","+C"),new[]{after1},0)=="1:# h,+A,-M,*Anchor,+C","the enabled flag of the moved mod stays and an unmanaged anchor is found");
  Check(mv(Lines("# h","+M","+A","+C"),new[]{after1},0)=="0:# h,+M,+A,+C","a missing anchor leaves the mod where it is");
  Check(mv(Lines("# h","+A","+Anchor"),new[]{after1},0)=="0:# h,+A,+Anchor","a mod that is not in the list is not added");
  Check(mv(Lines("# h","+M","+A","+Anchor"),new[]{after1},2)=="0:# h,+M,+A,+Anchor"&&mv(Lines("# h","+M","+A","+Anchor"),new[]{after1},1)=="1:# h,+A,+M,+Anchor","a setup that already has the revision is skipped; an older one is moved");
  Check(mv(Lines("# h","+M","+A","+Late","+Anchor"),new[]{new ModMove{mod="M",after=new[]{"Nope","Late"},revision=2}},0)=="1:# h,+A,+M,+Late,+Anchor","the first anchor that exists is used");
  var before1=new ModMove{mod="M",before=new[]{"Anchor"},revision=2};
  Check(mv(Lines("# h","+M","+A","+Anchor","+C"),new[]{before1},0)=="1:# h,+A,+Anchor,+M,+C","\"before\" in the pane puts the mod directly below the anchor in the file");
  var section=new ModMove{mod="M",sectionEnd="SEC_separator",revision=2};
  Check(mv(Lines("# h","+M","+x","+NEXT_separator","+y","+z","+SEC_separator","+w"),new[]{section},0)=="1:# h,+x,+NEXT_separator,+M,+y,+z,+SEC_separator,+w","the end of a section is directly above the next separator in the pane");
  Check(mv(Lines("# h","+x","+y","+SEC_separator","+M","+w"),new[]{section},0)=="1:# h,+M,+x,+y,+SEC_separator,+w","the last section ends at the top of the file, under the header");
  Check(mv(Lines("# h","+x","-NEXT_separator","+M","+y","+SEC_separator"),new[]{section},0)=="0:# h,+x,-NEXT_separator,+M,+y,+SEC_separator","a mod already at the end of its section stays (a disabled separator counts)");
  var seq=new[]{new ModMove{mod="L",after=new[]{"Cap"},revision=2},new ModMove{mod="F",after=new[]{"L","Cap"},revision=2}};
  Check(mv(Lines("# h","+F","+L","+z","+BASE_separator","+Cap","*DLC"),seq,0)=="2:# h,+z,+BASE_separator,+F,+L,+Cap,*DLC","two mods placed one after the other keep that order (file: F, L, Cap)");
  Check(mv(Lines("# h","+z","+BASE_separator","+F","+L","+Cap"),seq,0)=="0:# h,+z,+BASE_separator,+F,+L,+Cap"&&ModlistMoves.Apply(new List<string>{"# h"},null,0)==0,"moves that are already in place, or none at all, change nothing");

  // --- GECK profile INIs and the GECK (NVSE) entry ---
  string geckProfile=Path.Combine(root,"geck-profile");Directory.CreateDirectory(geckProfile);Put(Path.Combine(geckProfile,"GECKPrefs.ini"),"[General]\r\nkeep=1\r\n");
  UpdateSupport.EnsureGeckProfileInis(geckProfile);
  Check(new FileInfo(Path.Combine(geckProfile,"GECKCustom.ini")).Length==0&&Text(Path.Combine(geckProfile,"GECKPrefs.ini"))=="[General]\r\nkeep=1\r\n","missing GECK profile INIs are created empty and an existing one is left alone");
  string ini="[General]\r\nx=1\r\n[customExecutables]\r\nsize=3\r\n1\\title=New Vegas\r\n2\\title=GECK\r\n3\\title=My tool\r\n3\\binary=\"C:/t.exe\"\r\n[Settings]\r\ny=2\r\n";
  string added=UpdateSupport.AddGeckNvse(ini,@"D:\Games\FNV");
  Check(added.Contains("size=4")&&added.Contains("4\\title=GECK (NVSE)")&&added.Contains("4\\binary=\"D:/Games/FNV/nvse_loader.exe\"")&&added.Contains("4\\arguments=-editor")&&added.Contains("3\\binary=\"C:/t.exe\"")&&added.Contains("[Settings]"),"GECK (NVSE) is added as the next entry and the user's entries are untouched");
  Check(UpdateSupport.AddGeckNvse(added,@"D:\Games\FNV")==added,"the GECK (NVSE) entry is added only once");
  Check(UpdateSupport.AddGeckNvse("[General]\r\nx=1\r\n",@"D:\G")=="[General]\r\nx=1\r\n","a settings file without executables is not invented");
  string moved=PortableSupport.RewriteModOrganizerIni(added.Replace("D:/Games/FNV","E:/Old"),@"E:\Old",@"E:\Old",@"D:\M",@"D:\New game");
  Check(moved.Contains("4\\binary=\"D:/New game/nvse_loader.exe\"")&&moved.Contains("4\\workingDirectory=\"D:/New game\""),"a move or attach repoints the GECK (NVSE) entry");
  var engine=new Engine();string cfgRoot=Path.Combine(root,"cfg"),cfgGame=Path.Combine(cfgRoot,"Game"),cfgDest=Path.Combine(cfgRoot,"Mods"),cfgProfile=Path.Combine(cfgDest,"profiles","Default");
  Directory.CreateDirectory(cfgGame);Put(Path.Combine(cfgGame,"FalloutNV.exe"),"fixture game");
  Put(Path.Combine(cfgProfile,"settings.ini"),"[General]\r\n");Put(Path.Combine(cfgProfile,"fallout.ini"),"[General]\r\n");Put(Path.Combine(cfgProfile,"falloutprefs.ini"),"[Display]\r\n");Put(Path.Combine(cfgProfile,"falloutcustom.ini"),"[Display]\r\n");
  Invoke(engine,"Configure",new Options{Game=cfgGame,Destination=cfgDest,Store="GOG",LocalSaves=true});
  string cfg=Text(Path.Combine(cfgDest,"ModOrganizer.ini"));
  Check(cfg.Contains("size=5")&&cfg.Contains("3\\title=GECK\r\n")&&cfg.Contains("4\\title=FNVEdit")&&cfg.Contains("5\\title=GECK (NVSE)")&&cfg.Contains("5\\arguments=-editor")&&cfg.Contains("5\\binary=\""+cfgGame.Replace('\\','/')+"/nvse_loader.exe\""),"a new installation lists GECK, FNVEdit and GECK (NVSE) in Mod Organizer");
  Check(File.Exists(Path.Combine(cfgProfile,"GECKCustom.ini"))&&File.Exists(Path.Combine(cfgProfile,"GECKPrefs.ini")),"a new installation creates the profile's GECK INI files");

  // --- Extraction of a package that has an update part ---
  var fix=Build(Path.Combine(root,"pkg"),false);
  string freshDest=Path.Combine(root,"Fresh"),freshSupport=Path.Combine(freshDest,"_setup");Directory.CreateDirectory(freshSupport);
  var fe=new Engine(fix.New,fix.Folder);fe.ExtractFiles(freshDest,freshSupport);
  Check(Text(Path.Combine(freshDest,"mods","Changed","a.esm"))=="new a"&&Text(Path.Combine(freshDest,"mods","Changed","user.ini"))=="new ini","a fresh install takes changed files from the update part, not the superseded copies");
  Check(!Directory.Exists(Path.Combine(freshDest,"mods","Retired"))&&!File.Exists(Path.Combine(freshDest,"mods","Changed","gone.dds")),"files the package retired are not extracted");
  Check(Text(Path.Combine(freshDest,"mods","New","n.esp"))=="new plugin"&&Text(Path.Combine(freshSupport,"tools","xdelta3decode.exe"))=="tool","new files and support files are extracted");
  UpdateSupport.ApplyProfileEdits(fe,freshDest,true);
  Check(String.Join(",",File.ReadAllLines(Path.Combine(freshDest,"profiles","Default","modlist.txt")))=="# header,+Keep,+Changed,+New"&&String.Join(",",File.ReadAllLines(Path.Combine(freshDest,"profiles","Default","loadorder.txt")))=="# header,keep.esp,a.esm,n.esp","a fresh install's lists are the revision's lists");
  var wrong=Build(Path.Combine(root,"pkg-wrong"),false);wrong.New.profileEdits[0].result="0000";
  string wrongDest=Path.Combine(root,"WrongFresh");new Engine(wrong.New,wrong.Folder).ExtractFiles(wrongDest,Path.Combine(wrongDest,"_setup"));
  Reject(()=>UpdateSupport.ApplyProfileEdits(new Engine(wrong.New,wrong.Folder),wrongDest,true),"a list that does not match its expected digest stops a fresh install");
  var damaged=Build(Path.Combine(root,"pkg-damaged"),true);string dmgDest=Path.Combine(root,"DmgFresh");
  Check((Msg(()=>new Engine(damaged.New,damaged.Folder).ExtractFiles(dmgDest,Path.Combine(dmgDest,"_setup")))??"").Contains("Damaged extracted file"),"a damaged file in the update part is caught during extraction");
  var extra=Build(Path.Combine(root,"pkg-extra"),false);extra.New.files=extra.New.files.Where(f=>f.path!="app/mods/New/n.esp").ToArray();
  Check((Msg(()=>new Engine(extra.New,extra.Folder).ExtractFiles(Path.Combine(root,"ExtraFresh"),Path.Combine(root,"ExtraFresh","_setup")))??"").Contains("inventory"),"an update part with a file the inventory does not list is rejected");

  // --- Updating an existing setup ---
  string uRoot=Path.Combine(root,"U"),uGame=Path.Combine(uRoot,"Game"),uDest=Setup(uRoot,fix,uGame,true,false);
  Put(Path.Combine(uDest,"mods","Keep","mine.txt"),"my own file");
  Put(Path.Combine(uDest,"mods","Changed","user.ini"),"user edited ini");
  File.WriteAllText(Path.Combine(uDest,"mods","Retired","meta.ini"),"meta edited by MO2");
  File.AppendAllText(Path.Combine(uDest,"profiles","Default","modlist.txt"),"+My own mod\n+Local game files\n");
  File.WriteAllText(Path.Combine(uDest,"profiles","Default","loadorder.txt"),"# header\nmine.esp\nkeep.esp\na.esm\nr.esp\n");
  File.WriteAllText(Path.Combine(uDest,"profiles","Default","plugins.txt"),"# header\nkeep.esp\na.esm\nr.esp\n");
  Put(Path.Combine(uDest,"mods","Local game files","mine.esp"),"user plugin");
  Put(Path.Combine(uDest,"saves-marker.txt"),"x");
  Check(UpdateSupport.Needs(fe,uDest)&&UpdateSupport.Revision(uDest)==0,"a setup without a revision file is revision 0 and needs the update");
  string overlayBefore=Path.Combine(uDest,"mods","Russian text compatibility","Retired.esm");
  var log=new List<string>();string summary=UpdateSupport.Apply(fe,uDest,log.Add);
  Check(Text(Path.Combine(uDest,"mods","Changed","a.esm"))=="new a"&&Text(Path.Combine(uDest,"mods","New","n.esp"))=="new plugin","the update replaces changed files and adds new ones");
  Check(Text(Path.Combine(uDest,"mods","Changed","user.ini"))=="user edited ini"&&log.Any(l=>l.Contains("Kept your edited file")),"a file the user edited is not overwritten");
  Check(Text(Path.Combine(uDest,"mods","New","meta.ini"))=="new meta"&&Text(Path.Combine(uDest,"mods","Keep","mine.txt"))=="my own file"&&Text(Path.Combine(uDest,"mods","Keep","keep.txt"))=="keep","meta.ini and the user's own files are handled as expected");
  Check(!Directory.Exists(Path.Combine(uDest,"mods","Retired"))&&!File.Exists(Path.Combine(uDest,"mods","Changed","gone.dds")),"retired files are removed, including a meta.ini that Mod Organizer had rewritten, and the empty folder");
  Check(!File.Exists(overlayBefore)&&File.Exists(Path.Combine(uDest,"mods","Russian text compatibility","keep.esp")),"an outdated Russian overlay is removed and the others stay");
  Check(String.Join(",",File.ReadAllLines(Path.Combine(uDest,"profiles","Default","modlist.txt")))=="# header,+Keep,+Changed,+New,+My own mod,+Local game files","the mod list is edited, not replaced: the user's own entry stays");
  Check(String.Join(",",File.ReadAllLines(Path.Combine(uDest,"profiles","Default","loadorder.txt")))=="# header,mine.esp,keep.esp,a.esm,n.esp","the plugin order is edited and the user's plugin keeps its place");
  string uIni=Text(Path.Combine(uDest,"ModOrganizer.ini"));
  Check(uIni.Contains("size=4")&&uIni.Contains("4\\title=GECK (NVSE)")&&uIni.Contains("4\\binary=\""+uGame.Replace('\\','/')+"/nvse_loader.exe\"")&&uIni.Contains("3\\title=My tool"),"the GECK (NVSE) entry is added to Mod Organizer's settings");
  Check(File.Exists(Path.Combine(uDest,"profiles","Default","GECKCustom.ini"))&&File.Exists(Path.Combine(uDest,"profiles","Default","GECKPrefs.ini")),"the profile's GECK INI files are created");
  var stampKeep=File.GetLastWriteTimeUtc(Path.Combine(uDest,"mods","Keep","keep.esp")).Ticks;var stampA=File.GetLastWriteTimeUtc(Path.Combine(uDest,"mods","Changed","a.esm")).Ticks;var stampN=File.GetLastWriteTimeUtc(Path.Combine(uDest,"mods","New","n.esp")).Ticks;
  Check(stampKeep<stampA&&stampA<stampN&&File.GetLastWriteTimeUtc(Path.Combine(uDest,"mods","Local game files","mine.esp")).Ticks<stampKeep,"plugin timestamps follow the new order");
  Check(UpdateSupport.Revision(uDest)==1&&!UpdateSupport.Needs(fe,uDest),"the revision is recorded");
  var receipt=Engine.Json().Deserialize<OwnedFiles>(File.ReadAllText(Path.Combine(uDest,"_setup","installed-files.json")));
  Check(receipt.files.Any(f=>f.path=="mods/New/n.esp"&&f.hash==H(B("new plugin")))&&!receipt.files.Any(f=>f.path.StartsWith("mods/Retired/"))&&receipt.files.Where(f=>File.Exists(Path.Combine(uDest,f.path))&&f.path!="mods/Changed/user.ini").All(f=>Engine.Hash(Path.Combine(uDest,f.path))==f.hash),"the uninstall receipt follows the update (an edited file keeps its recorded original, so Uninstall will not delete it)");
  string backup=Directory.GetDirectories(Path.Combine(uDest,"_setup","update-backup")).Single();
  Check(Text(Path.Combine(backup,"files","mods","Changed","a.esm"))=="old a"&&Text(Path.Combine(backup,"files","profiles","Default","modlist.txt")).Contains("+Retired"),"replaced files and the old lists are backed up");
  Check(File.Exists(Path.Combine(uDest,"_setup","Setup and Restore.exe")),"the setup helper is refreshed");
  string snap=Snap(uDest);Check(UpdateSupport.Apply(fe,uDest,null).Contains("already up to date")&&Snap(uDest)==snap,"running the update again changes nothing");
  Check(summary.Contains("revision 1")&&File.Exists(Path.Combine(uDest,"saves-marker.txt")),"the summary names the revision");

  // --- A failure halfway puts everything back ---
  var bad=Build(Path.Combine(root,"pkg-bad"),true);string rRoot=Path.Combine(root,"R"),rDest=Setup(rRoot,bad,Path.Combine(rRoot,"Game"),true,false);
  string before=Snap(rDest);var rLog=new List<string>();
  string failure=Msg(()=>UpdateSupport.Apply(new Engine(bad.New,bad.Folder),rDest,rLog.Add));
  Check(failure!=null&&failure.Contains("Damaged update file")&&failure.Contains("left as it was"),"a damaged update file stops the update");
  Check(Snap(rDest)==before&&UpdateSupport.Revision(rDest)==0&&!Directory.Exists(Path.Combine(rDest,"mods","New")),"every touched file, list and the revision were put back and nothing new remains");
  Check(rLog.Any(l=>l.Contains("was put back")),"the rollback is logged");
  string mRoot=Path.Combine(root,"M"),mDest=Setup(mRoot,fix,Path.Combine(mRoot,"Game"),false,false);
  File.Delete(Path.Combine(fix.Folder,"Fallout Mod Setup-02.bin"));
  Check((Msg(()=>UpdateSupport.Apply(fe,mDest,null))??"").Contains("Missing or damaged data part")&&UpdateSupport.Revision(mDest)==0,"a missing update part is reported before anything changes");
  fix=Build(Path.Combine(root,"pkg"),false);fe=new Engine(fix.New,fix.Folder);
  string vRoot=Path.Combine(root,"V"),vDest=Setup(vRoot,fix,Path.Combine(vRoot,"Game"),false,false);var otherVersion=new Package{version="other",revision=1,archives=fix.New.archives,files=fix.New.files};
  Check((Msg(()=>UpdateSupport.Apply(new Engine(otherVersion,fix.Folder),vDest,null))??"").Contains("package version"),"a setup from another package version is refused");
  string movedDest=Path.Combine(vRoot,"MovedMods");Directory.Move(vDest,movedDest);
  Check((Msg(()=>UpdateSupport.Apply(fe,movedDest,null))??"").Contains("was moved"),"a moved setup must be repaired before it is updated");

  // --- Revision 2: the same moves on an update (a setup at revision 1 and one at revision 0) and on a fresh installation ---
  Func<Package,int,ModMove[],Package> withMoves=(p,r,m)=>new Package{version=p.version,profile=p.profile,activeMods=p.activeMods,plugins=p.plugins,installedBytes=p.installedBytes,archives=p.archives,files=p.files,masters=p.masters,patches=p.patches,required=p.required,languages=p.languages,revision=r,retired=p.retired,profileEdits=p.profileEdits,modMoves=m};
  var realMoves=new[]{
   new ModMove{mod="Local game files",after=new[]{"Captured game-folder files"},revision=2},
   new ModMove{mod="Framework support",after=new[]{"Local game files","Captured game-folder files"},revision=2},
   new ModMove{mod="WMX - Hide Notes and Uninstall Menu",after=new[]{"WMX-DLC","Weapon Mods Expanded - WMX"},revision=2},
   new ModMove{mod="Performance Tweaks",before=new[]{"BUG FIXES_separator"},sectionEnd="PERFORMANCE_separator",revision=2}};
  string gRoot=Path.Combine(root,"G"),gDest=Setup(gRoot,fix,Path.Combine(gRoot,"Game"),false,false);
  UpdateSupport.Apply(fe,gDest,null);
  string gProfile=Path.Combine(gDest,"profiles","Default"),gList=Path.Combine(gProfile,"modlist.txt");
  var arranged=Lines("# header","+Performance Tweaks","+Framework support","+Local game files","+WMX - Hide Notes and Uninstall Menu","+My own mod","-BUG FIXES_separator","+Keep","+PERFORMANCE_separator","+WMX-DLC","+Changed","-BASE_separator","+Captured game-folder files","*DLC: x");
  File.WriteAllLines(gList,arranged);
  string gPlugins=Text(Path.Combine(gProfile,"plugins.txt")),gOrder=Text(Path.Combine(gProfile,"loadorder.txt"));
  var fe2=new Engine(withMoves(fix.New,2,realMoves),fix.Folder);
  Check(UpdateSupport.Revision(gDest)==1&&UpdateSupport.Needs(fe2,gDest),"a setup at revision 1 needs a revision 2 package");
  var gLog=new List<string>();string gSummary=UpdateSupport.Apply(fe2,gDest,gLog.Add);
  var expectedList=Lines("# header","+My own mod","-BUG FIXES_separator","+Performance Tweaks","+Keep","+PERFORMANCE_separator","+WMX - Hide Notes and Uninstall Menu","+WMX-DLC","+Changed","-BASE_separator","+Framework support","+Local game files","+Captured game-folder files","*DLC: x");
  Check(File.ReadAllLines(gList).SequenceEqual(expectedList),"the update moves exactly the four entries: Local game files and Framework support above Captured (after it in the pane), the WMX companion above WMX-DLC, Performance Tweaks at the end of its section");
  var movers=new[]{"Performance Tweaks","Framework support","Local game files","WMX - Hide Notes and Uninstall Menu"};
  Check(File.ReadAllLines(gList).Where(l=>!movers.Contains(l.Substring(1))).SequenceEqual(arranged.Where(l=>!movers.Contains(l.Substring(1)))),"every other entry keeps its place and flag, including the user's own mod and the user's order");
  Check(Text(Path.Combine(gProfile,"plugins.txt"))==gPlugins&&Text(Path.Combine(gProfile,"loadorder.txt"))==gOrder,"the revision 1 list edits are not applied again and the plugin lists are untouched");
  Check(UpdateSupport.Revision(gDest)==2&&gSummary.Contains("4 mod list entries moved")&&gLog.Count(l=>l.StartsWith("Mod list: moved"))==4,"the update records revision 2 and says what it moved");
  var gBackups=Directory.GetDirectories(Path.Combine(gDest,"_setup","update-backup")).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
  Check(File.ReadAllLines(Path.Combine(gBackups.Last(),"files","profiles","Default","modlist.txt")).SequenceEqual(arranged),"the mod list as the user had it is backed up before the moves");
  string gSnap=Snap(gDest);Check(UpdateSupport.Apply(fe2,gDest,null).Contains("already up to date")&&Snap(gDest)==gSnap,"running the revision 2 update again changes nothing");
  // A user who had already put the entries there (or who removed the anchors) is left alone.
  string kRoot=Path.Combine(root,"K"),kDest=Setup(kRoot,fix,Path.Combine(kRoot,"Game"),false,false);UpdateSupport.Apply(fe,kDest,null);
  string kList=Path.Combine(kDest,"profiles","Default","modlist.txt");File.WriteAllLines(kList,expectedList.Where(l=>!l.Contains("WMX-DLC")&&!l.StartsWith("+Captured")).ToArray());
  var kBefore=File.ReadAllLines(kList);var kLog=new List<string>();UpdateSupport.Apply(new Engine(withMoves(fix.New,2,realMoves),fix.Folder),kDest,kLog.Add);
  Check(File.ReadAllLines(kList).SequenceEqual(kBefore)&&UpdateSupport.Revision(kDest)==2&&!kLog.Any(l=>l.StartsWith("Mod list: moved")),"entries that already sit in place, or whose anchor mod is missing, are not moved");
  // A file may list several earlier packaged copies (was + wasAlso): the update replaces it while it is any of them.
  Func<string[],string,Tuple<string,List<string>>> alsoRun=(also,tag)=>{
   string r2=Path.Combine(root,tag),g2=Path.Combine(r2,"Game"),d2=Setup(r2,fix,g2,true,false);
   var ser2=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};var pk=ser2.Deserialize<Package>(ser2.Serialize(fix.New));
   var uf=pk.files.Single(f=>f.path=="app/mods/Changed/user.ini");uf.was=H(B("another earlier copy"));uf.wasAlso=also;
   var lg=new List<string>();UpdateSupport.Apply(new Engine(pk,fix.Folder),d2,lg.Add);return Tuple.Create(Text(Path.Combine(d2,"mods","Changed","user.ini")),lg);};
  var wa1=alsoRun(new[]{H(B("old ini"))},"WA1");var wa2=alsoRun(null,"WA2");
  Check(wa1.Item1=="new ini"&&!wa1.Item2.Any(l=>l.Contains("Kept your edited file: mods/Changed/user.ini")),"a file is replaced while it still equals any earlier packaged copy listed in was or wasAlso");
  Check(wa2.Item1=="old ini"&&wa2.Item2.Any(l=>l.Contains("Kept your edited file: mods/Changed/user.ini")),"a file that equals none of the listed earlier copies is kept as the user's own");  Check(kLog.Any(l=>l.Contains("no place found")),"a move that has no anchor is logged and skipped");
  // A setup at revision 0 gets the revision 1 edits first and the moves second.
  string hRoot=Path.Combine(root,"H"),hDest=Setup(hRoot,fix,Path.Combine(hRoot,"Game"),false,false);
  File.AppendAllText(Path.Combine(hDest,"profiles","Default","modlist.txt"),"+Local game files\n");
  UpdateSupport.Apply(new Engine(withMoves(fix.New,2,new[]{new ModMove{mod="Local game files",after=new[]{"Keep"},revision=2}}),fix.Folder),hDest,null);
  Check(File.ReadAllLines(Path.Combine(hDest,"profiles","Default","modlist.txt")).SequenceEqual(Lines("# header","+Local game files","+Keep","+Changed","+New"))&&UpdateSupport.Revision(hDest)==2,"a setup at revision 0 gets the list edits and then the moves");
  // A fresh installation: PreparePluginOrder puts the three entries at the top, the moves then place them.
  string fRoot=Path.Combine(root,"FreshMoves"),fDest=Path.Combine(fRoot,"Mods"),fProfile=Path.Combine(fDest,"profiles","Default");Directory.CreateDirectory(fProfile);
  Put(Path.Combine(fDest,"mods","TopMod","keep.esp"),"plugin");Put(Path.Combine(fDest,"mods","WMX - Hide Notes and Uninstall Menu","Config","WMXPersonal.ini"),"[x]\r\n");
  var packaged=Lines("# header","+Performance Tweaks","+TopMod","-BUG FIXES_separator","+NVIOP","+Faster Decompression","+PERFORMANCE_separator","+Better Transitions - NVSE","+WMX-DLC","+Weapon Mods Expanded - WMX","-MISC_separator","+Fixed ESMs","-BASE_separator","+Captured game-folder files");
  Action resetFresh=()=>{File.WriteAllLines(Path.Combine(fProfile,"modlist.txt"),packaged);File.WriteAllText(Path.Combine(fProfile,"plugins.txt"),"keep.esp\n");File.WriteAllText(Path.Combine(fProfile,"loadorder.txt"),"keep.esp\n");};
  resetFresh();var freshOptions=new Options{Destination=fDest,Game=Path.Combine(fRoot,"Game")};
  Invoke(new Engine(fix.New,fix.Folder),"PreparePluginOrder",freshOptions);
  Check(File.ReadAllLines(Path.Combine(fProfile,"modlist.txt")).Take(5).SequenceEqual(Lines("# header","+Framework support","+Local game files","+WMX - Hide Notes and Uninstall Menu","+Performance Tweaks")),"a package without moves keeps the earlier placement at the top");
  resetFresh();var freshEngine=new Engine(withMoves(fix.New,2,realMoves),fix.Folder);
  Invoke(freshEngine,"PreparePluginOrder",freshOptions);Invoke(freshEngine,"PreparePluginOrder",freshOptions);
  var freshExpected=Lines("# header","+TopMod","-BUG FIXES_separator","+Performance Tweaks","+NVIOP","+Faster Decompression","+PERFORMANCE_separator","+Better Transitions - NVSE","+WMX - Hide Notes and Uninstall Menu","+WMX-DLC","+Weapon Mods Expanded - WMX","-MISC_separator","+Fixed ESMs","-BASE_separator","+Framework support","+Local game files","+Captured game-folder files");
  Check(File.ReadAllLines(Path.Combine(fProfile,"modlist.txt")).SequenceEqual(freshExpected),"a fresh installation ends with the requested order, also when the preparation runs twice");
  Check(Text(Path.Combine(fProfile,"plugins.txt"))=="keep.esp\n"&&Text(Path.Combine(fProfile,"loadorder.txt"))=="keep.esp\n"&&File.Exists(Path.Combine(fDest,"mods","Local game files","meta.ini")),"a fresh installation leaves the plugin lists alone and still creates the Local game files mod");

  // --- Revision 3: one more small part with a new mod; a revision 3 edit inserts its line and a revision 3 move puts it directly above an anchor mod ---
  string p3Path=Path.Combine(fix.Folder,"Fallout Mod Setup-03.bin");var wheelFiles=new Dictionary<string,byte[]>{{"app/mods/Wheel/wheel.ini",B("wheel on")},{"app/mods/Wheel/meta.ini",B("wm")}};Zip(p3Path,wheelFiles);
  var p3Edit=new ProfileEdit{file="modlist.txt",remove=new string[0],place=new[]{new PlaceItem{line="+Wheel",after=new[]{"+Keep"}}},revision=3,result=ListPatch.Digest(Lines("# header","+Keep","+Wheel","+Changed","+New"))};
  var p3=new Package{version=fix.New.version,profile=fix.New.profile,activeMods=fix.New.activeMods,plugins=fix.New.plugins,archives=fix.New.archives.Concat(new[]{new ArchiveItem{name="Fallout Mod Setup-03.bin",size=new FileInfo(p3Path).Length,hash=Engine.Hash(p3Path)}}).ToArray(),
   files=fix.New.files.Concat(wheelFiles.Select(x=>new FileItem{path=x.Key,size=x.Value.Length,hash=H(x.Value),part="Fallout Mod Setup-03.bin"})).ToArray(),masters=fix.New.masters,patches=fix.New.patches,required=fix.New.required,languages=fix.New.languages,revision=3,retired=fix.New.retired,
   profileEdits=fix.New.profileEdits.Concat(new[]{p3Edit}).ToArray(),modMoves=new[]{new ModMove{mod="Wheel",after=new[]{"Changed"},revision=3}},installedBytes=fix.New.installedBytes+wheelFiles.Sum(x=>x.Value.Length)};
  string wRoot=Path.Combine(root,"W3Fresh"),wDest=Path.Combine(wRoot,"Mods");Directory.CreateDirectory(Path.Combine(wDest,"_setup"));var e3=new Engine(p3,fix.Folder);e3.ExtractFiles(wDest,Path.Combine(wDest,"_setup"));
  UpdateSupport.ApplyProfileEdits(e3,wDest,true);var wList=File.ReadAllLines(Path.Combine(wDest,"profiles","Default","modlist.txt")).ToList();
  Check(wList.SequenceEqual(Lines("# header","+Keep","+Wheel","+Changed","+New"))&&ModlistMoves.Apply(wList,p3.modMoves,0)==0&&Text(Path.Combine(wDest,"mods","Wheel","wheel.ini"))=="wheel on","a fresh install gets the revision 3 mod and the list edit gives the expected list (the move finds it already in place)");
  string xRoot=Path.Combine(root,"W3Update"),xDest=Setup(xRoot,fix,Path.Combine(xRoot,"Game"),false,false);UpdateSupport.Apply(fe,xDest,null);
  File.WriteAllText(Path.Combine(xDest,"_setup","revision.txt"),"2");string xList=Path.Combine(xDest,"profiles","Default","modlist.txt");File.WriteAllLines(xList,Lines("# header","+My own mod","+Changed","+Keep","+New"));
  string xBefore=Snap(xDest);var xLog=new List<string>();UpdateSupport.Apply(e3,xDest,xLog.Add);
  Check(File.ReadAllLines(xList).SequenceEqual(Lines("# header","+My own mod","+Wheel","+Changed","+Keep","+New")),"an update of a revision 2 setup puts the new mod directly above its anchor and keeps the user's own order");
  Check(Text(Path.Combine(xDest,"mods","Wheel","wheel.ini"))=="wheel on"&&UpdateSupport.Revision(xDest)==3&&xLog.Any(l=>l.Contains("moved \"Wheel\"")),"the update adds the folder, records revision 3 and says what it moved");
  Check(Snap(xDest).Split('|').Where(s=>!s.Contains("Wheel")&&!s.Contains("modlist.txt")&&!s.Contains("revision.txt")&&!s.Contains("installed-files.json")&&!s.Contains("Setup and Restore")).All(s=>xBefore.Contains(s)),"nothing else in the mod folder changed: revision 1 and 2 files and edits are not applied again");
  string yRoot=Path.Combine(root,"W3NoAnchor"),yDest=Setup(yRoot,fix,Path.Combine(yRoot,"Game"),false,false);UpdateSupport.Apply(fe,yDest,null);
  File.WriteAllText(Path.Combine(yDest,"_setup","revision.txt"),"2");string yList=Path.Combine(yDest,"profiles","Default","modlist.txt");File.WriteAllLines(yList,Lines("# header","+Keep","+New"));var yLog=new List<string>();
  UpdateSupport.Apply(e3,yDest,yLog.Add);
  Check(File.ReadAllLines(yList).SequenceEqual(Lines("# header","+Keep","+Wheel","+New"))&&yLog.Any(l=>l.Contains("no place found"))&&UpdateSupport.Revision(yDest)==3,"without the anchor mod the new mod is added where the edit puts it and not moved");
  string zSnap=Snap(xDest);Check(UpdateSupport.Apply(e3,xDest,null).Contains("already up to date")&&Snap(xDest)==zSnap,"running the revision 3 update again changes nothing");
  // --- Revision 5: a retired mod leaves a setup (untouched files go, edited ones stay), its list lines go, INI keys follow the new defaults only where the user left them ---
  var wheelRetired=wheelFiles.Select(x=>new FileItem{path=x.Key,size=x.Value.Length,hash=H(x.Value)}).ToArray();
  var rm5=new ProfileEdit{file="modlist.txt",remove=new[]{"+Wheel","-Wheel"},place=new PlaceItem[0],revision=5};
  var ini5=new[]{new IniEdit{file="falloutprefs.ini",section="Display",key="iMaxAnisotropy",was="15",value="8",revision=5},new IniEdit{file="falloutprefs.ini",section="Display",key="iWaterMultisamples",was="4",value="2",revision=5},
   new IniEdit{file="falloutcustom.ini",section="Audio",key="bMultiThreadAudio",was="1",value="0",revision=5},new IniEdit{file="falloutcustom.ini",section="Audio",key="bNew",was=null,value="1",revision=5}};
  var p5=new Package{version=fix.New.version,profile=fix.New.profile,activeMods=fix.New.activeMods,plugins=fix.New.plugins,archives=fix.New.archives,files=fix.New.files,masters=fix.New.masters,patches=fix.New.patches,required=fix.New.required,languages=fix.New.languages,revision=5,
   retired=fix.New.retired.Concat(wheelRetired).ToArray(),profileEdits=fix.New.profileEdits.Concat(new[]{rm5}).ToArray(),modMoves=new ModMove[0],iniEdits=ini5,installedBytes=fix.New.installedBytes};
  var e5=new Engine(p5,fix.Folder);
  Func<string,string> r5Setup=name=>{string rr=Path.Combine(root,name),dd=Setup(rr,fix,Path.Combine(rr,"Game"),false,false);UpdateSupport.Apply(e3,dd,null);string pf=Path.Combine(dd,"profiles","Default");
   File.WriteAllText(Path.Combine(pf,"falloutprefs.ini"),"[Display]\r\niMaxAnisotropy=15\r\niWaterMultisamples=4\r\niSize W=1920\r\n");File.WriteAllText(Path.Combine(pf,"falloutcustom.ini"),"[Audio]\r\nbMultiThreadAudio=1\r\n");return dd;};
  string r5A=r5Setup("R5A");string r5Prefs=Path.Combine(r5A,"profiles","Default","falloutprefs.ini"),r5Custom=Path.Combine(r5A,"profiles","Default","falloutcustom.ini");
  Check(Directory.Exists(Path.Combine(r5A,"mods","Wheel"))&&File.ReadAllLines(Path.Combine(r5A,"profiles","Default","modlist.txt")).Contains("+Wheel")&&UpdateSupport.Revision(r5A)==3,"a revision 3 setup has the wheel mod and its list line (start of the retirement test)");
  var r5Log=new List<string>();UpdateSupport.Apply(e5,r5A,r5Log.Add);
  Check(!Directory.Exists(Path.Combine(r5A,"mods","Wheel"))&&!File.ReadAllLines(Path.Combine(r5A,"profiles","Default","modlist.txt")).Contains("+Wheel")&&File.ReadAllLines(Path.Combine(r5A,"profiles","Default","modlist.txt")).SequenceEqual(Lines("# header","+Keep","+Changed","+New")),"an update retires an untouched mod: folder gone, list line gone, the rest of the list as it was");
  Check(Text(r5Prefs).Contains("iMaxAnisotropy=8")&&Text(r5Prefs).Contains("iWaterMultisamples=2")&&Text(r5Prefs).Contains("iSize W=1920")&&Text(r5Custom).Contains("bMultiThreadAudio=0")&&Text(r5Custom).Contains("bNew=1")&&UpdateSupport.Revision(r5A)==5,"INI keys that still hold the old packaged value follow the new defaults, a missing key is added, other keys stay, revision 5 is recorded");
  Check(Directory.GetDirectories(Path.Combine(r5A,"_setup","update-backup")).Any(d=>File.Exists(Path.Combine(d,"files","profiles","Default","falloutprefs.ini"))&&Text(Path.Combine(d,"files","profiles","Default","falloutprefs.ini")).Contains("iMaxAnisotropy=15")),"the INI as it was is backed up before its keys change");
  string r5B=r5Setup("R5B");File.WriteAllText(Path.Combine(r5B,"mods","Wheel","wheel.ini"),"user edited");File.WriteAllText(Path.Combine(r5B,"profiles","Default","falloutprefs.ini"),"[Display]\r\niMaxAnisotropy=2\r\niWaterMultisamples=4\r\n");
  var r5BLog=new List<string>();UpdateSupport.Apply(e5,r5B,r5BLog.Add);
  Check(Text(Path.Combine(r5B,"mods","Wheel","wheel.ini"))=="user edited"&&!File.Exists(Path.Combine(r5B,"mods","Wheel","meta.ini"))&&r5BLog.Any(l=>l.Contains("Kept a changed file of a retired mod"))&&!File.ReadAllLines(Path.Combine(r5B,"profiles","Default","modlist.txt")).Contains("+Wheel"),"a retired mod's edited file is kept (the rest of the mod goes) and the list line still goes");
  Check(Text(Path.Combine(r5B,"profiles","Default","falloutprefs.ini")).Contains("iMaxAnisotropy=2")&&Text(Path.Combine(r5B,"profiles","Default","falloutprefs.ini")).Contains("iWaterMultisamples=2")&&r5BLog.Any(l=>l.Contains("Kept your value")),"a value the user changed is kept while an untouched one is updated");
  string r5C=Path.Combine(root,"R5C");Directory.CreateDirectory(Path.Combine(r5C,"profiles","Default"));File.WriteAllText(Path.Combine(r5C,"profiles","Default","falloutprefs.ini"),"[Display]\r\niMaxAnisotropy=15\r\niWaterMultisamples=3\r\n");File.WriteAllText(Path.Combine(r5C,"profiles","Default","falloutcustom.ini"),"[Audio]\r\n");
  Check(UpdateSupport.ApplyIniEdits(e5,r5C,5,false,null).Count==0&&Text(Path.Combine(r5C,"profiles","Default","falloutprefs.ini")).Contains("iMaxAnisotropy=15"),"a setup that already has revision 5 gets no INI edit");
  UpdateSupport.ApplyIniEdits(e5,r5C,0,true,null);
  Check(Text(Path.Combine(r5C,"profiles","Default","falloutprefs.ini")).Contains("iMaxAnisotropy=8")&&Text(Path.Combine(r5C,"profiles","Default","falloutprefs.ini")).Contains("iWaterMultisamples=2")&&Text(Path.Combine(r5C,"profiles","Default","falloutcustom.ini")).Contains("bMultiThreadAudio=0"),"a fresh installation sets every new default");
  Reject(()=>UpdateSupport.ApplyIniEdits(new Engine(new Package{version="t",archives=new ArchiveItem[0],files=new FileItem[0],iniEdits=new[]{new IniEdit{file="..\\x.ini",section="a",key="b",value="c",revision=5}}},fix.Folder),r5C,0,true,null),"an INI edit cannot leave the profile folder");
  // --- Revision 8: optional mods added DISABLED, only when absent, above their anchor mod; an existing entry is never touched ---
  var addEdit=new ProfileEdit{file="modlist.txt",remove=new string[0],place=new[]{new PlaceItem{line="-Opt",addOnly=true,above="Anchor",after=new[]{"+Before"}}},revision=8};
  Func<string[],string> ap=src8=>String.Join(",",ListPatch.Apply(src8,addEdit));
  Check(ap(Lines("# h","+Before","+Anchor","+Z"))=="# h,+Before,-Opt,+Anchor,+Z"&&ap(Lines("# h","+A","-Anchor","+Z"))=="# h,+A,-Opt,-Anchor,+Z","an optional mod is added disabled directly above its anchor mod, whatever the flag of the anchor");
  Check(ap(Lines("# h","+Before","+Opt","+Anchor"))=="# h,+Before,+Opt,+Anchor"&&ap(Lines("# h","-Opt","+Before","+Anchor"))=="# h,-Opt,+Before,+Anchor"&&ap(Lines("# h","+Before","+opt","+Anchor"))=="# h,+Before,+opt,+Anchor","an entry already in the list (enabled, disabled, any spelling) is left exactly where it is: nothing is added, enabled or moved");
  Check(ap(Lines("# h","+Before","+Z"))=="# h,+Before,-Opt,+Z"&&ap(Lines("# h","+Q","+Z"))=="# h,+Q,+Z","without the anchor mod the first remaining neighbour is used, and without any anchor nothing is added");
  var p8=new Package{version=fix.New.version,profile=fix.New.profile,archives=fix.New.archives,files=fix.New.files,masters=fix.New.masters,patches=fix.New.patches,required=fix.New.required,languages=fix.New.languages,revision=8,retired=fix.New.retired,profileEdits=fix.New.profileEdits.Concat(new[]{new ProfileEdit{file="modlist.txt",remove=new string[0],place=new[]{new PlaceItem{line="-Opt",addOnly=true,above="Changed",after=new[]{"+Keep"}}},revision=8}}).ToArray(),modMoves=new ModMove[0],installedBytes=fix.New.installedBytes};
  var e8=new Engine(p8,fix.Folder);string o8=Path.Combine(root,"O8"),o8Dest=Setup(o8,fix,Path.Combine(o8,"Game"),false,false);UpdateSupport.Apply(e3,o8Dest,null);
  string o8List=Path.Combine(o8Dest,"profiles","Default","modlist.txt");File.WriteAllLines(o8List,Lines("# header","+My own mod","+Keep","+Changed","+New"));File.WriteAllText(Path.Combine(o8Dest,"_setup","revision.txt"),"7");
  UpdateSupport.Apply(e8,o8Dest,null);
  Check(File.ReadAllLines(o8List).SequenceEqual(Lines("# header","+My own mod","+Keep","-Opt","+Changed","+New"))&&UpdateSupport.Revision(o8Dest)==8,"an update of a revision 7 setup adds the optional mod disabled above its anchor and changes nothing else in the list");
  string o8b=Path.Combine(root,"O8b"),o8bDest=Setup(o8b,fix,Path.Combine(o8b,"Game"),false,false);UpdateSupport.Apply(e3,o8bDest,null);string o8bList=Path.Combine(o8bDest,"profiles","Default","modlist.txt");File.WriteAllLines(o8bList,Lines("# header","+Opt","+Keep","+Changed","+New"));File.WriteAllText(Path.Combine(o8bDest,"_setup","revision.txt"),"7");
  UpdateSupport.Apply(e8,o8bDest,null);
  Check(File.ReadAllLines(o8bList).SequenceEqual(Lines("# header","+Opt","+Keep","+Changed","+New")),"an optional mod the user already enabled stays enabled where it is: no duplicate, no move");
  // --- Revision 9: an optional mod that a revision 8 setup already has is retired again: untouched files and folder go, an edited file stays, the list line goes ---
  var optFile=new FileItem{path="app/mods/Opt/o.ini",size=3,hash=H(B("abc"))};
  var p9=new Package{version=fix.New.version,profile=fix.New.profile,archives=fix.New.archives,files=fix.New.files,masters=fix.New.masters,patches=fix.New.patches,required=fix.New.required,languages=fix.New.languages,revision=9,retired=fix.New.retired.Concat(new[]{optFile,new FileItem{path="app/mods/Opt/meta.ini",size=1,hash=H(B("m"))}}).ToArray(),profileEdits=p8.profileEdits.Concat(new[]{new ProfileEdit{file="modlist.txt",remove=new[]{"+Opt","-Opt"},place=new PlaceItem[0],revision=9}}).ToArray(),modMoves=new ModMove[0],installedBytes=fix.New.installedBytes};
  var e9=new Engine(p9,fix.Folder);
  Func<string,string,string> o9Setup=(name,content)=>{string rr=Path.Combine(root,name),dd=Setup(rr,fix,Path.Combine(rr,"Game"),false,false);UpdateSupport.Apply(e3,dd,null);Put(Path.Combine(dd,"mods","Opt","o.ini"),content);Put(Path.Combine(dd,"mods","Opt","meta.ini"),"edited by MO2");File.WriteAllLines(Path.Combine(dd,"profiles","Default","modlist.txt"),Lines("# header","+Keep","-Opt","+Changed","+New"));File.WriteAllText(Path.Combine(dd,"_setup","revision.txt"),"8");return dd;};
  string o9A=o9Setup("O9A","abc");UpdateSupport.Apply(e9,o9A,null);
  Check(!Directory.Exists(Path.Combine(o9A,"mods","Opt"))&&File.ReadAllLines(Path.Combine(o9A,"profiles","Default","modlist.txt")).SequenceEqual(Lines("# header","+Keep","+Changed","+New"))&&UpdateSupport.Revision(o9A)==9,"a revision 8 setup loses the retired optional mod: folder and list line are gone, the rest of the list is as it was");
  string o9B=o9Setup("O9B","my own value");var o9Log=new List<string>();UpdateSupport.Apply(e9,o9B,o9Log.Add);
  Check(Text(Path.Combine(o9B,"mods","Opt","o.ini"))=="my own value"&&!File.Exists(Path.Combine(o9B,"mods","Opt","meta.ini"))&&o9Log.Any(l=>l.Contains("Kept a changed file of a retired mod"))&&!File.ReadAllLines(Path.Combine(o9B,"profiles","Default","modlist.txt")).Contains("-Opt"),"a file of the retired optional mod that was edited stays, the list line still goes");
  string o8Snap=Snap(o8Dest);Check(UpdateSupport.Apply(e8,o8Dest,null).Contains("already up to date")&&Snap(o8Dest)==o8Snap,"running the revision 8 update again changes nothing");

  // --- Localized setups: stale translated copies of updated plugins go away, the update still completes ---
  string lRoot=Path.Combine(root,"L"),lDest=Setup(lRoot,fix,Path.Combine(lRoot,"Game"),false,true);
  UpdateSupport.Apply(fe,lDest,log.Add);
  Check(!File.Exists(Path.Combine(lDest,"mods",Localization.ModName,"a.esm"))&&File.Exists(Path.Combine(lDest,"mods",Localization.ModName,"keep.esp"))&&UpdateSupport.Revision(lDest)==1,"a localized overlay copy of an updated plugin is dropped and the update completes without language files");

  // --- Interaction with the other package features ---
  Check(fix.New.files.Where(f=>!f.path.StartsWith("app/")).All(f=>f.part==null),"support files (verified when a setup is attached) all come from the original parts");

  // --- The real package embedded in this EXE ---
  var real=new Engine().P;
  Check(real.revision==13&&real.archives.Length==18&&Enumerable.Range(1,18).All(i=>real.archives[i-1].name=="Fallout Mod Setup-"+i.ToString("00")+".bin"),"the package is revision 13 and its data parts are numbered 01 to 18 without a gap");
  Check(real.files.All(f=>f.part==null||real.archives.Any(a=>a.name==f.part))&&real.files.Where(f=>f.part!=null).Select(f=>f.part).Distinct().OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(new[]{"Fallout Mod Setup-17.bin","Fallout Mod Setup-18.bin"})&&real.archives.Select(a=>a.name).Distinct().Count()==real.archives.Length&&!real.archives.Any(a=>a.name.EndsWith("-19.bin")||a.name.EndsWith("-20.bin")||a.name.EndsWith("-21.bin"))&&real.archives.All(a=>a.size>0&&a.hash.Length==64),"every file that names a part names one that is in the package (17 or 18), each archive is listed once with size and hash, and the old parts 19, 20 and 21 are gone");
  Check(real.modMoves!=null&&real.modMoves.Length==4&&real.modMoves.All(m=>m.revision==2)&&real.profileEdits.Count(e=>e.revision<=1)==3&&real.profileEdits.Count(e=>e.revision==5)==3,"the package carries the four revision 2 mod list moves, the three revision 1 list edits and three revision 5 list edits");
  Check(String.Join("|",real.modMoves.Select(m=>m.mod))=="Local game files|Framework support|WMX - Hide Notes and Uninstall Menu|Performance Tweaks","the moves are listed in the order they must be applied");
  Check(real.modMoves[0].after[0]=="Captured game-folder files"&&real.modMoves[1].after[0]=="Local game files"&&real.modMoves[2].after[0]=="WMX-DLC"&&real.modMoves[3].before[0]=="BUG FIXES_separator"&&real.modMoves[3].sectionEnd=="PERFORMANCE_separator","the moves put Local game files and Framework support after Captured game-folder files, the WMX companion after WMX-DLC and Performance Tweaks at the end of the performance section");
  Check(real.files.All(f=>f.part==null||f.part=="Fallout Mod Setup-17.bin"||f.part=="Fallout Mod Setup-18.bin")&&real.files.All(f=>f.was==null||f.part!=null),"only parts 17 and 18 carry files");
  const string KbTweaks="app/mods/Keybinder - Master Keybind Control/nvse/plugins/tweaks/inis/zzzzKeybinder.ini",KbIni="app/mods/Keybinder Ini/config/keybinder/Keybinder.ini";
  Check(real.files.Count(f=>f.path==KbTweaks||f.path==KbIni)==2&&real.files.Where(f=>f.path==KbTweaks||f.path==KbIni).All(f=>f.part=="Fallout Mod Setup-18.bin"&&f.was!=null&&f.was!=f.hash),"the two Keybinder files (revision 10) are in part 18, each a superseding copy that protects a user's edited file");
  Check(real.files.Count(f=>f.path==KbTweaks||f.path==KbIni)==2&&!real.retired.Any(r=>r.path==KbTweaks||r.path==KbIni)&&real.profileEdits.All(e=>e.revision!=10&&e.revision!=11)&&real.modMoves.All(m=>m.revision<10),"revisions 10 and 11 change no list: no mod list, plugin or load order edit, no move, and the Keybinder files are not retired");
  const string VwIni="app/mods/Vicious Wastes/config/Vicious Wastes.ini",LwtEsp="app/mods/Longer Weather Transitions/Longer Weather Transitions.esp";
  Check(real.files.Single(f=>f.path==LwtEsp).part=="Fallout Mod Setup-18.bin"&&real.files.Single(f=>f.path==LwtEsp).was=="bb90d342895eea06ae11032457cb67d00a96361f9685762a59ba1b75cf9f3334"&&real.files.Single(f=>f.path==LwtEsp).hash=="64c4f3d824118bfc9a99c133c79219fdcc0a3a4f2e9ee0cc6ecabb3d71afa94c"&&real.files.Count(f=>f.path.StartsWith("app/mods/Longer Weather Transitions/"))==2,"revision 13: Longer Weather Transitions.esp is the master's file (fWeatherTransMax 1.25) in part 18, a superseding copy of the previous packaged file; the mod's meta.ini is unchanged");
  Check(real.files.Single(f=>f.path==VwIni).part=="Fallout Mod Setup-18.bin"&&real.files.Single(f=>f.path==VwIni).was!=null&&real.files.Single(f=>f.path==VwIni).was!=real.files.Single(f=>f.path==VwIni).hash,"the user's Vicious Wastes INI is in part 18, a superseding copy that protects a user's edited file");
  Check(real.files.Select(f=>f.path.ToLowerInvariant()).Distinct().Count()==real.files.Length,"no path is listed twice");
  var inventory=new HashSet<string>(real.files.Select(f=>f.path),StringComparer.OrdinalIgnoreCase);
  Check(real.retired.Length>0&&!real.retired.Any(r=>inventory.Contains(r.path))&&real.retired.All(r=>r.path.StartsWith("app/mods/")),"retired files are no longer in the inventory");
  Check(real.files.Where(f=>!f.path.StartsWith("app/")).All(f=>f.part==null),"the files an attached setup is verified against are unchanged");
  Check(real.installedBytes==real.files.Sum(f=>f.size)+real.patches.Sum(p=>p.size),"installed size covers the current inventory");
  foreach(var name in new[]{"Trooper Uncut - SCE - Van Graff Fix","Ownership Fixes and Tweaks","Sep 2026 Loading Crash Fix (Windows 11 KB5124010)"})
   Check(real.files.Any(f=>f.path.StartsWith("app/mods/"+name+"/")),"the package contains the mod "+name);
  Check(real.files.Any(f=>f.path=="app/mods/Sep 2026 Loading Crash Fix (Windows 11 KB5124010)/NVSE/Plugins/Sep2026LoadingCrashFix.dll"),"the Windows 11 loading crash fix plugin is in the package");
  const string StewieIni="app/mods/Stewie Tweaks INI/nvse/plugins/nvse_stewie_tweaks.ini",PerfIni="app/mods/Performance Tweaks/NVSE/Plugins/nvse_stewie_tweaks.ini";
  Check(new[]{"meta.ini","README.txt","NVSE/Plugins/nvse_stewie_tweaks.ini"}.All(n=>real.files.Any(f=>f.path=="app/mods/Performance Tweaks/"+n&&f.part=="Fallout Mod Setup-17.bin"))&&real.files.Count(f=>f.path.StartsWith("app/mods/Performance Tweaks/"))==3,"the Performance Tweaks override mod (three files) is in part 17");
  Check(real.files.Single(f=>f.path==StewieIni).part=="Fallout Mod Setup-17.bin"&&real.files.Single(f=>f.path==StewieIni).was!=null,"the Stewie Tweaks INI is a superseding copy that protects a user's edited file");
  var modEdit=real.profileEdits.Single(e=>e.file=="modlist.txt"&&e.revision<=1);
  Check(modEdit.place.Any(p=>p.line=="+Performance Tweaks"&&p.after[0]=="# This file was automatically generated by Mod Organizer.")&&real.profileEdits.Where(e=>e.file!="modlist.txt").All(e=>!e.place.Any(p=>p.line.Contains("Performance"))),"Performance Tweaks goes first under the modlist header and adds no plugin or load order entry");
  string[] retiredNow={"Close Quarters Combat","Mouse Wheel Weapon Scroll"};
  Check(retiredNow.All(m=>!real.files.Any(f=>f.path.StartsWith("app/mods/"+m+"/",StringComparison.OrdinalIgnoreCase))&&real.retired.Count(r=>r.path.StartsWith("app/mods/"+m+"/",StringComparison.OrdinalIgnoreCase))>=4),"Close Quarters Combat and Mouse Wheel Weapon Scroll are retired: no inventory files, their files listed as retired");
  Check(real.retired.Count(r=>r.path.StartsWith("app/mods/Close Quarters Combat/"))==58&&real.retired.Any(r=>r.path=="app/mods/Close Quarters Combat/CloseQuartersCombat.esp")&&real.retired.Count(r=>r.path.StartsWith("app/mods/Mouse Wheel Weapon Scroll/"))==4,"all 58 files of Close Quarters Combat and the four of the wheel mod are retired");
  var rev5=real.profileEdits.Where(e=>e.revision==5).ToList();
  Check(real.revision>=5&&rev5.Count==3&&rev5.Select(e=>e.file).OrderBy(x=>x).SequenceEqual(new[]{"loadorder.txt","modlist.txt","plugins.txt"})&&rev5.All(e=>e.place.Length==0)&&rev5.Single(e=>e.file=="modlist.txt").remove.Contains("+Close Quarters Combat")&&rev5.Single(e=>e.file=="modlist.txt").remove.Contains("+Mouse Wheel Weapon Scroll")&&rev5.Single(e=>e.file=="plugins.txt").remove.SequenceEqual(new[]{"CloseQuartersCombat.esp"})&&rev5.Single(e=>e.file=="loadorder.txt").remove.SequenceEqual(new[]{"CloseQuartersCombat.esp"}),"revision 5 removes the two mods from the mod list (enabled or not) and the Close Quarters Combat plugin from plugins.txt and loadorder.txt");
  string[] retired6={"Ragdolls"};
  var rev6=real.profileEdits.Where(e=>e.revision==6).ToList();
  Check(real.revision>=6&&rev6.Count==3&&rev6.All(e=>e.place.Length==0)&&rev6.Single(e=>e.file=="modlist.txt").remove.SequenceEqual(new[]{"+Ragdolls","-Ragdolls"})&&rev6.Single(e=>e.file=="plugins.txt").remove.OrderBy(x=>x).SequenceEqual(new[]{"Ragdolls.esm","Vicious Wastes - Ragdolls.esp"})&&rev6.Single(e=>e.file=="loadorder.txt").remove.SequenceEqual(new[]{"Ragdolls.esm"}),"revision 6 removes the Ragdolls mod from the list and its plugin from plugins.txt and loadorder.txt, and deactivates the Vicious Wastes patch that needs it (it stays in the load order, inactive)");
  Check(!real.files.Any(f=>f.path.StartsWith("app/mods/Ragdolls/",StringComparison.OrdinalIgnoreCase))&&real.retired.Count(r=>r.path.StartsWith("app/mods/Ragdolls/"))==94&&real.retired.Any(r=>r.path=="app/mods/Ragdolls/Ragdolls.esm")&&real.files.Any(f=>f.path.StartsWith("app/mods/ED-E Ragdoll Mod Collision Fix/")),"the Ragdolls mod (94 files) is retired; ED-E Ragdoll Mod Collision Fix, which the master still has, stays");
  var rev7=real.profileEdits.Where(e=>e.revision==7).ToList();
  string[] gone7={"Ranger Station Charlie - Southern Mojave Expanded","Simple Water Extinguisher","House and Kings Truce"};
  Check(real.revision>=7&&rev7.Count==3&&rev7.All(e=>e.place.Length==0)&&rev7.Single(e=>e.file=="modlist.txt").remove.OrderBy(x=>x).SequenceEqual(gone7.SelectMany(m=>new[]{"+"+m,"-"+m}).OrderBy(x=>x))&&rev7.Single(e=>e.file=="plugins.txt").remove.OrderBy(x=>x).SequenceEqual(new[]{"House and Kings Truce.esm","SouthernMojaveExpanded_Charlie.esm"})&&rev7.Single(e=>e.file=="loadorder.txt").remove.OrderBy(x=>x).SequenceEqual(new[]{"House and Kings Truce.esm","SouthernMojaveExpanded_Charlie.esm"}),"revision 7 removes three mods from the mod list (enabled or not) and their two plugins from plugins.txt and loadorder.txt");
  Check(gone7.All(m=>!real.files.Any(f=>f.path.StartsWith("app/mods/"+m+"/",StringComparison.OrdinalIgnoreCase)))&&real.retired.Count(r=>r.path.StartsWith("app/mods/Ranger Station Charlie - Southern Mojave Expanded/"))==46&&real.retired.Count(r=>r.path.StartsWith("app/mods/Simple Water Extinguisher/"))==4&&real.retired.Count(r=>r.path.StartsWith("app/mods/House and Kings Truce/"))==2&&real.retired.Any(r=>r.path=="app/mods/House and Kings Truce/House and Kings Truce.esm"),"the three mods are retired (46, 4 and 2 files), none is in the inventory");
  Check(real.languages.Single(l=>l.id=="Russian").retiredPatches.Count(p=>p.target.EndsWith("/SouthernMojaveExpanded_Charlie.esm")||p.target.EndsWith("/House and Kings Truce.esm"))==2&&!real.languages.Single(l=>l.id=="Russian").patches.Any(p=>p.source.Contains("Southern Mojave Expanded")||p.source.Contains("House and Kings Truce")),"the Russian overlays of the two retired plugins are retired with them");
  string[] opt8={"NVAO 10mm Pistol - No Slide Lock","Crash Test - NVTF Texture Locks 1"};
  string[] optAnchor8={"New Vegas Animation Overhaul Guns","NVTF - New Vegas Tick Fix - .INI"};
  string[] optNow={"Crash Test - NVTF Texture Locks 1"},optAnchorNow={"NVTF - New Vegas Tick Fix - .INI"};
  var rev8=real.profileEdits.Where(e=>e.revision==8).ToList();
  Check(real.revision>=8&&rev8.Count(e=>e.file=="modlist.txt")==1&&rev8.Where(e=>e.file!="modlist.txt").All(e=>e.place.Length==0&&e.remove.Length==0)&&rev8.Single(e=>e.file=="modlist.txt").place.Length==2&&rev8.Single(e=>e.file=="modlist.txt").place.All(p=>p.addOnly&&p.line.StartsWith("-")&&p.above!=null)&&rev8.Single(e=>e.file=="modlist.txt").remove.Length==0,"revision 8 adds two DISABLED entries to the mod list, add-only, each placed above the mod whose file it overrides; no plugin or load order entry");
  Check(Enumerable.Range(0,1).All(k=>real.files.Count(f=>f.path.StartsWith("app/mods/"+optNow[k]+"/")&&f.part=="Fallout Mod Setup-18.bin")==3)&&Enumerable.Range(0,1).All(k=>real.files.Where(f=>f.path.StartsWith("app/mods/"+optNow[k]+"/")).Select(f=>f.path.Substring(("app/mods/"+optNow[k]+"/").Length).ToLowerInvariant()).Where(x=>x!="meta.ini"&&x!="readme.txt").Intersect(real.files.Where(f=>f.path.StartsWith("app/mods/"+optAnchorNow[k]+"/")).Select(f=>f.path.Substring(("app/mods/"+optAnchorNow[k]+"/").Length).ToLowerInvariant())).Count()==1),"the remaining optional mod has three files in part 18 and exactly one file at the same path as a file of the mod it overrides");
  Check(real.files.Where(f=>opt8.Any(m=>f.path.StartsWith("app/mods/"+m+"/"))).All(f=>f.was==null)&&real.modMoves.All(m=>!opt8.Contains(m.mod)),"the optional mod is a plain addition: no superseded file, no mod move");
  var p18=real.files.Where(f=>f.part=="Fallout Mod Setup-18.bin").ToList();
  Check(p18.Count==7&&p18.Count(f=>f.path==LwtEsp)==1&&p18.Count(f=>f.path==VwIni)==1&&p18.Count(f=>f.path==KbTweaks||f.path==KbIni)==2&&p18.Count(f=>optNow.Any(m=>f.path.StartsWith("app/mods/"+m+"/")))==3&&p18.Where(f=>f.was!=null).Select(f=>f.path).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(new[]{VwIni,KbTweaks,KbIni,LwtEsp}.OrderBy(x=>x,StringComparer.Ordinal)),"part 18 holds exactly seven files (the Vicious Wastes INI, the three files of the NVTF test mod, the two Keybinder files, Longer Weather Transitions.esp); the four that supersede an older copy keep their previous hash, the three new ones have none");
  string alpha9="Crash Test - Alpha Fixes Particle Patch Off";
  var rev9=real.profileEdits.Where(e=>e.revision==9).ToList();
  Check(real.revision>=9&&rev9.Count==3&&rev9.All(e=>e.place.Length==0)&&rev9.Single(e=>e.file=="modlist.txt").remove.OrderBy(x=>x).SequenceEqual(new[]{"+"+alpha9,"-"+alpha9}.OrderBy(x=>x))&&rev9.Where(e=>e.file!="modlist.txt").All(e=>e.remove.Length==0),"revision 9 removes the Alpha Fixes test mod (enabled or disabled) from the mod list again, no plugin entries");
  Check(!real.files.Any(f=>f.path.StartsWith("app/mods/"+alpha9+"/"))&&real.retired.Count(r=>r.path.StartsWith("app/mods/"+alpha9+"/"))==3&&real.retired.Any(r=>r.path=="app/mods/"+alpha9+"/nvse/plugins/FART.ini"),"the Alpha Fixes test mod is not in the package; its three files (as revision 8 shipped them) are retired so a setup that ran revision 8 loses it");
  var rev12=real.profileEdits.Where(e=>e.revision==12).ToList();
  Check(real.revision>=12&&rev12.Count==1&&rev12[0].file=="modlist.txt"&&rev12[0].place.Length==0&&rev12[0].remove.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(new[]{"+"+opt8[0],"-"+opt8[0]}.OrderBy(x=>x,StringComparer.Ordinal)),"revision 12 removes the NVAO 10mm Pistol - No Slide Lock mod (enabled or disabled) from the mod list; no plugin entry, no other list edit");
  Check(!real.files.Any(f=>f.path.StartsWith("app/mods/"+opt8[0]+"/"))&&real.retired.Count(r=>r.path.StartsWith("app/mods/"+opt8[0]+"/"))==3&&real.retired.Any(r=>r.path=="app/mods/"+opt8[0]+"/Meshes/AnimGroupOverride/zzz_pistol_10.json")&&real.retired.Any(r=>r.path=="app/mods/"+opt8[0]+"/meta.ini"),"the NVAO pistol mod is not in the package (a fresh install does not get it); its three files are retired so an update deletes them while unchanged");
  Check(real.profileEdits.Where(e=>e.revision==8).SelectMany(e=>e.place).Count()==2,"the revision 8 list edit is kept as it was (an update of an older setup still goes through it, then revision 12 removes the pistol mod again)");
  Check(real.files.Any(f=>f.path.StartsWith("app/mods/"+optNow[0]+"/"))&&!real.retired.Any(r=>r.path.StartsWith("app/mods/"+optNow[0]+"/"))&&!rev12.SelectMany(e=>e.remove).Any(x=>x.Contains("NVTF")),"the NVTF Texture Locks test mod stays: files in the package, nothing retired or removed from the list, so an update never disables it");
  var vwItem=real.files.Single(f=>f.path==VwIni);
  Check(vwItem.was=="0fe05b00d5b5189cf731561750fc0b902afc7a1f93a5cf7811730c6ded511d95"&&vwItem.wasAlso!=null&&vwItem.wasAlso.Length==1&&vwItem.part=="Fallout Mod Setup-18.bin"&&vwItem.hash!=vwItem.was&&vwItem.size==35677,"the Vicious Wastes INI (revision 12) is a superseding copy: was = the revision 11 packaged file, wasAlso = the original file of the oldest setups; a user-edited copy is kept");  Check(!real.profileEdits.SelectMany(e=>e.place).Any(p=>p.line.Contains("Alpha Fixes"))&&real.files.Count(f=>f.path.StartsWith("app/mods/Fallout Alpha Rendering Tweaks - NVSE/"))>0,"no list edit adds the Alpha Fixes test mod, and the Fallout Alpha Rendering Tweaks - NVSE mod itself is unchanged in the pack");
  Check(!real.languages.Single(l=>l.id=="Russian").patches.Any(p=>p.source.Contains("Ragdolls/"))&&real.languages.Single(l=>l.id=="Russian").retiredPatches.Any(p=>p.target.EndsWith("/Ragdolls.esm")),"the Russian overlay of Ragdolls.esm is retired with it");
  Check(real.profileEdits.All(e=>e.revision!=3)&&real.modMoves.All(m=>m.revision!=3)&&!real.files.Any(f=>f.path.StartsWith("app/mods/Mouse Wheel Weapon Scroll/")),"the revision 3 edit and move and the wheel mod (which used the old part 18) are gone from the package; part 18 is the new merged update part");
  Check(real.iniEdits!=null&&real.iniEdits.Length==3&&real.iniEdits.All(e=>e.revision==5)&&String.Join("|",real.iniEdits.Select(e=>e.file+"/"+e.section+"/"+e.key+": "+e.was+"->"+e.value))=="falloutprefs.ini/Display/iMaxAnisotropy: 15->8|falloutprefs.ini/Display/iWaterMultisamples: 4->2|falloutcustom.ini/Audio/bMultiThreadAudio: 1->0","revision 5 changes exactly three profile INI keys (master values): anisotropy 15 to 8, water multisamples 4 to 2, multi-thread audio 1 to 0");
  Check(!real.files.Any(f=>f.path.StartsWith("app/mods/Freeside Power Dilemma/")||f.path.StartsWith("app/mods/Unofficial Patch NVSE Plus - Grey Area Fixes/")),"the two dropped mods are no longer in the package");
  var ru=real.languages.Single(l=>l.id=="Russian");
  Check(ru.patches.Where(p=>p.sourceKind=="app").All(p=>{var f=real.files.FirstOrDefault(x=>x.path=="app/"+p.source);var t=real.patches.FirstOrDefault(x=>x.target=="app/"+p.source);return (f!=null&&f.hash==p.sourceHash)||(t!=null&&t.targetHash==p.sourceHash);}),"every Russian overlay is built from a plugin whose packaged bytes match its recorded source");
  Check(ru.retiredPatches.Length==5&&ru.retiredPatches.All(p=>!ru.patches.Any(q=>q.target==p.target)),"the overlays of the retired and the updated plugin are retired together");

  // --- Real data parts (only when they sit beside the checks) ---
  if(dist!=null&&File.Exists(Path.Combine(dist,"Fallout Mod Setup-17.bin"))) {
   var part=real.archives[16];string path=Path.Combine(dist,part.name);
   Check(new FileInfo(path).Length==part.size&&Engine.Hash(path)==part.hash,"data part 17 matches its size and hash in the package");
   var wantedAll=real.files.Where(f=>f.part!=null).ToDictionary(f=>f.path);var wanted=real.files.Where(f=>f.part=="Fallout Mod Setup-17.bin").ToDictionary(f=>f.path);
   using(var zip=ZipFile.OpenRead(path)) {
    Check(zip.Entries.Count==wanted.Count&&zip.Entries.All(e=>wanted.ContainsKey(e.FullName)),"part 17 holds exactly the files the inventory assigns to it");
    bool ok=true;foreach(var e in zip.Entries){var f=wanted[e.FullName];using(var s=e.Open())using(var h=SHA256.Create())ok&=e.Length==f.size&&BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant()==f.hash;}
    Check(ok,"every file in part 17 has the size and SHA-256 of its inventory entry");
   }
   var part18=real.archives[17];string path18=Path.Combine(dist,part18.name);
   Check(part18.name=="Fallout Mod Setup-18.bin"&&new FileInfo(path18).Length==part18.size&&Engine.Hash(path18)==part18.hash,"data part 18 matches its size and hash in the package");
   var wanted18=real.files.Where(f=>f.part=="Fallout Mod Setup-18.bin").ToDictionary(f=>f.path);
   using(var zip=ZipFile.OpenRead(path18)) {
    Check(zip.Entries.Count==wanted18.Count&&zip.Entries.All(e=>wanted18.ContainsKey(e.FullName)),"part 18 holds exactly the seven files the inventory assigns to it");
    bool ok18=true;foreach(var e in zip.Entries){var f=wanted18[e.FullName];using(var s=e.Open())using(var h=SHA256.Create())ok18&=e.Length==f.size&&BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant()==f.hash;}
    Check(ok18,"every file in part 18 has the size and SHA-256 of its inventory entry");
   }
   var old=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var oldParts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   for(int i=1;i<=16;i++){string n="Fallout Mod Setup-"+i.ToString("00")+".bin";using(var zip=ZipFile.OpenRead(Path.Combine(dist,n)))foreach(var e in zip.Entries){old.Add(e.FullName);oldParts[e.FullName]=n;}}
   var retiredSet=new HashSet<string>(real.retired.Select(r=>r.path),StringComparer.OrdinalIgnoreCase);
   Check(retiredSet.All(x=>old.Contains(x)||x.StartsWith("app/mods/Mouse Wheel Weapon Scroll/")||x.StartsWith("app/mods/Crash Test - Alpha Fixes Particle Patch Off/")||x.StartsWith("app/mods/"+opt8[0]+"/")),"every retired file exists in the original parts (the wheel mod was only ever in the dropped part 18, the Alpha Fixes test mod only in the unpublished revision 8)");
   var fromOld=new HashSet<string>(old.Where(x=>!retiredSet.Contains(x)&&!wantedAll.ContainsKey(x)),StringComparer.OrdinalIgnoreCase);
   var expectedOld=new HashSet<string>(real.files.Where(f=>f.part==null).Select(f=>f.path),StringComparer.OrdinalIgnoreCase);
   Check(fromOld.SetEquals(expectedOld),"the original parts, minus retired and superseded files, are exactly the unchanged inventory ("+expectedOld.Count+" files)");
   Check(wantedAll.Values.Where(f=>f.was!=null).All(f=>old.Contains(f.path)),"every superseded file exists in the original parts");
   Check(real.files.All(f=>f.part!=null?(f.part=="Fallout Mod Setup-17.bin"||f.part=="Fallout Mod Setup-18.bin")&&File.Exists(Path.Combine(dist,f.part)):old.Contains(f.path)),"every inventory file maps to a part that exists: update-part files to part 17 or 18, all others to one of the original parts 01-16");
   // The profile lists.
   Func<string,string[]> oldList=name=>{using(var zip=ZipFile.OpenRead(Path.Combine(dist,oldParts["app/profiles/Default/"+name])))using(var r=new StreamReader(zip.GetEntry("app/profiles/Default/"+name).Open(),new UTF8Encoding(false)))return r.ReadToEnd().Replace("\r","").TrimEnd('\n').Split('\n');};
   var lists=new Dictionary<string,List<string>>();bool digestsOk=true;foreach(var e in real.profileEdits){List<string> cur;if(!lists.TryGetValue(e.file,out cur))cur=oldList(e.file).ToList();cur=ListPatch.Apply(cur,e);digestsOk&=ListPatch.Digest(cur)==e.result;lists[e.file]=cur;}
   Check(digestsOk,"applying the packaged edits in order to the original lists gives the expected lists at every step");
   var modlist=lists["modlist.txt"];var plugins=lists["plugins.txt"];var order=lists["loadorder.txt"];
   Check(modlist.First(l=>!l.StartsWith("#"))=="+Performance Tweaks","the revision 1 list edit puts Performance Tweaks first (the revision 2 move then places it)");
   Check(Enumerable.Range(0,1).All(k=>{int a=modlist.IndexOf("+"+optAnchorNow[k]);return a>0&&modlist[a-1]=="-"+optNow[k]&&!modlist.Contains("+"+optNow[k])&&modlist.Count(x=>x.Length>1&&x.Substring(1)==optNow[k])==1;})&&!modlist.Any(x=>x.Length>1&&x.Substring(1)==opt8[0]),"in the real list the remaining optional mod is one DISABLED entry directly above the mod whose file it overrides (higher priority), none is enabled");
   Check(modlist.Where(x=>x.StartsWith("-")&&!x.EndsWith("_separator")).SequenceEqual(new[]{"-"+optNow[0]}),"the only disabled mod the package leaves in the list is the optional NVTF test mod");
   // Revision 2 on a fresh installation: the edited list, then what PreparePluginOrder adds at the top, then the moves.
   var before2=new List<string>{modlist[0],"+Framework support","+Local game files","+WMX - Hide Notes and Uninstall Menu"};before2.AddRange(modlist.Skip(1));
   var after2=new List<string>(before2);int movedReal=ModlistMoves.Apply(after2,real.modMoves,0);
   Func<List<string>,string,int> at=(l,n)=>l.FindIndex(x=>x.Length>1&&x.Substring(1)==n);
   Check(movedReal==4&&after2.Count==before2.Count&&after2.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(before2.OrderBy(x=>x,StringComparer.Ordinal)),"the real list after the moves has the same entries, four of them moved");
   Check(after2[at(after2,"Framework support")+1]=="+Local game files"&&after2[at(after2,"Local game files")+1]=="+Captured game-folder files"&&after2[at(after2,"Framework support")-1]=="-BASE_separator"&&after2.Last()=="+Captured game-folder files","Framework support and Local game files sit directly above Captured game-folder files (below it in the pane), under BASE_separator");
   Check(after2[at(after2,"WMX-DLC")-1]=="+WMX - Hide Notes and Uninstall Menu","the WMX companion sits directly above WMX-DLC (below it in the pane)");
   int perfAt=at(after2,"Performance Tweaks"),perfSep=at(after2,"PERFORMANCE_separator");
   Check(after2[at(after2,"BUG FIXES_separator")+1]=="+Performance Tweaks"&&perfAt<perfSep&&after2.Skip(perfAt+1).Take(perfSep-perfAt-1).All(l=>!l.EndsWith("_separator"))&&perfSep-perfAt>8,"Performance Tweaks is the last entry of the PERFORMANCE section (directly below BUG FIXES_separator in the file, above the nine other performance mods)");
   var moversReal=new[]{"Framework support","Local game files","WMX - Hide Notes and Uninstall Menu","Performance Tweaks"};
   Check(after2.Where(l=>l.Length>1&&!moversReal.Contains(l.Substring(1))).SequenceEqual(before2.Where(l=>l.Length>1&&!moversReal.Contains(l.Substring(1)))),"the other 340-odd entries keep their relative order");
   // Moving a mod in priority must not change which mod wins a loose file. Winner = the enabled mod that comes first in the file.
   var owners=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
   foreach(var f in real.files){var parts=f.path.Split('/');if(parts.Length<4||parts[0]!="app"||parts[1]!="mods")continue;string rel=String.Join("/",parts.Skip(3));if(rel.Equals("meta.ini",StringComparison.OrdinalIgnoreCase))continue;List<string> o;if(!owners.TryGetValue(rel,out o))owners[rel]=o=new List<string>();o.Add(parts[2]);}
   Func<List<string>,Dictionary<string,int>> rank=l=>{var d=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);for(int i=0;i<l.Count;i++)if(l[i].StartsWith("+"))d[l[i].Substring(1)]=i;return d;};
   var rankBefore=rank(before2);var rankAfter=rank(after2);
   var flipped=owners.Where(kv=>kv.Value.Count>1).Where(kv=>{var live=kv.Value.Where(rankBefore.ContainsKey).ToList();return live.Count>1&&live.OrderBy(m=>rankBefore[m]).First()!=live.OrderBy(m=>rankAfter[m]).First();}).Select(kv=>kv.Key).ToList();
   Check(flipped.All(k=>k.EndsWith("readme.txt",StringComparison.OrdinalIgnoreCase)),"no loose file changes its winner because of the moves, except readme.txt"+(flipped.Count>0?" ("+flipped.Count+" readme files): "+String.Join("; ",flipped.Take(5)):""));
   var stewie=owners["NVSE/Plugins/nvse_stewie_tweaks.ini"];
   Check(stewie.Count==2&&rankAfter["Performance Tweaks"]<rankAfter["Stewie Tweaks INI"],"Performance Tweaks still overrides Stewie Tweaks INI for nvse_stewie_tweaks.ini after the move");
   Check(owners.Where(kv=>kv.Value.Contains("Framework support")||kv.Value.Contains("WMX - Hide Notes and Uninstall Menu")).All(kv=>kv.Value.Count==1||kv.Key.EndsWith("readme.txt",StringComparison.OrdinalIgnoreCase)),"Framework support and the WMX companion share no loose file with another mod (only a readme)");
   Func<string,byte[]> fromPart=entry=>{using(var zip=ZipFile.OpenRead(path))using(var s=zip.GetEntry(entry).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);return ms.ToArray();}};
   Func<byte[],string[]> linesOf=b=>Encoding.GetEncoding(28591).GetString(b).Split('\n');
   byte[] oldStewie;using(var zip=ZipFile.OpenRead(Path.Combine(dist,oldParts[StewieIni])))using(var s=zip.GetEntry(StewieIni).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);oldStewie=ms.ToArray();}
   Func<byte[],byte[],string> diff=(a,b)=>{var x=linesOf(a);var y=linesOf(b);return x.Length!=y.Length?"length":String.Join(" | ",Enumerable.Range(0,x.Length).Where(i=>x[i]!=y[i]).Select(i=>x[i].Trim()+" -> "+y[i].Trim()));};
   Check(diff(oldStewie,fromPart(StewieIni))=="bNumberedDialogHotkeys = 0 -> bNumberedDialogHotkeys = 1","the Stewie Tweaks INI differs from the original in exactly the numbered dialogue hotkeys line");
   string perfDiff=diff(oldStewie,fromPart(PerfIni));
   Check(perfDiff=="bNumberedDialogHotkeys = 0 -> bNumberedDialogHotkeys = 1 | bGeneralErrors = 1 -> bGeneralErrors = 0","the Performance Tweaks INI differs from the original in exactly the hotkeys and the vanilla error log lines");
   if(File.Exists(Path.Combine(dist,"Fallout Mod Setup-18.bin"))) {
    byte[] oldVw;using(var zip=ZipFile.OpenRead(Path.Combine(dist,oldParts[VwIni])))using(var s=zip.GetEntry(VwIni).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);oldVw=ms.ToArray();}
    byte[] newVw;using(var zip=ZipFile.OpenRead(Path.Combine(dist,"Fallout Mod Setup-18.bin")))using(var s=zip.GetEntry(VwIni).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);newVw=ms.ToArray();}
    Check(BitConverter.ToString(SHA256.Create().ComputeHash(oldVw)).Replace("-","").ToLowerInvariant()==real.files.Single(f=>f.path==VwIni).wasAlso[0],"wasAlso of the Vicious Wastes INI is the original file in the frozen parts (setups that were never updated)");
    Check(diff(oldVw,newVw)=="bCustomGameSettings=0 -> bCustomGameSettings=1 | fHCDehydrationRate=5 -> fHCDehydrationRate=7.5 | fHCDehydrationRate=5 -> fHCDehydrationRate=7.5 | fHCStarvationRate=12 -> fHCStarvationRate=18.5 | fHCSleepDeprivationRate=18 -> fHCSleepDeprivationRate=34","the Vicious Wastes INI differs from the original in exactly the custom game settings line and the dehydration (both lines), starvation and sleep deprivation rates (7.5, 18.5, 34)");
     Check(BitConverter.ToString(SHA256.Create().ComputeHash(newVw)).Replace("-","").ToLowerInvariant()=="a75c93ec48375a09e8eb51b67b88955f747dbc74bea5e69e431212309e2358df","the packaged Vicious Wastes INI is the master's file (SHA-256 of E:\\low-req_games\\fnv_mods\\mods\\Vicious Wastes\\config\\Vicious Wastes.ini)");
    string vw=Encoding.GetEncoding(28591).GetString(newVw);
    Check(vw.Contains("\nbToxicWaste=1")&&vw.Contains("\nbDeadlyRads=1")&&vw.Contains("[MCM]")&&vw.Contains("\nfWadeRadiationDamageMult=0.5")&&vw.Contains("\nfSwimRadiationDamageMult=0.75"),"the Vicious Wastes INI keeps the toxic waste and deadly radiation switches on and the packaged radiation game settings");
   }
    {
     byte[] lwtOld;using(var zip=ZipFile.OpenRead(Path.Combine(dist,oldParts[LwtEsp])))using(var s=zip.GetEntry(LwtEsp).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);lwtOld=ms.ToArray();}
     byte[] lwtNew;using(var zip=ZipFile.OpenRead(Path.Combine(dist,"Fallout Mod Setup-18.bin")))using(var s=zip.GetEntry(LwtEsp).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);lwtNew=ms.ToArray();}
     var lwtDiff=Enumerable.Range(0,Math.Min(lwtOld.Length,lwtNew.Length)).Where(k=>lwtOld[k]!=lwtNew[k]).ToList();
     Check(BitConverter.ToString(SHA256.Create().ComputeHash(lwtOld)).Replace("-","").ToLowerInvariant()==real.files.Single(f=>f.path==LwtEsp).was&&lwtOld.Length==lwtNew.Length&&lwtDiff.Count==1&&BitConverter.ToSingle(lwtOld,lwtDiff[0]-3)==5.0f&&BitConverter.ToSingle(lwtNew,lwtDiff[0]-3)==1.25f,"Longer Weather Transitions.esp differs from the original in exactly one byte: the float 5.0 became 1.25 (was = the original file)");
    }
   if(File.Exists(Path.Combine(dist,"Fallout Mod Setup-18.bin"))) {
    Func<string,byte[]> kbOld=e=>{using(var zip=ZipFile.OpenRead(Path.Combine(dist,oldParts[e])))using(var s=zip.GetEntry(e).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);return ms.ToArray();}};
    Func<string,byte[]> kbNew=e=>{using(var zip=ZipFile.OpenRead(Path.Combine(dist,"Fallout Mod Setup-18.bin")))using(var s=zip.GetEntry(e).Open())using(var ms=new MemoryStream()){s.CopyTo(ms);return ms.ToArray();}};
    Func<byte[],string> sha=b=>BitConverter.ToString(SHA256.Create().ComputeHash(b)).Replace("-","").ToLowerInvariant();
    Check(sha(kbOld(KbTweaks))==real.files.Single(f=>f.path==KbTweaks).was&&sha(kbOld(KbIni))==real.files.Single(f=>f.path==KbIni).was,"the previous hashes recorded for the two Keybinder files are the frozen parts' files");
    Check(diff(kbOld(KbTweaks),kbNew(KbTweaks))=="iSecondSlotKey = 3 -> iSecondSlotKey = 0 | bRestore2Hotkey = 1 -> bRestore2Hotkey = 0","the Keybinder tweaks INI differs from the pack's previous copy in exactly two lines: the second weapon slot key off and 'restore key 2' off");
    Check(diff(kbOld(KbIni),kbNew(KbIni))=="18 = 0. -> 18 = 3.","Keybinder.ini differs from the pack's previous copy in exactly one line: vanilla Hotkey 2 (control 18) is the 2 key again");
    string kbText=Encoding.GetEncoding(28591).GetString(kbNew(KbIni));
    Check(kbText.Contains("\r\n;; Hotkey 2\r\n18 = 3.\r\n")&&kbText.Contains("\r\n17 = 2.\r\n")&&kbText.Contains("\r\n19 = 4.\r\n")&&kbText.Contains("\r\n0 = 17.\r\n")&&kbText.Contains("\r\n27 = 44.\r\n"),"in Keybinder.ini the key 2 entry sits between hotkey 1 (key 1) and hotkey 3 (key 3) and the user's other bindings are untouched");
    string kbTw=Encoding.GetEncoding(28591).GetString(kbNew(KbTweaks));
    Check(kbTw.Contains("\r\niSecondSlotKey = 0\r\n")&&kbTw.Contains("\r\nbRestore2Hotkey = 0\r\n")&&!kbTw.Contains("bRestore2Hotkey = 1")&&!kbTw.Contains("iSecondSlotKey = 3"),"the Keybinder tweaks INI no longer asks Stewie Tweaks to restore key 2 (the value that overrode the Stewie INI is off)");
   }
   Check(new[]{"+Trooper Uncut - SCE - Van Graff Fix","+Ownership Fixes and Tweaks","+Sep 2026 Loading Crash Fix (Windows 11 KB5124010)"}.All(modlist.Contains)&&!modlist.Contains("+Freeside Power Dilemma")&&!modlist.Contains("+Unofficial Patch NVSE Plus - Grey Area Fixes"),"the mod list has the three new mods and not the two dropped ones");
   Check(plugins.Contains("d20Ownership.esm")&&plugins.Contains("Trooper Uncut - SCE - Van Graff Fix.esp")&&!plugins.Contains("FreesidePowerDilemma.esm")&&!plugins.Contains("Unofficial Patch NVSE Plus - Grey Area Fixes.esm"),"the active plugins match: new plugins in, dropped plugins out");
   var orderSet=new HashSet<string>(order,StringComparer.OrdinalIgnoreCase);
   Check(plugins.Where(p=>p.Length>0&&!p.StartsWith("#")).All(orderSet.Contains)&&order.Distinct(StringComparer.OrdinalIgnoreCase).Count()==order.Count&&plugins.Distinct(StringComparer.OrdinalIgnoreCase).Count()==plugins.Count,"every active plugin is in the load order, without duplicates");
   Check(plugins.Count(p=>p.Length>0&&!p.StartsWith("#"))==real.plugins&&modlist.Count(l=>l.StartsWith("+")&&!l.EndsWith("_separator"))==real.activeMods,"the plugin and mod counts in the package follow the lists");
   var enabled=modlist.Where(l=>l.StartsWith("+")&&!l.EndsWith("_separator")).Select(l=>l.Substring(1)).ToList();
   var withFiles=new HashSet<string>(real.files.Where(f=>f.path.StartsWith("app/mods/")).Select(f=>f.path.Split('/')[2]),StringComparer.OrdinalIgnoreCase);
   var noFolder=enabled.Where(m=>!withFiles.Contains(m)&&m!="Fixed ESMs").ToList();
   Check(noFolder.Count==0,"every enabled mod has files in the package"+(noFolder.Count>0?": "+String.Join("; ",noFolder):""));
   var providers=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var f in real.files){var p=f.path.Split('/');if(p.Length==4&&p[0]=="app"&&p[1]=="mods"&&enabled.Contains(p[2])&&(p[3].EndsWith(".esm",StringComparison.OrdinalIgnoreCase)||p[3].EndsWith(".esp",StringComparison.OrdinalIgnoreCase)))providers.Add(p[3]);}
   foreach(var p in real.patches)providers.Add(Path.GetFileName(p.target));foreach(var m in real.masters)providers.Add(Path.GetFileName(m.name));providers.Add("FalloutNV_lang.esp");
   var orphans=plugins.Where(p=>p.Length>0&&!p.StartsWith("#")&&!providers.Contains(p)).ToList();
   Check(orphans.Count==0,"every active plugin is provided by an enabled mod, a game master or a Fixed ESM"+(orphans.Count>0?": "+String.Join("; ",orphans):""));
   // No active plugin may need a master that the package no longer has: read the TES4 header of every active plugin of the enabled mods from the data parts.
   var provFile=new Dictionary<string,FileItem>(StringComparer.OrdinalIgnoreCase);
   foreach(var f in real.files){var p=f.path.Split('/');if(p.Length==4&&p[0]=="app"&&p[1]=="mods"&&enabled.Contains(p[2])&&(p[3].EndsWith(".esm",StringComparison.OrdinalIgnoreCase)||p[3].EndsWith(".esp",StringComparison.OrdinalIgnoreCase)))provFile[p[3]]=f;}
   var activeSet=new HashSet<string>(plugins.Where(p=>p.Length>0&&!p.StartsWith("#")),StringComparer.OrdinalIgnoreCase);int headersRead=0;var missingMasters=new List<string>();
   foreach(var name in activeSet){FileItem pf;if(!provFile.TryGetValue(name,out pf))continue;
    string binPath=Path.Combine(dist,pf.part!=null?pf.part:oldParts[pf.path]);byte[] head;
    using(var zip=ZipFile.OpenRead(binPath))using(var s=zip.GetEntry(pf.path).Open()){var h24=new byte[24];int got=0,r;while(got<24&&(r=s.Read(h24,got,24-got))>0)got+=r;int size=BitConverter.ToInt32(h24,4);head=new byte[size];got=0;while(got<size&&(r=s.Read(head,got,size-got))>0)got+=r;}
    headersRead++;int hp=0;while(hp+6<=head.Length){string tag=Encoding.ASCII.GetString(head,hp,4);int len=BitConverter.ToUInt16(head,hp+4);hp+=6;if(tag=="MAST"){string master=Encoding.GetEncoding(28591).GetString(head,hp,Math.Max(0,len-1));if(!activeSet.Contains(master))missingMasters.Add(name+" needs "+master);}hp+=len;}}
   Check(headersRead>100&&missingMasters.Count==0,"every master of every active plugin is itself an active plugin ("+headersRead+" plugin headers read from the data parts)"+(missingMasters.Count>0?": "+String.Join("; ",missingMasters):""));
  }
  Console.WriteLine(count+" checks");
 }
}
