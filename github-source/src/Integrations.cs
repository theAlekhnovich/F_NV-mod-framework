using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FalloutModSetup {
public class SteamAccount {
    public string Config, Id; public bool Active;
    public override string ToString(){return "Steam account "+Id+(Active?" (last active)":"");}
}
public class IntegrationLocations {
    public Action RequireSteamClosed=Integrations.SteamClosed;
    public string Desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string Programs=Environment.GetFolderPath(Environment.SpecialFolder.Programs);
}
public class ShortcutRecord {public string path, backup, before, after, kind;public bool existed,touched;}
public class SteamRecord {public string path,appId,before,after;public bool existed,touched;}
public class IntegrationState {
    public string destination,game,status="pending";
    public List<ShortcutRecord> shortcuts=new List<ShortcutRecord>();
    public SteamRecord steam;
}

// Token offsets let us change one Steam setting without reserializing other account data.
public class Vdf {
    class Token {public string text;public int start,end;public bool brace;}
    class Node {public string name,value;public int start,end,valueStart,valueEnd,close;public List<Node> children;}
    static List<Token> Tokens(string s) {
        var list=new List<Token>();int i=0;
        while(i<s.Length) {
            if(Char.IsWhiteSpace(s[i])||s[i]=='\ufeff'){i++;continue;}
            if(s[i]=='/'&&i+1<s.Length&&s[i+1]=='/'){while(i<s.Length&&s[i]!='\n')i++;continue;}
            int start=i;char c=s[i++];
            if(c=='{'||c=='}'){list.Add(new Token{text=c.ToString(),start=start,end=i,brace=true});continue;}
            var b=new StringBuilder();
            if(c=='"') {
                bool closed=false;
                while(i<s.Length) {
                    char ch=s[i++];if(ch=='"'){closed=true;break;}
                    if(ch=='\\'&&i<s.Length) {
                        ch=s[i++];
                        if(ch=='n')b.Append('\n');else if(ch=='t')b.Append('\t');else if(ch=='r')b.Append('\r');
                        else if(ch=='"'||ch=='\\')b.Append(ch);else {b.Append('\\');b.Append(ch);}
                    } else b.Append(ch);
                }
                if(!closed)throw new Exception("Steam configuration contains an unterminated string.");
            } else {b.Append(c);while(i<s.Length&&!Char.IsWhiteSpace(s[i])&&s[i]!='{'&&s[i]!='}')b.Append(s[i++]);}
            list.Add(new Token{text=b.ToString(),start=start,end=i});
        }
        return list;
    }
    static List<Node> Read(List<Token> t,ref int i,bool nested) {
        var nodes=new List<Node>();
        while(i<t.Count) {
            if(t[i].brace&&t[i].text=="}"){if(!nested)throw new Exception("Unexpected Steam configuration brace.");return nodes;}
            var key=t[i++];if(key.brace||i>=t.Count)throw new Exception("Invalid Steam configuration.");
            var value=t[i++];var n=new Node{name=key.text,start=key.start};
            if(value.brace&&value.text=="{") {
                n.children=Read(t,ref i,true);if(i>=t.Count)throw new Exception("Incomplete Steam configuration.");n.close=t[i].start;n.end=t[i++].end;
            } else {if(value.brace)throw new Exception("Invalid Steam configuration value.");n.value=value.text;n.valueStart=value.start;n.valueEnd=value.end;n.end=value.end;}
            nodes.Add(n);
        }
        if(nested)throw new Exception("Incomplete Steam configuration block.");return nodes;
    }
    static Node Find(List<Node> nodes,string name) {
        var found=nodes.Where(n=>n.name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToList();
        if(found.Count>1)throw new Exception("Steam configuration has duplicate "+name+" entries; edit launch options manually.");
        return found.Count==1?found[0]:null;
    }
    static List<Node> Parse(string s){int i=0;return Read(Tokens(s),ref i,false);}
    static string[] Route(string appId) {
        if(appId!="22380"&&appId!="22490")throw new Exception("Unexpected Fallout Steam app ID.");
        return new[]{"UserLocalConfigStore","Software","Valve","Steam","apps",appId};
    }
    public static string Get(string text,string appId,out bool exists) {
        var nodes=Parse(text);Node node=null;
        foreach(var key in Route(appId)){node=Find(nodes,key);if(node==null){exists=false;return null;}if(node.children==null)throw new Exception("Unexpected Steam configuration structure.");nodes=node.children;}
        node=Find(nodes,"LaunchOptions");exists=node!=null;
        if(node!=null&&node.children!=null)throw new Exception("Unexpected Steam launch options structure.");
        return node==null?null:node.value;
    }
    static string Quote(string s){return "\""+s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t")+"\"";}
    public static string Set(string text,string appId,string value,bool present) {
        var route=Route(appId);var nodes=Parse(text);Node parent=null;
        for(int i=0;i<route.Length;i++) {
            var n=Find(nodes,route[i]);
            if(n==null) {
                if(!present)return text;
                if(parent==null)throw new Exception("This is not a Steam user localconfig.vdf file.");
                string insertion="\r\n";
                for(int j=i;j<route.Length;j++)insertion+=new string('\t',j+1)+Quote(route[j])+"\r\n"+new string('\t',j+1)+"{\r\n";
                insertion+=new string('\t',route.Length+1)+"\"LaunchOptions\"\t\t"+Quote(value)+"\r\n";
                for(int j=route.Length-1;j>=i;j--)insertion+=new string('\t',j+1)+"}\r\n";
                return text.Insert(parent.close,insertion);
            }
            if(n.children==null)throw new Exception("Unexpected Steam configuration structure.");parent=n;nodes=n.children;
        }
        var option=Find(nodes,"LaunchOptions");
        if(option==null)return present?text.Insert(parent.close,"\r\n\t\t\t\t\t\t\t\"LaunchOptions\"\t\t"+Quote(value)+"\r\n"):text;
        if(option.children!=null)throw new Exception("Unexpected Steam launch options structure.");
        return present?text.Substring(0,option.valueStart)+Quote(value)+text.Substring(option.valueEnd):text.Remove(option.start,option.end-option.start);
    }
}

public static class Integrations {
    public const string LaunchArguments="\"moshortcut://:New Vegas\"";
    // Shortcut titles (file names without .lnk): the same on the desktop, in the Start menu folder, in the mod folder and in the game folder.
    public const string PlayName="Fallout New Vegas - mod framework",ManageName="Mod Organizer 2",UninstallName="Uninstall F-NV MF";
    public const string MenuFolder=PlayName;
    // Names written by installers up to 1.7.2; still recognized so existing setups are renamed, restored and uninstalled.
    static readonly string[] LegacyPlay={"Play Modded New Vegas","Fallout New Vegas - Modded","Play New Vegas"},LegacyManage={"Manage mods","Fallout New Vegas - Mod Organizer"},LegacyUninstall={"Uninstall Framework","Fallout New Vegas - Uninstall"};
    static readonly string[] LegacyMenuFolders={"Fallout New Vegas - Modification Framework","Fallout New Vegas - Mod Setup"};
    static bool Is(string name,string current,string[] legacy){return name.Equals(current,StringComparison.OrdinalIgnoreCase)||legacy.Any(x=>name.Equals(x,StringComparison.OrdinalIgnoreCase));}
    // "play", "manage" or "uninstall" for a shortcut file name (old or new), otherwise null.
    static string Role(string file) {
        string n=Path.GetFileNameWithoutExtension(file);
        return Is(n,PlayName,LegacyPlay)?"play":Is(n,ManageName,LegacyManage)?"manage":Is(n,UninstallName,LegacyUninstall)?"uninstall":null;
    }
    static string RoleName(string role){return role=="play"?PlayName:role=="manage"?ManageName:UninstallName;}
    // Where a shortcut lives. Records written before 1.7.3 carry no kind, so it follows from the old file names.
    static string Kind(ShortcutRecord c) {
        if(!String.IsNullOrEmpty(c.kind))return c.kind;
        string name=Path.GetFileName(c.path);
        if(name.Equals("Play Modded New Vegas.lnk",StringComparison.OrdinalIgnoreCase))return "game";
        return name.StartsWith("Fallout New Vegas - ",StringComparison.Ordinal)?"desktop":"menu";
    }
    // Mod Organizer 2 keeps its own icon although its shortcut starts the helper.
    public static string ManageIcon(string dest){return Engine.SafeJoin(dest,"ModOrganizer.exe")+",0";}
    static string IconFor(string role,string dest,string lucky){return role=="manage"?ManageIcon(dest):lucky;}
    public static string LaunchOption(string destination){return "\""+LaunchSupport.Helper(destination)+"\" "+LaunchSupport.Arguments(destination,true)+" %command%";}
    public static List<SteamAccount> Accounts() {
        var result=new List<SteamAccount>();
        try {
            using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) {
                string root=key==null?null:key.GetValue("SteamPath") as string;if(root==null)return result;
                string users=Path.Combine(root,"userdata");if(!Directory.Exists(users))return result;
                string active="";using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))if(k!=null)active=Convert.ToString(k.GetValue("ActiveUser"));
                foreach(var d in Directory.EnumerateDirectories(users)) {
                    string id=Path.GetFileName(d),p=Path.Combine(d,"config","localconfig.vdf");
                    if(Regex.IsMatch(id,@"^\d+$")&&File.Exists(p))result.Add(new SteamAccount{Id=id,Config=p,Active=id==active});
                }
            }
        }catch{}
        return result.OrderByDescending(x=>x.Active).ThenBy(x=>x.Id).ToList();
    }
    public static void SteamClosed() {
        if(Process.GetProcessesByName("steam").Length>0)throw new Exception("Exit Steam completely using Steam > Exit, then try again. Steam can overwrite launch options edited while it is running. Alternatively, turn off automatic Steam integration and use the supplied launch-options text afterward.");
    }
    static string SteamPath(string path) {
        if(String.IsNullOrWhiteSpace(path))throw new Exception("Choose the Steam account to configure.");
        path=Engine.Full(path);var file=new FileInfo(path);var config=file.Directory;var account=config==null?null:config.Parent;var users=account==null?null:account.Parent;
        if(!file.Name.Equals("localconfig.vdf",StringComparison.OrdinalIgnoreCase)||config==null||config.Name!="config"||account==null||!Regex.IsMatch(account.Name,@"^\d+$")||users==null||users.Name!="userdata")throw new Exception("Select a Steam account's userdata\\<account>\\config\\localconfig.vdf.");
        return path;
    }
    public static void Preflight(Options o,IntegrationLocations places=null,bool includeInstallSpace=true) {
        places=places??new IntegrationLocations();
        if(o.DesktopShortcut)Engine.Full(places.Desktop);
        if(o.StartMenuShortcut)Engine.Full(places.Programs);
        if(!String.IsNullOrWhiteSpace(o.ImportSaves)) {
            if(!o.LocalSaves)throw new Exception("Enable profile-local saves before copying existing saves.");
            string path=Engine.Full(o.ImportSaves);if(!Directory.Exists(path))throw new Exception("The selected existing-saves folder is not available.");
            if(Engine.Within(path,o.Destination)||Engine.Within(o.Destination,path))throw new Exception("The existing-saves folder must be separate from the new mod folder.");
            long bytes=Directory.EnumerateFiles(path).Where(x=>new[]{".fos",".nvse",".bak"}.Contains(Path.GetExtension(x).ToLowerInvariant())).Sum(x=>new FileInfo(x).Length);
            if(new DriveInfo(Path.GetPathRoot(o.Destination)).AvailableFreeSpace<bytes+(includeInstallSpace?26L*1024*1024*1024:0))throw new Exception("Allow enough room for the mod setup and the selected existing saves.");
        }
        if(o.SteamPlay)throw new Exception("Automatic Steam integration has been removed. Use the manual instructions installed in the mod folder.");
    }
    public static void Shortcut(string file,string exe,string args,string cwd,string icon) {
        Engine.EnsureParent(file);object shell=null,link=null;
        try {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{file});
            foreach(var v in new Dictionary<string,object>{{"TargetPath",exe},{"Arguments",args},{"WorkingDirectory",cwd},{"IconLocation",icon},{"Description","Fallout New Vegas mod setup"}})
                link.GetType().InvokeMember(v.Key,BindingFlags.SetProperty,null,link,new[]{v.Value});
            link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
        }finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
    }
    static string Journal(string destination){return Engine.SafeJoin(destination,"_setup/integrations.json");}
    static void Save(IntegrationState state) {
        string path=Journal(state.destination),temp=path+".tmp";Engine.EnsureParent(path);File.WriteAllText(temp,Engine.Json().Serialize(state),Engine.Utf8);
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
    static void WriteConfig(string path,string before,string after) {
        if(File.ReadAllText(path,Engine.Utf8)!=before)throw new Exception("Steam configuration changed during installation. Retry with Steam closed.");
        string temp=Path.Combine(Path.GetDirectoryName(path),"fnv-launch-"+Guid.NewGuid().ToString("N")+".tmp");
        try {
            File.WriteAllText(temp,after,Engine.Utf8);
            if(File.ReadAllText(path,Engine.Utf8)!=before)throw new Exception("Steam configuration changed before it could be saved.");
            File.Replace(temp,path,null);
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
    // Launchers that live inside the mod folder. Also rewritten when a setup is moved or updated.
    public static void WriteLocalFiles(string dest) {
        string iconPath=Engine.SafeJoin(dest,"_setup/Lucky38.ico");Engine.EnsureParent(iconPath);
        using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("Lucky38.ico"))using(var output=File.Create(iconPath))input.CopyTo(output);
        string mo=LaunchSupport.Helper(dest),icon=iconPath+",0";
        Shortcut(Engine.SafeJoin(dest,PlayName+".lnk"),mo,LaunchSupport.Arguments(dest,true),dest,icon);
        Shortcut(Engine.SafeJoin(dest,ManageName+".lnk"),mo,LaunchSupport.Arguments(dest,false),dest,ManageIcon(dest));
        Shortcut(Engine.SafeJoin(dest,UninstallName+".lnk"),mo,"--uninstall-ui --destination "+Engine.Q(dest),dest,icon);
        RemoveLegacyLocalLinks(dest);
        File.WriteAllText(Engine.SafeJoin(dest,"Steam launch options.txt"),"Paste this in Fallout: New Vegas > Properties > General > Launch Options:\r\n\r\n"+LaunchOption(dest)+"\r\n\r\nThe helper starts this installation's Default profile. Close other MO2 instances first. Manual Steam setup is optional and is not guaranteed on every Steam configuration. You can also add the game-folder \""+PlayName+"\" shortcut as a non-Steam shortcut. If Steam rejects a .lnk target, copy its Target and Arguments instead.\r\n",Engine.Utf8);
        // Relative paths only, so it keeps working after the folder is moved.
        File.WriteAllText(Engine.SafeJoin(dest,PortableSupport.RepairCommand),PortableSupport.RepairCommandText(),Encoding.ASCII);
    }
    static readonly string[] LegacyLocalLinks={"Play New Vegas.lnk","Manage mods.lnk","Uninstall Framework.lnk"};
    static string LinkTarget(string file) {
        object shell=null,link=null;
        try {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{file});
            return Convert.ToString(link.GetType().InvokeMember("TargetPath",BindingFlags.GetProperty,null,link,null));
        }catch{return null;}
        finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
    }
    // Only a link that still starts the setup helper is removed; anything else under an old name is not ours.
    static bool OurLegacyLink(string file){
        string target=File.Exists(file)?LinkTarget(file):null;
        return target!=null&&Path.GetFileName(target).Equals("Setup and Restore.exe",StringComparison.OrdinalIgnoreCase);
    }
    static void RemoveLegacyLocalLinks(string dest){
        foreach(var n in LegacyLocalLinks){string file=Engine.SafeJoin(dest,n);if(OurLegacyLink(file))File.Delete(file);}
    }
    // True when this setup still has shortcuts under the names used before 1.7.3.
    public static bool HasLegacyNames(string dest) {
        if(LegacyLocalLinks.Any(n=>File.Exists(Engine.SafeJoin(dest,n))))return true;
        string journal=Journal(dest);if(!File.Exists(journal))return false;
        var state=Engine.Json().Deserialize<IntegrationState>(File.ReadAllText(journal,Engine.Utf8));if(state.status=="restored")return false;
        return state.shortcuts.Where(x=>x.touched).Any(x=>{string role=Role(x.path);return role!=null&&(!Path.GetFileName(x.path).Equals(RoleName(role)+".lnk",StringComparison.OrdinalIgnoreCase)||LegacyMenuFolders.Any(f=>f.Equals(Path.GetFileName(Path.GetDirectoryName(x.path)),StringComparison.OrdinalIgnoreCase)));});
    }
    // Update route: renames the shortcuts of an existing setup to the current names. Nothing happens when there is nothing to rename.
    public static void MigrateNames(string dest,string game,Action<string> log,IntegrationLocations places=null) {
        if(!HasLegacyNames(dest))return;
        WriteLocalFiles(dest);Rehome(dest,game,log,places);
    }
    static string Remap(ShortcutRecord c,string role,IntegrationLocations places,string game) {
        string kind=Kind(c),name=RoleName(role)+".lnk";
        return kind=="game"?Path.Combine(game,name):kind=="desktop"?Path.Combine(places.Desktop,name):Path.Combine(places.Programs,MenuFolder,name);
    }
    // Repoints every shortcut this installer created to a moved mod/game folder and gives it the current name. Edited shortcuts are left alone.
    public static void Rehome(string dest,string game,Action<string> log,IntegrationLocations places=null) {
        places=places??new IntegrationLocations();string journal=Journal(dest);if(!File.Exists(journal))return;
        var state=Engine.Json().Deserialize<IntegrationState>(File.ReadAllText(journal,Engine.Utf8));if(state.status=="restored")return;
        state.destination=dest;state.game=game;
        string mo=LaunchSupport.Helper(dest),lucky=Engine.SafeJoin(dest,"_setup/Lucky38.ico")+",0",stage=Engine.SafeJoin(dest,"_setup/shortcut-stage.lnk");
        var args=new Dictionary<string,string>{{"play",LaunchSupport.Arguments(dest,true)},{"manage",LaunchSupport.Arguments(dest,false)},{"uninstall","--uninstall-ui --destination "+Engine.Q(dest)}};
        foreach(var c in state.shortcuts.Where(x=>x.touched).ToList()) {
            string role=Role(c.path);
            if(role==null)continue;
            string old=c.path,target=Engine.Full(Remap(c,role,places,game)),kind=Kind(c);bool same=String.Equals(old,target,StringComparison.OrdinalIgnoreCase);
            bool renamed=!String.Equals(Path.GetFileName(old),Path.GetFileName(target),StringComparison.OrdinalIgnoreCase);
            Shortcut(stage,mo,args[role],dest,IconFor(role,dest,lucky));string fresh=Engine.Hash(stage);
            bool oursOld=File.Exists(old)&&Engine.Hash(old)==c.after;
            if(same) {
                if(File.Exists(old)&&!oursOld&&Engine.Hash(old)!=fresh){log("Kept your edited shortcut: "+old);continue;}
                File.Copy(stage,old,true);c.after=fresh;c.kind=kind;
            } else {
                if(renamed&&File.Exists(old)&&!oursOld&&Engine.Hash(old)!=fresh){log("Kept your edited shortcut under its old name: "+old);continue;}
                if(File.Exists(target)&&Engine.Hash(target)!=fresh&&Engine.Hash(target)!=c.after){log("Skipped shortcut because another file is there: "+target);continue;}
                Engine.EnsureParent(target);File.Copy(stage,target,true);
                if(oursOld) {
                    string saved=Engine.SafeJoin(dest,"_setup/"+c.backup);
                    if(c.existed&&File.Exists(saved)&&Engine.Hash(saved)==c.before)File.Copy(saved,old,true);else File.Delete(old);
                }
                c.path=target;c.after=fresh;c.existed=false;c.before=null;c.kind=kind;
            }
            Save(state);log("Updated shortcut: "+c.path);
        }
        if(File.Exists(stage))File.Delete(stage);
        // The Start menu folder of earlier versions goes away once it is empty.
        foreach(var f in LegacyMenuFolders){string menu=Path.Combine(places.Programs,f);try{if(Directory.Exists(menu)&&!Directory.EnumerateFileSystemEntries(menu).Any())Directory.Delete(menu,false);}catch{}}
        Save(state);
    }
    public static void Apply(Options o,Action<string> log,IntegrationLocations places=null) {
        places=places??new IntegrationLocations();Preflight(o,places,false);
        string iconPath=Engine.SafeJoin(o.Destination,"_setup/Lucky38.ico");
        string mo=LaunchSupport.Helper(o.Destination),icon=iconPath+",0",playArgs=LaunchSupport.Arguments(o.Destination,true),manageArgs=LaunchSupport.Arguments(o.Destination,false),uninstallArgs="--uninstall-ui --destination "+Engine.Q(o.Destination);
        WriteLocalFiles(o.Destination);
        var state=new IntegrationState{destination=o.Destination,game=o.Game};Save(state);
        // path, arguments, role (play/manage/uninstall), kind (game/desktop/menu)
        var links=new List<string[]>();
        links.Add(new[]{Path.Combine(o.Game,PlayName+".lnk"),playArgs,"play","game"});
        if(o.DesktopShortcut) {
            links.Add(new[]{Path.Combine(places.Desktop,PlayName+".lnk"),playArgs,"play","desktop"});
            links.Add(new[]{Path.Combine(places.Desktop,ManageName+".lnk"),manageArgs,"manage","desktop"});
            links.Add(new[]{Path.Combine(places.Desktop,UninstallName+".lnk"),uninstallArgs,"uninstall","desktop"});
        }
        if(o.StartMenuShortcut) {
            string folder=Path.Combine(places.Programs,MenuFolder);
            links.Add(new[]{Path.Combine(folder,PlayName+".lnk"),playArgs,"play","menu"});
            links.Add(new[]{Path.Combine(folder,ManageName+".lnk"),manageArgs,"manage","menu"});
            links.Add(new[]{Path.Combine(folder,UninstallName+".lnk"),uninstallArgs,"uninstall","menu"});
        }
        foreach(var l in links) {
            string path=Engine.Full(l[0]),stage=Engine.SafeJoin(o.Destination,"_setup/shortcut-stage.lnk");Shortcut(stage,mo,l[1],o.Destination,IconFor(l[2],o.Destination,icon));
            var c=new ShortcutRecord{path=path,kind=l[3],existed=File.Exists(path),after=Engine.Hash(stage),backup="shortcut-"+state.shortcuts.Count+".lnk"};
            if(c.existed){c.before=Engine.Hash(path);File.Copy(path,Engine.SafeJoin(o.Destination,"_setup/"+c.backup),false);}
            state.shortcuts.Add(c);c.touched=true;Save(state);Engine.EnsureParent(path);File.Copy(stage,path,true);
            if(Engine.Hash(path)!=c.after)throw new Exception("Shortcut verification failed.");log("Created shortcut: "+path);
        }
        state.status="installed";Save(state);
    }
    static bool AllowedShortcut(string p,IntegrationLocations places,string game) {
        string full=Engine.Full(p);
        var names=new[]{PlayName,ManageName,UninstallName}.Concat(LegacyPlay).Concat(LegacyManage).Concat(LegacyUninstall).Select(n=>n+".lnk").ToList();
        if(!String.IsNullOrEmpty(game)&&names.Any(n=>full.Equals(Path.Combine(Engine.Full(game),n),StringComparison.OrdinalIgnoreCase)))return true;
        if(names.Any(n=>full.Equals(Path.Combine(places.Desktop,n),StringComparison.OrdinalIgnoreCase)))return true;
        names.Add("Setup and restore.lnk");
        return new[]{MenuFolder}.Concat(LegacyMenuFolders).Any(folder=>names.Any(n=>full.Equals(Path.Combine(places.Programs,folder,n),StringComparison.OrdinalIgnoreCase)));
    }
    // report != null is the uninstall: nothing here may stop it. A shortcut that points to the framework (or is one of our own names with an unreadable target) is removed even if its bytes changed;
    // one that now points to another program stays and is reported. The shortcut that existed before setup comes back only from a backup whose hash matches. Steam trouble is reported and retried on the next run.
    static void RestoreLenient(string destination,IntegrationState state,IntegrationLocations places,Action<string> report) {
        bool complete=true;
        if(state.steam!=null&&state.steam.touched) {
            try {
                places.RequireSteamClosed();string config=SteamPath(state.steam.path),text=File.ReadAllText(config,Engine.Utf8);bool exists;string current=Vdf.Get(text,state.steam.appId,out exists);
                bool original=exists==state.steam.existed&&current==state.steam.before;
                if(original){}
                else if(!exists||current!=state.steam.after)report("Steam launch options were changed after installation and were left as they are.");
                else WriteConfig(state.steam.path,text,Vdf.Set(text,state.steam.appId,state.steam.before,state.steam.existed));
            } catch(Exception ex) { complete=false;report("Steam launch options were not restored ("+ex.Message+"). Close Steam and run Uninstall again, or remove the launch option by hand."); }
        }
        foreach(var c in state.shortcuts.Where(x=>x.touched)) {
            try {
                if(!AllowedShortcut(c.path,places,state.game)){report("Skipped a shortcut outside the places setup uses: "+c.path);continue;}
                if(File.Exists(c.path)) {
                    string h=Engine.Hash(c.path);
                    if(c.existed&&h==c.before)continue;
                    string target=UninstallSupport.ShortcutTarget(c.path);
                    bool ours=h==c.after||UninstallSupport.PointsToHelper(target)||(target==null&&Role(c.path)!=null);
                    if(!ours){report("Kept the shortcut "+c.path+": it now points to "+target+".");continue;}
                    if((File.GetAttributes(c.path)&FileAttributes.ReadOnly)!=0)File.SetAttributes(c.path,FileAttributes.Normal);
                    File.Delete(c.path);
                }
                if(c.existed) {
                    string saved=Regex.IsMatch(c.backup??"",@"^shortcut-\d+\.lnk$")?Engine.SafeJoin(destination,"_setup/"+c.backup):null;
                    if(saved!=null&&File.Exists(saved)&&Engine.Hash(saved)==c.before){Engine.EnsureParent(c.path);File.Copy(saved,c.path,true);}
                    else report("The shortcut that was here before setup could not be put back (its backup is missing or changed): "+c.path);
                }
            } catch(Exception ex) { report("A shortcut could not be handled: "+c.path+" ("+ex.Message+")"); }
        }
        foreach(string name in new[]{MenuFolder}.Concat(LegacyMenuFolders)){try{string menu=Path.Combine(places.Programs,name);if(Directory.Exists(menu)&&!Directory.EnumerateFileSystemEntries(menu).Any())Directory.Delete(menu,false);}catch(Exception ex){report("The Start menu folder could not be removed ("+ex.Message+").");}}
        if(complete){state.status="restored";try{Save(state);}catch(Exception ex){report("The shortcut record could not be updated ("+ex.Message+").");}}
    }
    public static void Restore(string destination,bool failure,IntegrationLocations places=null,Action<string> report=null) {
        places=places??new IntegrationLocations();string path=Journal(destination);if(!File.Exists(path))return;
        var state=Engine.Json().Deserialize<IntegrationState>(File.ReadAllText(path,Engine.Utf8));if(state.status=="restored")return;
        if(!String.Equals(state.destination,destination,StringComparison.OrdinalIgnoreCase)){if(report!=null){report("The shortcut record belongs to another mod folder, so no shortcuts were touched.");return;}throw new Exception("Integration backup belongs to another mod folder.");}
        if(report!=null){RestoreLenient(destination,state,places,report);return;}
        string steamText=null,steamNext=null;
        if(state.steam!=null&&state.steam.touched) {
            places.RequireSteamClosed();string config=SteamPath(state.steam.path);steamText=File.ReadAllText(config,Engine.Utf8);bool exists;
            string current=Vdf.Get(steamText,state.steam.appId,out exists);
            bool original=exists==state.steam.existed&&current==state.steam.before;
            if(!original && (!exists||current!=state.steam.after))throw new Exception("Steam launch options changed after installation. Restore stopped to preserve that edit.");
            steamNext=Vdf.Set(steamText,state.steam.appId,state.steam.before,state.steam.existed);
        }
        foreach(var c in state.shortcuts.Where(x=>x.touched)) {
            if(!AllowedShortcut(c.path,places,state.game)||!Regex.IsMatch(c.backup,@"^shortcut-\d+\.lnk$"))throw new Exception("Unexpected shortcut backup path.");
            if(c.existed&&Engine.Hash(Engine.SafeJoin(destination,"_setup/"+c.backup))!=c.before)throw new Exception("Shortcut backup changed.");
            if(File.Exists(c.path)) {string h=Engine.Hash(c.path);if(!failure&&h!=c.after&&h!=c.before)throw new Exception("Shortcut changed after installation: "+c.path);}
        }
        if(steamNext!=null)WriteConfig(state.steam.path,steamText,steamNext);
        foreach(var c in state.shortcuts.Where(x=>x.touched)) {
            if(c.existed){if(File.Exists(c.path)&&Engine.Hash(c.path)==c.before)continue;Engine.EnsureParent(c.path);File.Copy(Engine.SafeJoin(destination,"_setup/"+c.backup),c.path,true);}
            else if(File.Exists(c.path))File.Delete(c.path);
        }
        foreach(string name in new[]{MenuFolder}.Concat(LegacyMenuFolders)){string menu=Path.Combine(places.Programs,name);if(Directory.Exists(menu)&&!Directory.EnumerateFileSystemEntries(menu).Any())Directory.Delete(menu,false);}
        state.status="restored";Save(state);
    }
}
}
