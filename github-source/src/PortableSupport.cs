using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FalloutModSetup {
public class GameCandidate {
    public string Path, Store, Source; public bool Supported;
    public override string ToString(){return (Store??"Unknown store")+": "+Path;}
}
// Every source is replaceable so the finder can be tested without touching the real registry or drives.
public class GameSources {
    public Func<IEnumerable<string>> SteamLibraries=GameLocator.RegistrySteamLibraries;
    public Func<IEnumerable<string>> Gog=GameLocator.RegistryGogFolders;
    public Func<IEnumerable<string>> Epic=delegate{return GameLocator.EpicManifestFolders(null);};
    public Func<IEnumerable<string>> Bethesda=GameLocator.RegistryBethesdaFolders;
    public Func<IEnumerable<string>> DriveRoots=GameLocator.FixedDriveRoots;
}

public static class GameLocator {
    static readonly string[] SteamAppIds={"22380","22490"};
    static readonly string[] ScanParents={"","Games","GOG Games","Epic Games","SteamLibrary\\steamapps\\common","Steam\\steamapps\\common","Program Files (x86)\\Steam\\steamapps\\common","Program Files\\Steam\\steamapps\\common","Program Files (x86)\\GOG Galaxy\\Games","Program Files (x86)\\Epic Games","Program Files\\Epic Games"};
    public static bool IsGameFolder(string p) {
        try{return !String.IsNullOrWhiteSpace(p)&&File.Exists(Path.Combine(p,"FalloutNV.exe"))&&File.Exists(Path.Combine(p,"FalloutNVLauncher.exe"));}catch{return false;}
    }
    public static bool SupportedExe(string game) {
        try{var v=FileVersionInfo.GetVersionInfo(Path.Combine(game,"FalloutNV.exe"));return v.FileMajorPart==1&&v.FileMinorPart==4&&v.FileBuildPart==0&&v.FilePrivatePart==525;}catch{return false;}
    }
    // Store files are the most reliable hint: .egstore (Epic), goggame-*.info (GOG), steamapps path or steam_api.dll (Steam).
    public static string StoreFromFolder(string game) {
        try {
            if(String.IsNullOrWhiteSpace(game)||!Directory.Exists(game))return null;
            if(Directory.Exists(Path.Combine(game,".egstore")))return "Epic";
            if(Directory.EnumerateFiles(game,"goggame-*.info").Any())return "GOG";
            if(Regex.IsMatch(game,@"[\\/]steamapps[\\/]common[\\/]",RegexOptions.IgnoreCase)||File.Exists(Path.Combine(game,"steam_api.dll")))return "Steam";
        }catch{}
        return null;
    }
    public static List<GameCandidate> Find(GameSources s=null) {
        s=s??new GameSources();
        var found=new List<GameCandidate>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Action<string,string,string> add=delegate(string path,string store,string source) {
            if(String.IsNullOrWhiteSpace(path))return;
            string full;try{full=Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\','/');}catch{return;}
            if(!IsGameFolder(full)||!seen.Add(full))return;
            found.Add(new GameCandidate{Path=full,Store=store??StoreFromFolder(full),Source=source,Supported=SupportedExe(full)});
        };
        Action<string,Func<IEnumerable<string>>,string> each=delegate(string store,Func<IEnumerable<string>> source,string name){
            try{foreach(var p in source())add(p,store,name);}catch{}
        };
        try {
            foreach(var library in s.SteamLibraries()) {
                string apps=Path.Combine(library,"steamapps");
                foreach(var id in SteamAppIds) {
                    string manifest=Path.Combine(apps,"appmanifest_"+id+".acf");
                    try{if(File.Exists(manifest)){var m=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s+\"([^\"]+)\"",RegexOptions.IgnoreCase);if(m.Success)add(Path.Combine(apps,"common",m.Groups[1].Value),"Steam","Steam library");}}catch{}
                }
                try{string common=Path.Combine(apps,"common");if(Directory.Exists(common))foreach(var d in Directory.EnumerateDirectories(common))if(LooksLikeGameName(Path.GetFileName(d)))add(d,"Steam","Steam library");}catch{}
            }
        }catch{}
        each("GOG",s.Gog,"GOG Galaxy");
        each("Epic",s.Epic,"Epic Games Launcher");
        each(null,s.Bethesda,"Bethesda registry");
        try {
            foreach(var root in s.DriveRoots())foreach(var parent in ScanParents) {
                string dir=Path.Combine(root,parent);
                try{if(Directory.Exists(dir))foreach(var d in Directory.EnumerateDirectories(dir))if(LooksLikeGameName(Path.GetFileName(d)))add(d,null,"Common folder");}catch{}
            }
        }catch{}
        // Supported 1.4.0.525 copies first; discovery order breaks ties (store libraries before folder scans).
        return found.Select((c,i)=>new{c,i}).OrderByDescending(x=>x.c.Supported).ThenBy(x=>x.i).Select(x=>x.c).ToList();
    }
    public static bool LooksLikeGameName(string name){return name!=null&&Regex.IsMatch(name,@"^Fallout\W{0,3}New\W{0,3}Vegas",RegexOptions.IgnoreCase);}
    public static IEnumerable<string> SteamLibraryFolders(IEnumerable<string> steamRoots) {
        var list=new List<string>();
        foreach(var r in steamRoots) {
            string root;try{root=Path.GetFullPath(r.Replace('/','\\'));}catch{continue;}
            list.Add(root);
            try {
                string vdf=Path.Combine(root,"steamapps","libraryfolders.vdf");
                if(!File.Exists(vdf))continue;
                string text=File.ReadAllText(vdf);
                // New format: "path" "D:\\Library". Old format: "1" "D:\\Library".
                foreach(Match m in Regex.Matches(text,"\"path\"\\s+\"([^\"]+)\"",RegexOptions.IgnoreCase))list.Add(m.Groups[1].Value.Replace("\\\\","\\"));
                foreach(Match m in Regex.Matches(text,"\"\\d+\"\\s+\"([A-Za-z]:[^\"]*)\""))list.Add(m.Groups[1].Value.Replace("\\\\","\\"));
            }catch{}
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static IEnumerable<string> RegistrySteamLibraries() {
        var roots=new List<string>();
        try{using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")){var v=k==null?null:k.GetValue("SteamPath") as string;if(v!=null)roots.Add(v);}}catch{}
        foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
            try{using(var h=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var k=h.OpenSubKey(@"SOFTWARE\Valve\Steam")){var v=k==null?null:k.GetValue("InstallPath") as string;if(v!=null)roots.Add(v);}}catch{}
        return SteamLibraryFolders(roots);
    }
    public static IEnumerable<string> RegistryGogFolders() {
        var list=new List<string>();
        foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
            try {
                using(var h=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var games=h.OpenSubKey(@"SOFTWARE\GOG.com\Games")) {
                    if(games==null)continue;
                    foreach(var id in games.GetSubKeyNames())using(var g=games.OpenSubKey(id)) {
                        string path=g==null?null:g.GetValue("PATH") as string,name=g==null?null:g.GetValue("gameName") as string;
                        if(path!=null&&(LooksLikeGameName(name==null?null:name.Replace(":","").Replace("-","").Replace("  "," "))||IsGameFolder(path)))list.Add(path);
                    }
                }
            }catch{}
        return list;
    }
    public static IEnumerable<string> EpicManifestFolders(string manifests) {
        var list=new List<string>();
        manifests=manifests??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"Epic\EpicGamesLauncher\Data\Manifests");
        try {
            if(!Directory.Exists(manifests))return list;
            foreach(var file in Directory.EnumerateFiles(manifests,"*.item"))
                try {
                    var item=Engine.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
                    object name,location;
                    if(item.TryGetValue("DisplayName",out name)&&item.TryGetValue("InstallLocation",out location)&&Regex.IsMatch(Convert.ToString(name),"New Vegas",RegexOptions.IgnoreCase))list.Add(Convert.ToString(location));
                }catch{}
        }catch{}
        return list;
    }
    public static IEnumerable<string> RegistryBethesdaFolders() {
        var list=new List<string>();
        foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
            try{using(var h=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var k=h.OpenSubKey(@"SOFTWARE\Bethesda Softworks\FalloutNV")){var p=k==null?null:k.GetValue("Installed Path") as string;if(p!=null)list.Add(p);}}catch{}
        return list;
    }
    public static IEnumerable<string> FixedDriveRoots() {
        var list=new List<string>();
        try{foreach(var d in DriveInfo.GetDrives())if(d.DriveType==DriveType.Fixed&&d.IsReady)list.Add(d.RootDirectory.FullName);}catch{}
        return list;
    }
}

public class RelocationPlan {
    public string Destination,OldDestination,Game,OldGame;public InstallState State;public bool DestinationMoved,GameMissing;
    public bool NeedsWork{get{return DestinationMoved||GameMissing;}}
}

// A finished setup records absolute paths. These routines rewrite them after the mod or game folder was moved.
public static class PortableSupport {
    public const string RepairCommand="Move or repair this setup.cmd";
    public static string RepairCommandText(){
        return "@echo off\r\nrem Run this after moving the mod folder or the game folder. It repoints MO2, the shortcuts and the uninstall record.\r\n\"%~dp0_setup\\Setup and Restore.exe\" --relocate --destination \"%~dp0.\"\r\n";
    }
    // The helper lives in <mod folder>\_setup. Returns that mod folder, or null for an installer running from elsewhere.
    public static string InstalledFolderOf(string exe) {
        try {
            string dir=Path.GetDirectoryName(exe);
            if(dir==null||!Path.GetFileName(dir).Equals("_setup",StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(dir,"state.json")))return null;
            return Directory.GetParent(dir).FullName;
        }catch{return null;}
    }
    public static RelocationPlan Inspect(string destination) {
        string dest=Engine.Full(destination),p=Engine.SafeJoin(dest,"_setup/state.json");
        if(!File.Exists(p))throw new Exception("This folder has no installer record. Select the mod folder that contains _setup.");
        var s=Engine.Json().Deserialize<InstallState>(File.ReadAllText(p,Engine.Utf8));
        if(s.status!="installed")throw new Exception("This installation is not active. Only a finished installation can be moved or repaired.");
        var plan=new RelocationPlan{Destination=dest,State=s,OldDestination=s.destination,OldGame=s.game};
        try{plan.DestinationMoved=!String.Equals(Engine.Full(s.destination),dest,StringComparison.OrdinalIgnoreCase);}catch{plan.DestinationMoved=true;}
        plan.GameMissing=!GameLocator.IsGameFolder(s.game);
        plan.Game=s.game;return plan;
    }
    // A game folder belongs to this setup when its FalloutNV.exe is the exact patched file the installer wrote.
    public static bool OwnsGame(InstallState s,string game) {
        string exe=Path.Combine(game,"FalloutNV.exe");
        if(!File.Exists(exe)||!Engine.HasLoaderPatch(exe))return false;
        var change=s.changes.FirstOrDefault(c=>c.path=="FalloutNV.exe");
        return change==null||String.IsNullOrEmpty(change.after)||Engine.Hash(exe)==change.after;
    }
    // Candidate game folders that this setup can use right now.
    public static List<string> MatchingGames(InstallState s,GameSources sources=null) {
        return GameLocator.Find(sources).Where(c=>OwnsGame(s,c.Path)).Select(c=>c.Path).ToList();
    }

    static string PathPattern(string path) {
        // Forward, single-backslash and Qt-doubled backslash spellings, only at a path boundary.
        string a=path.Replace('\\','/'),b=path.Replace('/','\\'),c=b.Replace("\\","\\\\");
        return "(?:"+Regex.Escape(a)+"|"+Regex.Escape(b)+"|"+Regex.Escape(c)+")(?=[\\\\/\"\\r\\n]|$)";
    }
    static string Respell(string text,string oldPath,string newPath) {
        if(String.IsNullOrEmpty(oldPath)||String.Equals(oldPath,newPath,StringComparison.OrdinalIgnoreCase))return text;
        return Regex.Replace(text,PathPattern(oldPath),delegate(Match m){
            string v=m.Value;
            if(v.Contains("\\\\"))return newPath.Replace("/","\\").Replace("\\","\\\\");
            return v.Contains("\\")?newPath.Replace('/','\\'):newPath.Replace('\\','/');
        },RegexOptions.IgnoreCase|RegexOptions.Multiline);
    }
    static string ExecutableValue(string text,string key) {
        var m=Regex.Match(text,@"(?im)^"+Regex.Escape(key)+@"=(.*)$");return m.Success?m.Groups[1].Value.Trim():null;
    }
    // Rewrites only path settings. User-chosen values outside the old folders are kept.
    public static string RewriteModOrganizerIni(string text,string oldDest,string oldGame,string dest,string game) {
        string g=game.Replace('\\','/'),d=dest.Replace('\\','/');
        text=Respell(text,oldDest,dest);text=Respell(text,oldGame,game);
        text=Engine.Ini(text,"General","gamePath",Engine.GamePathValue(game));
        foreach(var item in new[]{new[]{"base_directory",""},new[]{"mod_directory","/mods"},new[]{"profiles_directory","/profiles"},new[]{"overwrite_directory","/overwrite"},new[]{"download_directory","/downloads"}}) {
            string current=ExecutableValue(text,item[0]);
            // A directory outside the recorded mod folder was chosen by the user; leave it. Missing keys get the standard value.
            if(current==null)text=Engine.Ini(text,"Settings",item[0],Engine.Q(d+item[1]));
        }
        // The four executables the installer created always follow the new locations.
        var titles=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase){
            {"New Vegas",new[]{g+"/FalloutNV.exe",g}},{"Fallout Launcher",new[]{g+"/FalloutNVLauncher.exe",g}},
            {"GECK",new[]{g+"/GECK.exe",g}},{Engine.GeckNvseTitle,new[]{g+"/nvse_loader.exe",g}},{"FNVEdit",new[]{d+"/tools/FNVEdit/FNVEdit.exe",g}}};
        foreach(Match m in Regex.Matches(text,@"(?im)^(\d+)\\title=(.*)$").Cast<Match>().ToList()) {
            string[] paths;if(!titles.TryGetValue(m.Groups[2].Value.Trim(),out paths))continue;
            string key=m.Groups[1].Value+"\\";
            text=Engine.Ini(text,"customExecutables",key+"binary",Engine.Q(paths[0]));text=Engine.Ini(text,"customExecutables",key+"workingDirectory",Engine.Q(paths[1]));
        }
        return text;
    }

    static void RefuseLiveCopy(RelocationPlan plan) {
        if(!plan.DestinationMoved)return;
        string old=null;try{old=Path.Combine(Engine.Full(plan.OldDestination),"_setup","state.json");}catch{}
        if(old!=null&&File.Exists(old))throw new Exception("The original setup still exists at "+plan.OldDestination+".\r\nMove the folder instead of copying it, or uninstall the original first: two setups cannot safely share one game folder.");
    }
    // Store-specific MO2 settings follow the game folder that was attached (a Steam setup can be attached to a GOG or Epic copy). Values the user changed are kept.
    public static string RewriteStoreSettings(string text,string store,string appId) {
        text=Engine.Ini(text,"General","game_edition",store=="Epic"?"Epic Games":store);
        foreach(Match m in Regex.Matches(text,@"(?im)^(\d+)\\title=(.*)$").Cast<Match>().ToList()) {
            string title=m.Groups[2].Value.Trim();if(title!="New Vegas"&&title!="Fallout Launcher")continue;
            string key=m.Groups[1].Value+"\\",args=ExecutableValue(text,key+"arguments"),id=ExecutableValue(text,key+"steamAppID");
            if(String.IsNullOrEmpty(args)||args=="-EpicPortal")text=Engine.Ini(text,"customExecutables",key+"arguments",store=="Epic"?"-EpicPortal":"");
            if(String.IsNullOrEmpty(id)||id=="22380"||id=="22490")text=Engine.Ini(text,"customExecutables",key+"steamAppID",store=="Steam"?appId:"");
        }
        return text;
    }
    // Every file the helper needs to patch and back up a game (xNVSE, the 4GB patcher, xdelta, the ESM deltas) must match the package inventory.
    static void VerifySupportFiles(Engine engine,string dest) {
        string support=Engine.SafeJoin(dest,"_setup");
        foreach(var f in engine.P.files.Where(x=>!x.path.StartsWith("app/"))) {
            string p=Engine.SafeJoin(support,f.path);
            if(!File.Exists(p)||new FileInfo(p).Length!=f.size||Engine.Hash(p)!=f.hash)throw new Exception("The setup's own file _setup\\"+f.path.Replace('/','\\')+" is missing or damaged, so this copy is incomplete. Copy the whole mod folder again.\r\nNo game files have been changed.");
        }
    }
    // English unless the mod folder contains the overlays of a language package.
    public static string InstalledLanguage(Package p,string dest) {
        foreach(var lang in p.languages??new LanguageItem[0])if(lang.patches.Any(x=>File.Exists(Engine.SafeJoin(dest,x.target))))return lang.id;
        return "English";
    }
    // Language overlays are derived from the game's masters. Intact ones are kept, damaged or missing ones are rebuilt with xdelta.
    static void RefreshLanguage(Engine engine,Options o,Action<string> log,List<string> touched) {
        var lang=(engine.P.languages??new LanguageItem[0]).FirstOrDefault(x=>x.id==o.Language);if(lang==null)return;
        string support=Engine.SafeJoin(o.Destination,"_setup"),scratch=Engine.SafeJoin(support,"language-work");Directory.CreateDirectory(scratch);
        foreach(var p in lang.patches) {
            string target=Engine.SafeJoin(o.Destination,p.target);
            if(File.Exists(target)&&new FileInfo(target).Length==p.size&&Engine.Hash(target)==p.targetHash)continue;
            if(p.sourceKind!="game"&&p.sourceKind!="app")throw new Exception("Invalid language source.");
            string localSource=Path.Combine(scratch,"source.esm"),delta=Path.Combine(scratch,"text.vcdiff"),result=Path.Combine(scratch,"result.esm");
            File.Copy(Engine.SafeJoin(p.sourceKind=="game"?o.Game:o.Destination,p.source),localSource,true);if(Engine.Hash(localSource)!=p.sourceHash)throw new Exception("The localization source does not match this game: "+p.source);
            File.Copy(Engine.SafeJoin(support,p.patch),delta,true);
            Engine.Run(Engine.SafeJoin(support,"tools/xdelta3decode.exe"),"-f -d -s source.esm text.vcdiff result.esm",scratch,600,false);
            if(new FileInfo(result).Length!=p.size||Engine.Hash(result)!=p.targetHash)throw new Exception("Russian text verification failed: "+p.target);
            if(File.Exists(target))File.Delete(target);
            File.Move(result,target);touched.Add(target);log("Rebuilt and verified "+p.target);
        }
        string mod=Engine.SafeJoin(o.Destination,"mods/Russian text compatibility");
        foreach(var f in lang.fontFiles) {
            string target=Engine.SafeJoin(mod,f.path.Substring("Data/".Length));
            if(File.Exists(target)&&Engine.Hash(target)==f.hash)continue;
            Engine.EnsureParent(target);File.Copy(Engine.SafeJoin(o.Game,f.path),target,true);
            if(Engine.Hash(target)!=f.hash)throw new Exception("Russian font verification failed: "+f.path);
            touched.Add(target);log("Copied "+f.path);
        }
    }
    // The "Local game files" mod holds timestamped copies of game plugins; they must be this game's plugins.
    static void RefreshLocalCopies(string dest,string game,Action<string> log,List<string> touched) {
        string local=Engine.SafeJoin(dest,"mods/Local game files");if(!Directory.Exists(local))return;
        foreach(var file in Directory.GetFiles(local)) {
            string ext=Path.GetExtension(file).ToLowerInvariant();if(ext!=".esm"&&ext!=".esp")continue;
            string source=Path.Combine(game,"Data",Path.GetFileName(file));
            if(!File.Exists(source)||Engine.Hash(source)==Engine.Hash(file))continue;
            DateTime stamp=File.GetLastWriteTimeUtc(file);File.Copy(source,file,true);File.SetLastWriteTimeUtc(file,stamp);
            if(Engine.Hash(file)!=Engine.Hash(source))throw new Exception("Game plugin copy verification failed: "+Path.GetFileName(file));
            touched.Add(file);log("Refreshed the copy of "+Path.GetFileName(file)+" to match this game.");
        }
    }

    // Uses a setup that was copied to another PC with a fresh, unpatched game folder there. The game folder gets the same verified treatment as a new
    // installation (checks, backups, 4GB patch, xNVSE files); the mod folder keeps its mods, saves and settings but is repointed and re-verified.
    public static void AttachGame(Engine engine,string destination,string game,Action<string> log,IntegrationLocations places=null,string store="Auto",bool defender=false) {
        places=places??new IntegrationLocations();log=log??delegate{};
        var plan=Inspect(destination);string dest=plan.Destination;var old=plan.State;
        game=Engine.Full(game);
        if(!GameLocator.IsGameFolder(game))throw new Exception("The game folder was not found: "+game+"\r\nSelect the folder containing FalloutNV.exe and FalloutNVLauncher.exe.");
        if(Engine.Within(game,dest)||Engine.Within(dest,game))throw new Exception("The game folder and the mod folder must be separate.");
        if(OwnsGame(old,game))throw new Exception("This game folder is already the one this setup uses. Run --relocate without --attach-game to repair its paths.");
        RefuseLiveCopy(plan);
        string setup=Engine.SafeJoin(dest,"_setup"),ini=Engine.SafeJoin(dest,"ModOrganizer.ini");
        if(!File.Exists(ini))throw new Exception("ModOrganizer.ini is missing from the mod folder.");
        if(old.version!=engine.P.version)throw new Exception("This setup was installed by package version "+old.version+", but this installer contains "+engine.P.version+". Attach it with the installer that created it.");
        var o=new Options{Game=game,Destination=dest,Store=store,Language="Auto"};
        engine.CheckGame(o);
        string built=InstalledLanguage(engine.P,dest);
        if(o.Language!=built)throw new Exception("This setup was built for the "+built+" game, but the selected game folder is "+o.Language+". Choose a game folder of the same language.\r\nNo game files have been changed.");
        Engine.CheckGameSpace(o);
        VerifySupportFiles(engine,dest);
        string appId=o.Store=="Steam"?Engine.SteamAppId(game):"";
        var touched=new List<string>();
        // Everything below until the commit only touches the mod folder or a scratch copy.
        string scratch=Engine.SafeJoin(setup,"patchwork");Directory.CreateDirectory(scratch);
        foreach(var p in engine.P.patches) {
            string target=Engine.SafeJoin(dest,p.target.Substring(4));
            if(File.Exists(target)&&new FileInfo(target).Length==p.size&&Engine.Hash(target)==p.targetHash){log("Fixed ESM verified: "+p.target.Substring(4));continue;}
            engine.RebuildMaster(o,p,setup,scratch);touched.Add(target);log("Rebuilt and verified "+p.source);
        }
        RefreshLanguage(engine,o,log,touched);
        RefreshLocalCopies(dest,game,log,touched);
        string originalExeHash=engine.PrepareExecutable(o,setup,scratch,log);
        string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss"),saved=Engine.SafeJoin(setup,"attach-backup/"+stamp);
        Directory.CreateDirectory(saved);
        var meta=new List<string[]>();
        foreach(var n in new[]{"ModOrganizer.ini","_setup/state.json","_setup/integrations.json","_setup/installed-files.json","_setup/defender.json"}) {
            string src=Engine.SafeJoin(dest,n);if(!File.Exists(src))continue;
            string copy=Path.Combine(saved,Path.GetFileName(src));File.Copy(src,copy,true);meta.Add(new[]{src,copy});
        }
        string live=Engine.SafeJoin(setup,"backup"),oldBackup=Path.Combine(saved,"backup");
        var fresh=new InstallState{game=game,destination=dest,version=engine.P.version,status="installing"};
        try {
            // The backups of the other PC's game stay with the record of the attach; the new game gets its own verified backups.
            if(Directory.Exists(live))Directory.Move(live,oldBackup);
            engine.CommitGame(o,fresh,setup,scratch,originalExeHash,log);
            File.WriteAllText(ini,RewriteStoreSettings(RewriteModOrganizerIni(File.ReadAllText(ini,Engine.Utf8),plan.OldDestination,plan.OldGame,dest,game),o.Store,appId),Engine.Utf8);touched.Add(ini);
            log("Updated Mod Organizer paths and store settings.");
            string self=Assembly.GetExecutingAssembly().Location,helper=LaunchSupport.Helper(dest);
            if(!Engine.Within(self,dest)&&File.Exists(self)&&(!File.Exists(helper)||Engine.Hash(helper)!=Engine.Hash(self))){File.Copy(self,helper,true);log("Updated the setup helper.");}
            touched.Add(helper);
            Integrations.WriteLocalFiles(dest);
            foreach(var n in new[]{Integrations.PlayName+".lnk",Integrations.ManageName+".lnk",Integrations.UninstallName+".lnk","Steam launch options.txt",PortableSupport.RepairCommand})touched.Add(Engine.SafeJoin(dest,n));
            Integrations.Rehome(dest,game,log,places);
            fresh.status="installed";Engine.SaveState(fresh);
            touched.Add(Engine.SafeJoin(dest,"_setup/state.json"));touched.Add(Engine.SafeJoin(dest,"_setup/integrations.json"));
            touched.AddRange(Directory.GetFiles(live,"*",SearchOption.AllDirectories));touched.Add(Path.Combine(scratch,"FalloutNV.exe"));
            UpdateReceipt(dest,touched);
            UninstallSupport.RelocateRegistry(plan.OldDestination,dest,log);
        } catch(Exception ex) {
            log("FAILED: "+ex.Message);
            try {
                Engine.RevertChanges(fresh);
                if(Directory.Exists(live)) {
                    // Backups keep the read-only flag of the game files they copy.
                    foreach(var f in Directory.GetFiles(live,"*",SearchOption.AllDirectories))File.SetAttributes(f,FileAttributes.Normal);
                    Directory.Delete(live,true);
                }
                if(Directory.Exists(oldBackup))Directory.Move(oldBackup,live);
                foreach(var m in meta)File.Copy(m[1],m[0],true);
                log("Game files and the setup record were restored to their state before the attach.");
            } catch(Exception re) {log("RESTORE REQUIRED: "+re.Message);}
            throw;
        }
        log("Setup now uses the game folder "+game+" (store: "+o.Store+"). Restore and Uninstall use the backups made now.");
        // A setup copied from an older package revision gets the newer mods in the same step; a failure here never undoes the attach.
        try{if(UpdateSupport.Needs(engine,dest)){log("This setup is older than the installer's package revision; applying the update.");UpdateSupport.Apply(engine,dest,log);}}
        catch(Exception ex){log("The setup was attached but not updated: "+ex.Message+" Run \"Update an existing setup.cmd\" later.");}
        // Exclusions recorded on the old PC mean nothing here. Move them if asked (Windows approval), otherwise close that record.
        try {
            if(DefenderSupport.HasExclusions(dest)) {
                if(defender){DefenderSupport.RequestRehome(dest,game,plan.OldDestination,plan.OldGame);log("Moved the Defender file exclusions to this PC.");}
            } else if(defender){DefenderSupport.Request(dest,game,true);log("Added the Defender file exclusions on this PC.");}
        } catch(Exception ex){log("Defender exclusions were not applied: "+ex.Message);}
        if(DefenderSupport.Detach(dest,game))log("The Defender record from the old PC was closed without adding exclusions here. If Defender blocks xNVSE, exclude the eight installer files manually (see Courier\u2019s Field Guide.html).");
    }

    public static void Relocate(string destination,string game,Action<string> log,IntegrationLocations places=null,bool rehomeDefender=false) {
        places=places??new IntegrationLocations();log=log??delegate{};
        var plan=Inspect(destination);string dest=plan.Destination;var state=plan.State;
        game=Engine.Full(String.IsNullOrWhiteSpace(game)?state.game:game);
        if(!GameLocator.IsGameFolder(game))throw new Exception("The game folder was not found: "+game+"\r\nSelect the folder containing FalloutNV.exe and FalloutNVLauncher.exe that this setup was installed into.");
        if(Engine.Within(game,dest)||Engine.Within(dest,game))throw new Exception("The game folder and the mod folder must be separate.");
        if(!OwnsGame(state,game))throw new Exception("This game folder is not the one the setup was installed into: its FalloutNV.exe is not the patched file the installer wrote.\r\nMove the whole patched game folder, or attach this setup to the other game folder: --relocate --destination <mod folder> --attach-game <game folder> (the setup then patches and backs up that game, like a new installation).");
        Engine.NotRunning(game,dest);
        RefuseLiveCopy(plan);
        string setup=Engine.SafeJoin(dest,"_setup"),stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss"),backup=Engine.SafeJoin(setup,"relocate-backup/"+stamp);
        var touched=new List<string>();
        Directory.CreateDirectory(backup);
        foreach(var n in new[]{"ModOrganizer.ini","_setup/state.json","_setup/integrations.json","_setup/installed-files.json"}) {
            string src=Engine.SafeJoin(dest,n);if(File.Exists(src)){string dst=Path.Combine(backup,Path.GetFileName(src));File.Copy(src,dst,true);touched.Add(dst);}
        }
        string ini=Engine.SafeJoin(dest,"ModOrganizer.ini");
        if(!File.Exists(ini))throw new Exception("ModOrganizer.ini is missing from the mod folder.");
        File.WriteAllText(ini,RewriteModOrganizerIni(File.ReadAllText(ini,Engine.Utf8),plan.OldDestination,plan.OldGame,dest,game),Engine.Utf8);touched.Add(ini);
        log("Updated Mod Organizer paths.");
        // A newer installer can replace the helper that the shortcuts start.
        string self=Assembly.GetExecutingAssembly().Location,helper=LaunchSupport.Helper(dest);
        if(!Engine.Within(self,dest)&&File.Exists(self)&&(!File.Exists(helper)||Engine.Hash(helper)!=Engine.Hash(self))){File.Copy(self,helper,true);log("Updated the setup helper.");}
        touched.Add(helper);
        Integrations.WriteLocalFiles(dest);
        foreach(var n in new[]{Integrations.PlayName+".lnk",Integrations.ManageName+".lnk",Integrations.UninstallName+".lnk","Steam launch options.txt",PortableSupport.RepairCommand})touched.Add(Engine.SafeJoin(dest,n));
        Integrations.Rehome(dest,game,log,places);
        // The state record changes last: if anything above failed, running this again continues the job.
        state.destination=dest;state.game=game;Engine.SaveState(state);
        touched.Add(Engine.SafeJoin(dest,"_setup/state.json"));touched.Add(Engine.SafeJoin(dest,"_setup/integrations.json"));
        UpdateReceipt(dest,touched);
        UninstallSupport.RelocateRegistry(plan.OldDestination,dest,log);
        log("Setup now points to "+dest+" and "+game+".");
        if(DefenderSupport.HasExclusions(dest)) {
            // The exclusions name exact files, so they must follow the files. This needs the Windows administrator prompt.
            if(!rehomeDefender)log("Defender exclusions still name the old locations. Run \""+RepairCommand+"\" interactively to move them.");
            else try{DefenderSupport.RequestRehome(dest,game,plan.OldDestination,plan.OldGame);log("Moved the Defender file exclusions to the new locations.");}
            catch(Exception ex){log("Defender exclusions were not moved: "+ex.Message);}
        }
    }
    internal static void UpdateReceipt(string dest,IEnumerable<string> files,IEnumerable<string> removed=null) {
        string path=Engine.SafeJoin(dest,"_setup/installed-files.json");if(!File.Exists(path))return;
        var receipt=Engine.Json().Deserialize<OwnedFiles>(File.ReadAllText(path,Engine.Utf8));receipt.destination=dest;
        var gone=new HashSet<string>((removed??new string[0]).Select(x=>x.Replace('\\','/')),StringComparer.OrdinalIgnoreCase);
        receipt.files.RemoveAll(f=>gone.Contains(f.path)&&!File.Exists(Engine.SafeJoin(dest,f.path)));
        var index=new Dictionary<string,FileItem>(StringComparer.OrdinalIgnoreCase);foreach(var f in receipt.files)index[f.path]=f;
        foreach(var file in files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) {
            string rel=file.Substring(dest.Length+1).Replace('\\','/');FileItem item;
            if(!index.TryGetValue(rel,out item)){item=new FileItem{path=rel};receipt.files.Add(item);index[rel]=item;}
            item.hash=Engine.Hash(file);item.size=new FileInfo(file).Length;
        }
        File.WriteAllText(path,Engine.Json().Serialize(receipt),Engine.Utf8);
    }

    // Interactive or scripted entry point. Returns true when a relocation was performed.
    public static bool Run(string destination,string game,bool quiet,bool onlyIfNeeded,Action<string> log,string self=null,bool offerPlay=true,string attachGame=null,string store="Auto",bool defender=false,IntegrationLocations places=null) {
        string dest=String.IsNullOrWhiteSpace(destination)?InstalledFolderOf(self??Assembly.GetExecutingAssembly().Location):destination;
        if(dest==null)throw new Exception("Use --relocate --destination <mod folder>, or start this from the setup helper inside the mod folder.");
        var plan=Inspect(dest);
        if(onlyIfNeeded&&!plan.NeedsWork)return false;
        bool attach=!String.IsNullOrWhiteSpace(attachGame);
        if(attach)game=attachGame;
        else if(String.IsNullOrWhiteSpace(game)&&plan.GameMissing) {
            var matches=MatchingGames(plan.State);
            if(matches.Count>0)game=matches[0];
            else if(quiet)throw new Exception("The game folder "+plan.State.game+" was not found. Pass --game <patched game folder>, or --attach-game <fresh game folder> to use this setup with another copy of the game (for example after copying it to a new PC).");
            else {var pick=PickGame(plan);if(pick==null)return false;game=pick.Path;attach=pick.Attach;}
            if(game==null)return false;
        }
        string target=String.IsNullOrWhiteSpace(game)?plan.State.game:game;
        // The recorded game folder exists on this PC but is not the patched file this setup wrote (for example a fresh copy at the same path).
        if(!attach&&!quiet&&GameLocator.IsGameFolder(target)&&!OwnsGame(plan.State,target)&&GameLocator.SupportedExe(target)) {
            if(MessageBox.Show("The game folder\r\n"+target+"\r\nis not the patched game this setup was installed into.\r\n\r\nAttach the setup to this game folder instead? Its game files are checked, backed up and patched like a new installation.","Move or repair setup",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return false;
            attach=true;
        }
        if(attach) {
            if(!quiet) {
                string text="Attach this setup to a different game folder?\r\n\r\nMod folder: "+plan.Destination+"\r\nGame folder: "+target+"\r\n\r\nThe game files are checked, then backed up. The 4GB patch and the xNVSE files are added to that game folder, Mod Organizer and the shortcuts are repointed, and the Fixed ESMs are verified against this game. Restore and Uninstall undo the game changes. Mods and saves are not changed.";
                if(MessageBox.Show(text,"Attach setup to a game folder",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return false;
                if(!defender)defender=MessageBox.Show("xNVSE can be blocked by Microsoft Defender. Add file exclusions for the same eight named files? Windows asks for approval, and Uninstall removes them again.","Attach setup to a game folder",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes;
            }
            AttachGame(new Engine(),plan.Destination,target,log,places,store,defender);
            if(!quiet&&offerPlay&&MessageBox.Show("The setup is attached to the new game folder.\r\n\r\nPlay New Vegas now?","Attach setup to a game folder",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)LaunchSupport.Launch(plan.Destination,true);
            return true;
        }
        if(!quiet) {
            string text=(plan.NeedsWork?"This setup has been moved or its game folder is missing.\r\n\r\n":"Nothing appears to have moved. Refresh the paths and shortcuts anyway?\r\n\r\n")+"Mod folder: "+plan.Destination+(plan.DestinationMoved?"\r\n   (was "+plan.OldDestination+")":"")+"\r\nGame folder: "+target+(String.Equals(target,plan.OldGame,StringComparison.OrdinalIgnoreCase)?"":"\r\n   (was "+plan.OldGame+")")+"\r\n\r\nThis updates Mod Organizer's settings, the shortcuts and the uninstall record. Mods, saves and game files are not changed.";
            if(MessageBox.Show(text,"Move or repair setup",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return false;
        }
        Relocate(plan.Destination,game,log,places,!quiet);
        if(!quiet&&offerPlay&&MessageBox.Show("The setup is ready at its new location.\r\n\r\nPlay New Vegas now?","Move or repair setup",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)LaunchSupport.Launch(plan.Destination,true);
        return true;
    }
    class GameChoice {public string Path;public bool Attach;}
    static GameChoice PickGame(RelocationPlan plan) {
        // A supported game on this PC is the likely target when the setup was copied here.
        string start=plan.OldGame;
        try{var first=GameLocator.Find().FirstOrDefault(c=>c.Supported);if(first!=null)start=first.Path;}catch{}
        while(true) {
            using(var f=new FolderBrowserDialog{Description="The game folder was not found.\r\nSelect the patched Fallout New Vegas folder this setup belongs to, or, if the setup was copied to this PC, the game folder here (it contains FalloutNV.exe).",SelectedPath=start}) {
                if(f.ShowDialog()!=DialogResult.OK)return null;
                if(GameLocator.IsGameFolder(f.SelectedPath)) {
                    if(OwnsGame(plan.State,f.SelectedPath))return new GameChoice{Path=f.SelectedPath};
                    if(GameLocator.SupportedExe(f.SelectedPath)&&MessageBox.Show("That is a different game folder (not the patched one this setup was installed into).\r\nAttach the setup to it? The game is checked, backed up and patched like a new installation.","Move or repair setup",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)return new GameChoice{Path=f.SelectedPath,Attach=true};
                }
                if(MessageBox.Show("That folder cannot be used for this setup.\r\nChoose a different folder?","Move or repair setup",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return null;
            }
        }
    }
}
}
