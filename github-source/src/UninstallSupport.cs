using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
namespace FalloutModSetup {
public class OwnedFiles { public string destination;public List<FileItem> files=new List<FileItem>(); }
// What an uninstall did, in plain numbers. Summary is the text shown on the uninstall screen and written to UNINSTALLED.txt.
public class UninstallResult {
    public int Removed,Kept,Failed,RuntimeKept,Missing,GameRestored,GameRemoved,GameKept,Directories,Junk,Scratch,SavesCopied,SavesAlready,SavesRenamed,SavesMoved;
    public bool HadReceipt=true,GameAlreadyRestored,Cancelled,FolderRemoved,SavesRequested,InPlace,HelperPending;
    public string Summary,DefenderNote,GameNote,Destination;
    public readonly List<string> KeptFiles=new List<string>(),FailedFiles=new List<string>(),LeftOver=new List<string>(),Notes=new List<string>();
    // Files that stay in the mod folder (saves, downloads, edited and new files) as relative paths; the folder is only removed completely when this is empty.
    public bool Complete {get{return Failed==0&&DefenderNote==null&&!Cancelled;}}
}
// What the uninstall window asks for. SavesTarget is the game's default save folder (Documents\My Games\FalloutNV\Saves) unless a test points it elsewhere.
// InPlaceHelper: the uninstaller runs from the setup helper inside the mod folder (the temporary copy was blocked); that file cannot be deleted while it runs, so its removal is postponed.
public class UninstallOptions { public bool MoveSaves; public string SavesTarget,InPlaceHelper; }
// Progress bar drawn in the installer's amber (the system bar is green with Windows visual styles). Same Value/Maximum use as ProgressBar, so a page can swap it in.
public class AmberProgressBar:Control {
    int value,maximum=100;
    public ProgressBarStyle Style {get;set;}
    public int Maximum {get{return maximum;}set{maximum=Math.Max(1,value);Invalidate();}}
    public int Value {get{return value;}set{this.value=Math.Max(0,Math.Min(maximum,value));Invalidate();}}
    public static readonly Color Track=Color.FromArgb(44,31,22),Edge=Color.FromArgb(79,43,27),Fill=Color.FromArgb(243,167,112);
    public AmberProgressBar(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true);TabStop=false;Height=16;}
    protected override void OnPaint(PaintEventArgs e) {
        var r=ClientRectangle;e.Graphics.Clear(Track);
        int w=(int)Math.Round((r.Width-2.0)*value/maximum);
        if(w>0)using(var b=new SolidBrush(Fill))e.Graphics.FillRectangle(b,1,1,w,r.Height-2);
        using(var p=new Pen(Edge))e.Graphics.DrawRectangle(p,0,0,r.Width-1,r.Height-1);
    }
}
public static class UninstallSupport {
    const string Receipt="_setup/installed-files.json";
    // Said on the uninstall screen and in the result: uninstall only ever touches the mod folder, the backed-up game files, the shortcuts and the Defender exclusions that setup recorded.
    public const string RuntimeNotice="Visual C++, DirectX, .NET and the other shared Microsoft runtimes are not removed. Other programs and games need them.";
    // Windows 'Installed apps' entry, one per mod folder. Checks point RegistryParent at a test key.
    // The environment variable lets a test run keep every registry write under a throw-away key instead of the real Installed apps list.
    public static string RegistryParent=Environment.GetEnvironmentVariable("FNVMF_REGISTRY_PARENT")??@"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    const string RegistryPrefix="FalloutNewVegasModFramework-";
    static readonly Regex SharedRuntime=new Regex(@"(^|/)add-software/|(^|/)(vc_?redist[^/]*|dxsetup[^/]*|dxwebsetup[^/]*|directx[^/]*|ndp[0-9][^/]*|netfx[^/]*|dotnet-?(runtime|sdk|hosting)[^/]*|windowsdesktop-runtime[^/]*)\.(exe|msi|cab|msu)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    static bool Personal(string relative) {var p=relative.Replace('\\','/').ToLowerInvariant();return p.StartsWith("downloads/")||p.StartsWith("overwrite/")||(p.StartsWith("profiles/")&&p.Split('/').Contains("saves"));}
    // A recorded path that names an installer for a shared Microsoft runtime (or the add-software folder) is never deleted, whatever the record says.
    public static bool IsSharedRuntime(string relative) { return SharedRuntime.IsMatch(relative.Replace('\\','/')); }
    // Where a .lnk points (null when it cannot be read).
    public static string ShortcutTarget(string lnk) {
        try {
            object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));object l=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{lnk});
            string target=(string)l.GetType().InvokeMember("TargetPath",BindingFlags.GetProperty,null,l,null);return String.IsNullOrEmpty(target)?null:target;
        } catch { return null; }
    }
    // The framework's own helper (in the mod folder or the temporary uninstall copy).
    public static bool PointsToHelper(string target) {
        if(String.IsNullOrEmpty(target))return false;string n=Path.GetFileName(target);
        return n.Equals("Setup and Restore.exe",StringComparison.OrdinalIgnoreCase)||n.Equals("Framework Uninstall.exe",StringComparison.OrdinalIgnoreCase);
    }
    // Windows itself, the system-wide program folders and the installer cache of the runtimes are never a mod folder.
    public static bool IsProtectedFolder(string folder) {
        string f=Engine.Full(folder).TrimEnd('\\'),win=Environment.GetFolderPath(Environment.SpecialFolder.Windows),data=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if(!String.IsNullOrEmpty(win)&&Engine.Within(f,win.TrimEnd('\\')))return true;
        if(!String.IsNullOrEmpty(data)&&Engine.Within(f,Path.Combine(data,"Package Cache")))return true;
        foreach(var sp in new[]{Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,Environment.SpecialFolder.CommonApplicationData}){string x=Environment.GetFolderPath(sp);if(!String.IsNullOrEmpty(x)&&f.Equals(x.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return true;}
        return false;
    }
    static IEnumerable<string> Files(string dir) {Engine.NoLinks(dir);foreach(var p in Directory.EnumerateFiles(dir)){Engine.NoLinks(p);yield return p;}foreach(var d in Directory.EnumerateDirectories(dir)){Engine.NoLinks(d);foreach(var p in Files(d))yield return p;}}
    // SHA-256 with a reusable buffer, reporting the bytes read so far. Tolerates another program holding the file open for reading.
    static string HashFile(string file,byte[] buffer,Action<long> read) {
        using(var h=SHA256.Create()) using(var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,81920)) {
            int n;long total=0;
            while((n=s.Read(buffer,0,buffer.Length))>0){h.TransformBlock(buffer,0,n,null,0);total+=n;if(read!=null)read(total);}
            h.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(h.Hash).Replace("-","").ToLowerInvariant();
        }
    }
    // At most one update per 60 ms keeps the window responsive however many small files there are.
    // Checks set PaceMilliseconds to 0 so every value is reported and step counts do not depend on wall time.
    public static int PaceMilliseconds=60;
    class Pace { readonly Stopwatch clock=Stopwatch.StartNew();bool first=true;public bool Due(bool force=false){if(force||first||clock.ElapsedMilliseconds>=PaceMilliseconds){first=false;clock.Restart();return true;}return false;} }
    static IEnumerable<FileInfo> Listing(DirectoryInfo dir) {
        foreach(var fi in dir.EnumerateFiles()){if((fi.Attributes&FileAttributes.ReparsePoint)!=0)throw new Exception("Symbolic link is not supported: "+fi.FullName);yield return fi;}
        foreach(var sub in dir.EnumerateDirectories()){if((sub.Attributes&FileAttributes.ReparsePoint)!=0)throw new Exception("Choose a real folder rather than a junction or symbolic link: "+sub.FullName);foreach(var fi in Listing(sub))yield return fi;}
    }
    public static TimeSpan ReuseMargin=TimeSpan.FromSeconds(3);
    public static string LastCapture="";
    public static void Capture(Engine e,string dest) {
        e.Progress("Recording installed files for safe removal",98);
        var receipt=new OwnedFiles{destination=dest};var list=new List<FileInfo>();long total=0;
        // One directory walk: size, write time and the link check come from the directory listing itself (no per-file path walk, which is slow with an antivirus scanner on 40,000 files).
        Engine.NoLinks(dest);
        foreach(FileInfo fi in Listing(new DirectoryInfo(dest))) {
            string relative=fi.FullName.Substring(dest.Length+1).Replace('\\','/');if(Personal(relative)||relative==Receipt)continue;
            list.Add(fi);total+=fi.Length;
        }
        var pace=new Pace();long done=0;byte[] buffer=new byte[1024*1024];int reused=0,hashed=0;
        foreach(FileInfo info in list) {
            string p=info.FullName,relative=p.Substring(dest.Length+1).Replace('\\','/');long before=done;
            // The receipt describes the actual installed bytes. A file that still has the size and write time the extraction recorded keeps the hash that was verified then;
            // anything else (edited INIs, profile lists, files made later) is read again. Files written in the last moments of the extraction are always read again,
            // so a later edit within the same clock tick can never be mistaken for the extracted copy.
            ExtractRecord known;string hash=null;
            lock(e.Extracted)if(e.Extracted.TryGetValue(relative,out known)&&known.size==info.Length&&known.ticks==info.LastWriteTimeUtc.Ticks&&e.ExtractedAtUtc-info.LastWriteTimeUtc>=ReuseMargin)hash=known.hash;
            if(hash==null){hash=HashFile(p,buffer,read=>{if(pace.Due())e.Fine(98+2.0*(before+read)/Math.Max(1,total));});hashed++;}else reused++;
            long size=info.Length;done+=size;
            receipt.files.Add(new FileItem{path=relative,hash=hash,size=size});
            if(pace.Due())e.Fine(98+2.0*done/Math.Max(1,total));
        }
        LastCapture=reused+" hashes reused from the extraction, "+hashed+" files read again";
        File.WriteAllText(Engine.SafeJoin(dest,Receipt),Engine.Json().Serialize(receipt),Engine.Utf8);
    }

    // ---- Windows 'Installed apps' entry (HKCU, so no administrator prompt) ----
    static string RegistryName(string dest) {
        using(var h=SHA256.Create())return RegistryPrefix+BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(Engine.Full(dest).ToLowerInvariant()))).Replace("-","").Substring(0,12).ToLowerInvariant();
    }
    // Adds (or refreshes) the entry that lists the framework under Windows Settings > Apps and lets Uninstall find the mod folder when a shortcut is broken. Returns false (and logs) when the registry refuses.
    // Call it once the setup helper and icon exist in the mod folder: after a fresh install, after a move/attach, after an update.
    public static bool WriteRegistry(string destination,Action<string> log=null) {
        try {
            string dest=Engine.Full(destination),helper=LaunchSupport.Helper(dest),icon=Engine.SafeJoin(dest,"_setup/Lucky38.ico");
            using(var k=Registry.CurrentUser.CreateSubKey(RegistryParent+"\\"+RegistryName(dest))) {
                k.SetValue("DisplayName","Fallout New Vegas - mod framework");
                k.SetValue("DisplayIcon",File.Exists(icon)?icon:helper+",0");
                k.SetValue("UninstallString","\""+helper+"\" --uninstall-ui --destination "+Engine.Q(dest));
                k.SetValue("InstallLocation",dest);
                k.SetValue("Publisher","Modification Framework");
                k.SetValue("InstallDate",DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("NoModify",1,RegistryValueKind.DWord);k.SetValue("NoRepair",1,RegistryValueKind.DWord);
            }
            return true;
        } catch(Exception ex) { if(log!=null)log("Windows Apps entry was not written: "+ex.Message);return false; }
    }
    // Removes the entry of this mod folder (and any entry that names it as its install location).
    public static void RemoveRegistry(string destination) {
        try {
            string dest=Engine.Full(destination);
            using(var parent=Registry.CurrentUser.OpenSubKey(RegistryParent,true)) {
                if(parent==null)return;
                foreach(string name in parent.GetSubKeyNames().Where(n=>n.StartsWith(RegistryPrefix,StringComparison.Ordinal))) {
                    bool mine=name==RegistryName(dest);
                    if(!mine)using(var k=parent.OpenSubKey(name))mine=String.Equals(Convert.ToString(k.GetValue("InstallLocation")).TrimEnd('\\','/'),dest,StringComparison.OrdinalIgnoreCase);
                    if(mine)parent.DeleteSubKeyTree(name,false);
                }
            }
        } catch {/* The entry is a convenience; a locked registry must never stop an uninstall. */}
    }
    // After a move: the old folder's entry goes, the new folder's entry is written.
    public static bool RelocateRegistry(string oldDestination,string newDestination,Action<string> log=null) {
        if(!String.IsNullOrWhiteSpace(oldDestination))try{RemoveRegistry(oldDestination);}catch{}
        return WriteRegistry(newDestination,log);
    }
    static bool HasRecords(string folder) {
        try{return File.Exists(Path.Combine(folder,"_setup","state.json"))||File.Exists(Path.Combine(folder,"_setup","installed-files.json"));}catch{return false;}
    }
    // Mod folders listed under Windows Apps that still exist and still have their setup records.
    public static List<string> RegisteredFolders() {
        var found=new List<string>();
        try {
            using(var parent=Registry.CurrentUser.OpenSubKey(RegistryParent)) {
                if(parent==null)return found;
                foreach(string name in parent.GetSubKeyNames().Where(n=>n.StartsWith(RegistryPrefix,StringComparison.Ordinal)))
                    using(var k=parent.OpenSubKey(name)){string loc=Convert.ToString(k.GetValue("InstallLocation"));if(!String.IsNullOrWhiteSpace(loc)&&Directory.Exists(loc)&&HasRecords(loc))found.Add(loc);}
            }
        } catch {}
        return found;
    }
    // The mod folder to uninstall: the one named on the command line; else the folder this helper lives in (the folder was moved after the shortcut was made); else the one Windows Apps lists.
    public static string Locate(string destination,string self) {
        if(!String.IsNullOrWhiteSpace(destination))try{string d=Engine.Full(destination);if(HasRecords(d))return d;}catch{}
        string beside=PortableSupport.InstalledFolderOf(self);if(beside!=null&&HasRecords(beside))return Engine.Full(beside);
        var listed=RegisteredFolders();
        if(listed.Count==1)return Engine.Full(listed[0]);
        if(!String.IsNullOrWhiteSpace(destination)){try{RemoveRegistry(destination);}catch{}}
        throw new Exception(listed.Count>1?"More than one Fallout New Vegas modification folder is registered. Start Uninstall from the shortcut or the _setup folder of the one you want to remove.":"The mod folder was not found"+(String.IsNullOrWhiteSpace(destination)?"":" at "+destination)+". Start Uninstall from the _setup folder inside the mod folder, or from Windows Settings > Apps.");
    }

    // A problem that would stop Uninstall, found before the user presses the button (null when the setup looks fine).
    public static string Inspect(string destination) {
        try {
            string dest=Engine.Full(destination);
            if(IsProtectedFolder(dest))return "This folder belongs to Windows or to shared software and cannot be uninstalled here.";
            string p=Engine.SafeJoin(dest,"_setup/state.json");
            if(!File.Exists(p))return HasRecords(dest)?null:"This folder has no installer records. Select the mod folder that contains _setup.";
            var s=Engine.Json().Deserialize<InstallState>(File.ReadAllText(p,Engine.Utf8));
            if(!String.Equals(Engine.Full(s.destination),dest,StringComparison.OrdinalIgnoreCase))return "This mod folder was moved. Run \""+PortableSupport.RepairCommand+"\" in it first, then uninstall.";
            return null;
        } catch(Exception ex) { return ex.Message; }
    }

    static string Short(string path){return path.Length<=70?path:"..."+path.Substring(path.Length-67);}

    // ---- the game's own save folder, and moving the mod folder's saves there ----
    static readonly string[] SaveExtensions={".fos",".nvse",".bak",".ess"};
    // Documents\My Games\FalloutNV\Saves: where the game keeps its saves without Mod Organizer. Tests replace it.
    public static string DefaultSavesFolder() {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"My Games","FalloutNV","Saves");
    }
    static bool IsLink(string path){try{return (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;}catch{return false;}}
    // Save and co-save files (.fos, .nvse, .bak, .ess) in each profile's saves folder and in overwrite\__MO_Saves, top level only, never through a link.
    public static List<string> FindSaves(string destination) {
        string dest=Engine.Full(destination);var list=new List<string>();
        Action<string> take=folder=>{
            if(!Directory.Exists(folder)||IsLink(folder))return;
            foreach(var p in Directory.EnumerateFiles(folder))if(!IsLink(p)&&SaveExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))list.Add(p);
        };
        string profiles=Path.Combine(dest,"profiles");
        if(Directory.Exists(profiles)&&!IsLink(profiles))foreach(var d in Directory.EnumerateDirectories(profiles).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))if(!IsLink(d))take(Path.Combine(d,"saves"));
        take(Path.Combine(dest,"overwrite","__MO_Saves"));
        return list;
    }
    // "Save 3 ....fos", "Save 3 ....nvse" and "Save 3 ....fos.bak" belong together: they share this key and are renamed together.
    static string SaveKey(string name) {
        string key=name;bool again=true;
        while(again){again=false;foreach(var ext in SaveExtensions)if(key.EndsWith(ext,StringComparison.OrdinalIgnoreCase)&&key.Length>ext.Length){key=key.Substring(0,key.Length-ext.Length);again=true;}}
        return key;
    }
    // Copies the saves to the target, verifies every copy by hash, and only then deletes the originals. A different file that is already there is never overwritten:
    // the saves are stored beside it with " (from mod folder)" added to the name (all files of one save together, so the co-save still matches).
    static void MoveSaves(Engine engine,string dest,string target,UninstallResult result,Action<string,double> step) {
        var files=FindSaves(dest);if(files.Count==0)return;
        string targetFolder=Engine.Full(target);Directory.CreateDirectory(targetFolder);
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);byte[] buffer=new byte[1024*1024];
        Func<string,string> hash=p=>{string h;if(!hashes.TryGetValue(p,out h)){h=HashFile(p,buffer,null);hashes[p]=h;}return h;};
        var groups=files.GroupBy(p=>Path.GetDirectoryName(p).ToLowerInvariant()+"|"+SaveKey(Path.GetFileName(p)).ToLowerInvariant()).ToList();
        int done=0;
        foreach(var g in groups) {
            engine.Cancel.ThrowIfCancellationRequested();
            var members=g.ToList();string key=SaveKey(Path.GetFileName(members[0]));
            for(int n=0;;n++) {
                string suffix=n==0?"":n==1?" (from mod folder)":" (from mod folder "+n+")";
                var plan=members.Select(p=>{string name=Path.GetFileName(p);return Tuple.Create(p,Path.Combine(targetFolder,key+suffix+name.Substring(key.Length)));}).ToList();
                if(plan.Any(t=>File.Exists(t.Item2)&&HashFile(t.Item2,buffer,null)!=hash(t.Item1)))continue;
                foreach(var t in plan) {
                    if(File.Exists(t.Item2)){result.SavesAlready++;continue;}
                    File.Copy(t.Item1,t.Item2,false);
                    if(HashFile(t.Item2,buffer,null)!=hash(t.Item1)){try{File.Delete(t.Item2);}catch{}throw new Exception("A save copy could not be verified: "+Path.GetFileName(t.Item1)+". Nothing was removed.");}
                    result.SavesCopied++;
                }
                if(n>0)result.SavesRenamed++;
                break;
            }
            done+=members.Count;step("Copying saves to the game's save folder: "+done+" of "+files.Count+"  "+Short(Path.GetFileName(members[0])),1+2.0*done/files.Count);
        }
        // Every copy is verified: the saves count as moved, so the originals go.
        foreach(var p in files){try{if((File.GetAttributes(p)&FileAttributes.ReadOnly)!=0)File.SetAttributes(p,FileAttributes.Normal);File.Delete(p);result.SavesMoved++;}catch(Exception ex){result.Failed++;result.FailedFiles.Add(p.Substring(dest.Length+1)+" ("+ex.Message+")");}}
    }

    // ---- junk the installer, Mod Organizer and Python leave behind ----
    sealed class Entry { public string Path,Rel;public bool Link; }
    // Every file and link under the folder, relative paths with forward slashes. Links are listed, never entered.
    static IEnumerable<Entry> Walk(string root,string dir=null) {
        dir=dir??root;
        IEnumerable<string> files,dirs;
        try{files=Directory.EnumerateFiles(dir).ToList();dirs=Directory.EnumerateDirectories(dir).ToList();}catch{yield break;}
        foreach(var f in files)yield return new Entry{Path=f,Rel=f.Substring(root.Length+1).Replace('\\','/'),Link=IsLink(f)};
        foreach(var d in dirs) {
            if(IsLink(d)){yield return new Entry{Path=d,Rel=d.Substring(root.Length+1).Replace('\\','/'),Link=true};continue;}
            foreach(var e in Walk(root,d))yield return e;
        }
    }
    static bool IsSaveOrDownload(string rel){var p=rel.ToLowerInvariant();return p.StartsWith("downloads/")||(p.StartsWith("profiles/")&&p.Split('/').Contains("saves"));}
    // Recognised junk: Python cache, log and crash files, Mod Organizer's log/web cache folders, the old UNINSTALLED.txt and, once everything else went well, the installer's own _setup folder.
    // Saves and downloads are never junk; a recorded runtime installer is never touched.
    public static bool IsJunk(string rel,bool scratchToo) {
        string p=rel.Replace('\\','/').ToLowerInvariant();
        if(IsSaveOrDownload(p)||IsSharedRuntime(p))return false;
        string ext=Path.GetExtension(p);
        if(ext==".pyc"||ext==".pyo"||ext==".log"||ext==".dmp")return true;
        if(p.Split('/').Contains("__pycache__"))return true;
        if(p.StartsWith("logs/")||p.StartsWith("webcache/")||p.StartsWith("crashdumps/"))return true;
        if(p=="uninstalled.txt")return true;
        return scratchToo&&p.StartsWith("_setup/");
    }
    static void RemoveJunk(string dest,UninstallResult result,bool scratchToo,string helper=null,string helperFile=null) {
        foreach(var e in Walk(dest).ToList()) {
            if(e.Link||!IsJunk(e.Rel,scratchToo))continue;
            if(helper!=null&&String.Equals(e.Path,helper,StringComparison.OrdinalIgnoreCase)){result.HelperPending=true;continue;}
            try{if((File.GetAttributes(e.Path)&FileAttributes.ReadOnly)!=0)File.SetAttributes(e.Path,FileAttributes.Normal);File.Delete(e.Path);result.Junk++;}
            catch(Exception ex){if(helperFile!=null&&String.Equals(e.Path,helperFile,StringComparison.OrdinalIgnoreCase))result.HelperPending=true;else{result.Failed++;result.FailedFiles.Add(e.Rel+" ("+ex.Message+")");}}
        }
    }
    // Removes a folder tree without following links: a junction or symbolic link inside is removed itself, its target is never entered. Returns how many items could not be removed.
    static int DeleteTree(string dir) {
        int failed=0;
        foreach(var f in Directory.EnumerateFiles(dir).ToList()){try{if((File.GetAttributes(f)&FileAttributes.ReadOnly)!=0)File.SetAttributes(f,FileAttributes.Normal);File.Delete(f);}catch{failed++;}}
        foreach(var d in Directory.EnumerateDirectories(dir).ToList()) {
            try{if(IsLink(d))Directory.Delete(d,false);else{failed+=DeleteTree(d);}}catch{failed++;}
        }
        try{Directory.Delete(dir,false);}catch{failed++;}
        return failed;
    }
    // The 'Delete the whole mod folder' choice after an uninstall that left the user's own files behind. Only the folder the result names; never outside it, never through a link.
    public static int DeleteModFolder(UninstallResult result) {
        if(result==null||String.IsNullOrEmpty(result.Destination)||result.FolderRemoved)return 0;
        string dest=Engine.Full(result.Destination);
        string self=typeof(Engine).Assembly.Location;bool inside=Engine.Within(self,dest);
        if(IsProtectedFolder(dest)||(inside&&!result.InPlace))throw new Exception("This folder cannot be deleted from here.");
        if(!Directory.Exists(dest)){result.FolderRemoved=true;return 0;}
        int failed=DeleteTree(dest);result.FolderRemoved=!Directory.Exists(dest);
        // Running from inside the folder: the helper EXE is the one thing that could not go; the summary tells the user to delete what is left.
        if(!result.FolderRemoved&&inside){failed=Math.Max(0,failed-1);result.HelperPending=true;}
        RemoveRegistry(dest);return failed;
    }

    public static UninstallResult Remove(Engine engine,string destination) { return Remove(engine,destination,null); }
    public static UninstallResult Remove(Engine engine,string destination,UninstallOptions options) {
        var result=new UninstallResult();options=options??new UninstallOptions();
        string dest=Engine.Full(destination),path=Engine.SafeJoin(dest,Receipt);result.Destination=dest;
        if(IsProtectedFolder(dest))throw new Exception("This folder belongs to Windows or to shared software and cannot be uninstalled here.");
        Action<string,double> step=(text,percent)=>{engine.Progress(text,(int)percent);engine.Fine(percent);};
        if(!HasRecords(dest)) {
            // A finished uninstall removes its own records: repeating it has nothing to do and changes nothing.
            RemoveRegistry(dest);
            result.Summary="Nothing to uninstall: this folder has no setup records any more, so the framework was already removed.";result.FolderRemoved=!Directory.Exists(dest);
            step(result.Summary,100);return result;
        }
        step("Reading the uninstall record",1);
        OwnedFiles receipt=null;var targets=new List<Tuple<FileItem,string>>();
        result.HadReceipt=File.Exists(path);
        if(result.HadReceipt) {
            receipt=Engine.Json().Deserialize<OwnedFiles>(File.ReadAllText(path,Engine.Utf8));
            if(receipt==null||!String.Equals(receipt.destination,dest,StringComparison.OrdinalIgnoreCase)||receipt.files==null)throw new Exception("Uninstall receipt belongs to another folder. If the mod folder was moved, run \""+PortableSupport.RepairCommand+"\" in it first.");
            // Check every recorded path before any mutation; never follow a junction or remove a whole directory tree.
            if(receipt.files.Select(f=>f.path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=receipt.files.Count)throw new Exception("Duplicate uninstall paths.");
            foreach(var f in receipt.files){if(String.IsNullOrEmpty(f.path)||f.path.Replace('\\','/').Split('/').Any(n=>n=="."||n==".."||n.Length==0))throw new Exception("Uninstall record contains a non-canonical path.");if(Personal(f.path))throw new Exception("Uninstall record includes personal files.");targets.Add(Tuple.Create(f,Engine.SafeJoin(dest,f.path)));}
        }
        string self=typeof(Engine).Assembly.Location,helper=options.InPlaceHelper,helperFile=LaunchSupport.Helper(dest);
        if(Engine.Within(self,dest)&&(helper==null||!String.Equals(helper,self,StringComparison.OrdinalIgnoreCase)))throw new Exception("Start Uninstall through its shortcut so the removal tool can run outside the mod folder.");
        result.InPlace=helper!=null;
        // The saves first, before anything is removed: 1% to 3%. A stop here leaves the mod folder untouched (copies already made are harmless).
        result.SavesRequested=options.MoveSaves;
        try {
            if(options.MoveSaves)MoveSaves(engine,dest,options.SavesTarget??DefaultSavesFolder(),result,step);
            engine.Cancel.ThrowIfCancellationRequested();
        } catch(OperationCanceledException) { return Stopped(engine,result,"Stopped before anything was removed."); }
        // Game files, Defender exclusions, shortcuts and Steam options: 3% to 15%. Edited game files are reported and kept; they do not stop the uninstall.
        var shown=engine.Progress;var wasKeeping=engine.KeepChangedGameFiles;engine.KeepChangedGameFiles=true;
        engine.Progress=(text,n)=>{double p=Math.Min(15,3+12*Math.Max(0,Math.Min(100,n))/100.0);shown(text,(int)p);engine.Fine(p);};
        try{engine.Restore(dest);}finally{engine.Progress=shown;engine.KeepChangedGameFiles=wasKeeping;}
        result.GameRestored=engine.GameRestored.Count;result.GameRemoved=engine.GameRemoved.Count;result.GameKept=engine.GameKept.Count;result.GameAlreadyRestored=engine.GameAlreadyRestored;
        result.DefenderNote=engine.DefenderNote;result.GameNote=engine.GameNote;result.Notes.AddRange(engine.IntegrationNotes);
        step("Original game files restored",15);
        // The installer-owned files: 15% to 95%, by bytes, so the number moves smoothly while large archives are hashed.
        if(result.HadReceipt) {
            long total=0;foreach(var t in targets)total+=Math.Max(0,t.Item1.size)+65536;
            var pace=new Pace();long done=0;byte[] buffer=new byte[1024*1024];int index=0;
            Func<long,double> at=bytes=>15+80.0*Math.Min(total,bytes)/Math.Max(1,total);
            foreach(var t in targets) {
                var f=t.Item1;string p=t.Item2;index++;long weight=Math.Max(0,f.size)+65536,before=done;
                // Stopping waits for the file in hand, never cuts one in half.
                if(engine.Cancel.IsCancellationRequested){result.Cancelled=true;break;}
                if(pace.Due())step("Removing framework files: "+index+" of "+targets.Count+"  "+Short(f.path),at(before));
                try {
                    if(!File.Exists(p)){result.Missing++;continue;}
                    if(helper!=null&&String.Equals(p,helper,StringComparison.OrdinalIgnoreCase)){result.HelperPending=true;continue;}
                    if(IsSharedRuntime(f.path)){result.RuntimeKept++;continue;}
                    bool scratch=f.path.StartsWith("_setup/",StringComparison.OrdinalIgnoreCase);
                    if(f.size>0&&new FileInfo(p).Length!=f.size){if(scratch)result.Scratch++;else{result.Kept++;result.KeptFiles.Add(f.path);}continue;}
                    if(HashFile(p,buffer,read=>{if(pace.Due())engine.Fine(at(before+read));})!=f.hash){if(scratch)result.Scratch++;else{result.Kept++;result.KeptFiles.Add(f.path);}continue;}
                    if((File.GetAttributes(p)&FileAttributes.ReadOnly)!=0)File.SetAttributes(p,FileAttributes.Normal);
                    File.Delete(p);result.Removed++;
                } catch(Exception ex) {
                    // The setup helper itself may still be running for a moment (it started this window): its removal is postponed, not a failure.
                    if(String.Equals(p,helperFile,StringComparison.OrdinalIgnoreCase))result.HelperPending=true;
                    // One locked file (MO2 or the game still open) must not abandon the rest; the summary says what is left and Uninstall can be run again.
                    else{result.Failed++;result.FailedFiles.Add(f.path+" ("+ex.Message+")");}
                } finally { done+=weight; }
            }
        }
        if(result.Cancelled)return Stopped(engine,result,"Stopped by you.");
        // Junk, empty folders and (when nothing else is left) the folder itself: 95% to 100%.
        step("Removing leftover cache and log files",95);
        bool clean=result.Failed==0&&result.DefenderNote==null;
        RemoveJunk(dest,result,clean&&result.Failed==0,helper,helperFile);
        step("Removing empty folders",97);
        // Empty directories only. Any saves, downloads, new files, changed files or links stay in place.
        result.Directories=EmptyDirectories(dest);
        if(result.Failed==0&&result.DefenderNote==null){step("Removing the entry from Windows Settings > Apps",98);RemoveRegistry(dest);}
        // What is still there belongs to the user (or could not be removed): the folder stays and the window says exactly what.
        foreach(var e in Walk(dest)){if((helper!=null&&String.Equals(e.Path,helper,StringComparison.OrdinalIgnoreCase))||(result.HelperPending&&String.Equals(e.Path,helperFile,StringComparison.OrdinalIgnoreCase))){result.HelperPending=true;continue;}result.LeftOver.Add(e.Rel);}
        result.LeftOver.Sort(StringComparer.OrdinalIgnoreCase);
        if(result.LeftOver.Count==0&&result.Failed==0&&!result.HelperPending){
            try{Directory.Delete(dest,false);result.FolderRemoved=true;}catch{}
        }
        result.Summary=Summarize(result);
        if(!result.FolderRemoved&&!(result.HelperPending&&result.LeftOver.Count==0))try{File.WriteAllText(Path.Combine(dest,"UNINSTALLED.txt"),Report(result,engine)+Environment.NewLine,Engine.Utf8);}catch{}
        step(result.Summary,100);
        return result;
    }
    static UninstallResult Stopped(Engine engine,UninstallResult result,string why) {
        result.Cancelled=true;
        var t=new StringBuilder(why+" Nothing is half-done: every file is either removed or still in place.");
        if(result.SavesCopied+result.SavesAlready>0)t.Append("\r\nSaves already copied to the game's save folder: "+(result.SavesCopied+result.SavesAlready)+(result.SavesMoved>0?" (the copies in the mod folder were removed)":" (the mod folder's copies are still there)")+".");
        if(result.Removed+result.GameRestored+result.GameRemoved>0||result.GameAlreadyRestored)t.Append("\r\nDone so far: "+(result.GameAlreadyRestored?"original game files restored; ":result.GameRestored>0?result.GameRestored+" original game files restored; ":"")+result.Removed+" framework files removed.");
        t.Append("\r\nPress Start again to continue where it stopped. "+RuntimeNotice);
        result.Summary=t.ToString();return result;
    }
    // Plain wording for the screen: what was restored, what was removed, what stayed and why.
    public static string Summarize(UninstallResult r) {
        var t=new StringBuilder();
        t.Append(r.Failed==0?(r.FolderRemoved?"Framework uninstalled and the mod folder removed.":"Framework uninstalled."):"Framework uninstalled, but "+r.Failed+" files could not be removed (a program probably still uses them). Close the game and Mod Organizer, then run Uninstall again.");
        if(r.SavesRequested)t.Append("\r\nSaves: "+r.SavesCopied+" copied to the game's save folder"+(r.SavesAlready>0?", "+r.SavesAlready+" already there identical":"")+(r.SavesRenamed>0?", "+r.SavesRenamed+" saved under a new name because a different file was already there (\"(from mod folder)\")":"")+"; all verified by hash.");
        if(r.GameNote!=null)t.Append("\r\n"+r.GameNote);
        else if(r.GameAlreadyRestored)t.Append("\r\nGame folder: the original files were already restored earlier.");
        else t.Append("\r\nGame folder: "+r.GameRestored+" original files restored from the backup"+(r.GameRemoved>0?", "+r.GameRemoved+" files added by setup removed":"")+(r.GameKept>0?", "+r.GameKept+" edited files kept":"")+".");
        if(r.HadReceipt)t.Append("\r\nMod folder: "+r.Removed+" unchanged framework files removed"+(r.Junk>0?", "+r.Junk+" cache, log and setup files cleaned up":"")+"; "+r.Kept+" edited files kept.");
        else t.Append("\r\nThis older installation has no file list, so the files in the mod folder were kept.");
        if(!r.FolderRemoved&&r.LeftOver.Count>0) {
            t.Append("\r\nThe mod folder was kept because "+r.LeftOver.Count+" of your files are still in it:");
            foreach(var g in Groups(r))t.Append("\r\n  "+g.Value+" "+g.Key);
            foreach(var x in r.LeftOver.Take(25))t.Append("\r\n    "+x.Replace('/','\\'));
            if(r.LeftOver.Count>25)t.Append("\r\n    ... and "+(r.LeftOver.Count-25)+" more (see UNINSTALLED.txt)");
        }
        foreach(var n in r.Notes)t.Append("\r\nShortcuts and Steam: "+n);
        if(r.DefenderNote!=null)t.Append("\r\n"+r.DefenderNote);
        if(r.HelperPending)t.Append(r.LeftOver.Count==0?"\r\nWindows cannot delete a program while it runs. Close this window and delete the folder "+r.Destination+" yourself (it only holds the uninstaller now).":"\r\nWindows cannot delete a program while it runs. Close this window and delete the folder "+Path.Combine(r.Destination,"_setup")+" yourself (it only holds the uninstaller now).");
        t.Append("\r\n"+RuntimeNotice);
        return t.ToString();
    }
    // Left-over files by kind, for the summary.
    static List<KeyValuePair<string,int>> Groups(UninstallResult r) {
        var kept=new HashSet<string>(r.KeptFiles,StringComparer.OrdinalIgnoreCase);var d=new Dictionary<string,int>();
        foreach(var rel in r.LeftOver) {
            string p=rel.ToLowerInvariant();
            string kind=p.StartsWith("profiles/")&&p.Split('/').Contains("saves")?"save files":p.StartsWith("downloads/")?"downloaded files":p.StartsWith("overwrite/")?"files in Mod Organizer's overwrite folder":kept.Contains(rel)?"framework files you edited":IsSharedRuntime(p)?"runtime installers (never removed)":"new files";
            int n;d.TryGetValue(kind,out n);d[kind]=n+1;
        }
        return d.ToList();
    }
    static string Report(UninstallResult r,Engine e) {
        var t=new StringBuilder(r.Summary);
        Action<string,IEnumerable<string>> list=(title,items)=>{var a=items.ToList();if(a.Count==0)return;t.Append("\r\n\r\n"+title+" ("+a.Count+"):");foreach(var x in a.Take(500))t.Append("\r\n  "+x);if(a.Count>500)t.Append("\r\n  ... and "+(a.Count-500)+" more");};
        list("Game files restored from the backup",e.GameRestored);list("Files setup had added to the game folder, removed",e.GameRemoved);list("Game files edited after installation, kept",e.GameKept);
        list("Mod folder files edited after installation, kept",r.KeptFiles);list("Everything still in the mod folder",r.LeftOver);list("Mod folder files that could not be removed",r.FailedFiles);
        return t.ToString();
    }
    static int EmptyDirectories(string root) {
        int removed=0;
        foreach(string d in Directory.EnumerateDirectories(root).ToList()){if((File.GetAttributes(d)&FileAttributes.ReparsePoint)!=0)continue;removed+=EmptyDirectories(d);if(!Directory.EnumerateFileSystemEntries(d).Any()){try{Directory.Delete(d,false);removed++;}catch{}}}
        return removed;
    }
    static string TempRoot(){return Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');}
    const string CopyPrefix="FNV-Framework-Uninstall-";
    // The folder of this running EXE when it is a temporary uninstall copy, else null.
    static string OwnTempCopy() {
        try{string dir=Path.GetFullPath(Path.GetDirectoryName(typeof(Engine).Assembly.Location)).TrimEnd('\\');return Path.GetFileName(dir).StartsWith(CopyPrefix,StringComparison.Ordinal)&&String.Equals(Path.GetDirectoryName(dir),TempRoot(),StringComparison.OrdinalIgnoreCase)?dir:null;}catch{return null;}
    }
    // Earlier uninstall runs left their EXE copy in %TEMP% (a running program cannot delete itself): remove the other copies older than an hour, never this run's own. A copy that is still running stays until the next run.
    public static void CleanOldCopies(TimeSpan? olderThan=null) {
        TimeSpan age=olderThan??TimeSpan.FromHours(1);string own=OwnTempCopy();
        try{foreach(var d in Directory.GetDirectories(TempRoot(),CopyPrefix+"*")){if(own!=null&&String.Equals(Path.GetFullPath(d).TrimEnd('\\'),own,StringComparison.OrdinalIgnoreCase))continue;if(DateTime.UtcNow-Directory.GetCreationTimeUtc(d)>age)try{Directory.Delete(d,true);}catch{}}}catch{}
    }
    public const string BlockedMessage="Windows Security (Microsoft Defender or another antivirus) blocked or removed the temporary copy of the uninstaller. This is a false alarm on setup's own program.\r\n\r\nTo let it run: open Windows Security > Virus & threat protection > Protection history, select the blocked item and choose Actions > Allow on device. Protection stays on; nothing needs to be turned off.\r\n\r\nUninstall will now run from the setup folder instead. The mod folder is removed right after you close the window.";
    // Starts the temporary copy; false when it could not be started or disappeared at once (antivirus).
    // Check hook: called with the new copy's path right after it was verified (a check deletes it here, the way an antivirus would).
    public static Action<string> CopyCreated;
    public static bool TryStartCopy(string self,string dest,out string reason) {
        reason=null;string dir=Path.Combine(Path.GetTempPath(),CopyPrefix+Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(dir);Engine.NoLinks(dir);
            string copy=Path.Combine(dir,"Framework Uninstall.exe");File.Copy(self,copy);if(!File.Exists(copy)||Engine.Hash(copy)!=Engine.Hash(self)){reason="the copy failed verification";return false;}
            if(CopyCreated!=null)CopyCreated(copy);
            var p=Process.Start(new ProcessStartInfo(copy,"--uninstall-ui --destination "+Engine.Q(dest)){UseShellExecute=true,WorkingDirectory=dir});
            // Wait only until the new window is up (a second at most in practice), so this helper is gone before anyone can press Start.
            // A blocked or deleted program ends at once or its file vanishes.
            try{p.WaitForInputIdle(4000);}catch(InvalidOperationException){}
            if(p.HasExited&&(p.ExitCode!=0||!File.Exists(copy))){reason="the copy ended at once (exit code "+p.ExitCode+")";return false;}
            if(!File.Exists(copy)){reason="the copy was deleted";return false;}
            return true;
        } catch(Exception ex) { reason=ex.Message;return false; }
    }
    public static void Show(string destination) {
        string self=typeof(Engine).Assembly.Location,dest=Locate(destination,self);bool inside=Engine.Within(self,dest);
        if(inside) {
            // A mod folder moved by hand is repaired first (with consent, like Play), so the records match before anything is removed.
            try{PortableSupport.Run(dest,"",false,true,null,null,false);}catch{/* the uninstall screen explains what is still wrong */}
            CleanOldCopies();string reason;
            if(TryStartCopy(self,dest,out reason))return;
            MessageBox.Show(BlockedMessage+"\r\n\r\n("+reason+")","Uninstall: the temporary copy was blocked",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new UninstallForm(dest){InPlace=inside});
    }
}
// Test hook: lay the window out as if the screen had another DPI scale and another working area.
public class UninstallLook { public float Scale; public Size Area; }
// The uninstall window. Everything is laid out in tables and flow panels with DPI-scaled sizes, the window is never taller than the screen's working area,
// and the long texts live in scrolling panels, so the progress bar, the percentage and the buttons stay visible at any scale.
public class UninstallForm:Form {
    static readonly Color Background=Color.FromArgb(26,19,13),Surface=Color.FromArgb(44,31,22),Ink=Color.FromArgb(235,229,215),Muted=Color.FromArgb(170,173,166),Amber=Color.FromArgb(243,167,112),Line=Color.FromArgb(79,43,27);
    readonly string destination;readonly float scale,system;readonly Rectangle area;
    readonly AmberProgressBar progress=new AmberProgressBar();
    readonly Label title=new Label(),pathLabel=new Label(),info=new Label(),percent=new Label(),status=new Label();
    readonly Panel infoPanel=new Panel(),statusPanel=new Panel();
    readonly CheckBox saves=new CheckBox(),deleteAll=new CheckBox();readonly Label savesNote=new Label();
    readonly Button start=new Button(),cancel=new Button(),finish=new Button();
    readonly Panel options=new Panel(),buttons=new Panel();
    readonly TableLayoutPanel root=new TableLayoutPanel();
    double shown;bool busy,finished;CancellationTokenSource stop;UninstallResult last;
    // Asked before the whole mod folder is deleted; a check answers it without a dialog.
    public Func<string,bool> Confirm;
    // Set when the window runs from the helper inside the mod folder (the temporary copy was blocked): the helper's own removal is postponed to after the window closes.
    public bool InPlace;
    // Where saves are copied to when the box is ticked (null: the game's default folder). A check points it at a fixture.
    public string SavesTarget;
    public string PercentText {get{return percent.Text;}}
    public string StatusText {get{return status.Text;}}
    public bool Busy {get{return busy;}}
    public bool Finished {get{return finished;}}
    int S(int v){return (int)Math.Round(v*scale);}
    Font F(float points,FontStyle style=FontStyle.Regular){return new Font("Segoe UI",points*scale/system,style);}
    public UninstallForm(string destination,UninstallLook look=null) {
        this.destination=destination;
        using(var g=Graphics.FromHwnd(IntPtr.Zero))system=g.DpiX/96f;
        scale=look!=null&&look.Scale>0?look.Scale:system;
        area=look!=null&&!look.Area.IsEmpty?new Rectangle(Point.Empty,look.Area):Screen.PrimaryScreen.WorkingArea;
        Text="Uninstall | Fallout New Vegas Modification Framework";
        using(var icon=typeof(Engine).Assembly.GetManifestResourceStream("Lucky38.ico"))Icon=new Icon(icon);
        ShowInTaskbar=true;AutoScaleMode=AutoScaleMode.None;Font=F(10);BackColor=Background;ForeColor=Ink;FormBorderStyle=FormBorderStyle.Sizable;MaximizeBox=true;StartPosition=FormStartPosition.Manual;
        ClientSize=new Size(S(760),S(660));
        if(Width>area.Width)Width=area.Width;if(Height>area.Height)Height=area.Height;
        MinimumSize=new Size(Math.Min(S(560),area.Width),Math.Min(S(420),area.Height));
        Location=new Point(area.X+(area.Width-Width)/2,area.Y+(area.Height-Height)/2);

        root.Dock=DockStyle.Fill;root.ColumnCount=1;root.RowCount=8;root.Padding=new Padding(S(28),S(16),S(28),S(14));root.BackColor=Background;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,55));root.RowStyles.Add(new RowStyle(SizeType.Absolute,S(60)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,S(28)));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,45));root.RowStyles.Add(new RowStyle(SizeType.Absolute,S(56)));
        Style(title,"Remove Modification Framework",F(20,FontStyle.Bold),Amber);title.Margin=new Padding(0,0,0,S(2));
        Style(pathLabel,destination,F(9),Muted);pathLabel.Margin=new Padding(0,0,0,S(4));
        Style(info,"This puts back the original game files that setup backed up (FalloutNV.exe, the xNVSE files and Fallout - Invalidation.bsa if your game had it), removes the shortcuts and Microsoft Defender exclusions that setup added, and removes the unchanged framework files from this mod folder. Cache and log files left behind are cleaned up too.\r\n\r\nYour saves, downloads, new files and anything you edited are kept, unless you choose otherwise below.\r\n\r\n"+UninstallSupport.RuntimeNotice+"\r\n\r\nClose the game and Mod Organizer first. Nothing is changed until you press Start.",Font,Ink);
        Style(status,"",Font,Ink);Scroller(infoPanel,info);Scroller(statusPanel,status);
        string target=SavesTarget??UninstallSupport.DefaultSavesFolder();int found=0;try{found=UninstallSupport.FindSaves(destination).Count;}catch{}
        saves.AutoSize=false;saves.UseMnemonic=false;saves.ForeColor=Ink;saves.BackColor=Color.Transparent;saves.Margin=new Padding(0,S(2),0,S(2));
        saves.Text="Move my save files to the game's default folder";savesNote.Text="Saves go to "+target+(found==0?". No save files were found in the mod folder.":". "+found+" save files found. Each copy is verified; a different file that is already there is never overwritten.");saves.Enabled=found>0;
        deleteAll.AutoSize=false;deleteAll.UseMnemonic=false;deleteAll.ForeColor=Ink;deleteAll.BackColor=Color.Transparent;deleteAll.Margin=new Padding(0,S(2),0,S(2));deleteAll.Visible=false;
        deleteAll.Text="Delete the whole mod folder, including my edited and new files (cannot be undone)";
        options.Dock=DockStyle.Fill;options.BackColor=Color.Transparent;options.Margin=new Padding(0,S(4),0,S(4));Style(savesNote,savesNote.Text,F(9),Muted);savesNote.Margin=new Padding(S(22),0,0,S(4));options.Controls.Add(saves);options.Controls.Add(savesNote);options.Controls.Add(deleteAll);
        progress.Dock=DockStyle.Fill;progress.Maximum=10000;progress.Margin=new Padding(0,S(4),0,S(4));
        Style(percent,"00.00%",F(11,FontStyle.Bold),Amber);percent.Margin=new Padding(0,0,0,S(2));
        string problem=UninstallSupport.Inspect(destination);status.Text=problem??"Ready. Nothing has been changed yet. Press Start to begin, or Cancel to close this window.";
        buttons.Dock=DockStyle.Fill;buttons.BackColor=Color.Transparent;buttons.Margin=new Padding(0,S(8),0,0);buttons.SizeChanged+=delegate{PlaceButtons();};options.SizeChanged+=delegate{PlaceOptions();};
        StyleButton(finish,"Finish",true);StyleButton(start,"Start",true);StyleButton(cancel,"Cancel",false);finish.Visible=false;
        buttons.Controls.Add(finish);buttons.Controls.Add(start);buttons.Controls.Add(cancel);
        root.Controls.Add(title,0,0);root.Controls.Add(pathLabel,0,1);root.Controls.Add(infoPanel,0,2);root.Controls.Add(options,0,3);root.Controls.Add(progress,0,4);root.Controls.Add(percent,0,5);root.Controls.Add(statusPanel,0,6);root.Controls.Add(buttons,0,7);
        // Names let checks (and screen readers) find the controls.
        start.Name="StartButton";cancel.Name="CancelButton";finish.Name="FinishButton";saves.Name="SavesBox";deleteAll.Name="DeleteFolderBox";progress.Name="ProgressBar";percent.Name="PercentLabel";status.Name="StatusLabel";info.Name="InfoLabel";infoPanel.Name="InfoPanel";statusPanel.Name="StatusPanel";
        Controls.Add(root);
        root.SizeChanged+=delegate{Fit();};Load+=delegate{Fit();};Shown+=delegate{Fit();};
        AcceptButton=null;
        FormClosing+=delegate(object sender,FormClosingEventArgs a){if(busy){a.Cancel=true;RequestStop();}};
        FormClosed+=delegate{WasClosed=true;};
        start.Click+=async delegate{await StartUninstall();};
        cancel.Click+=delegate{if(busy)RequestStop();else Close();};
        finish.Click+=delegate{FinishNow();};
        deleteAll.CheckedChanged+=delegate{finish.Text=deleteAll.Checked?"Delete folder and finish":"Finish";finish.Width=S(deleteAll.Checked?250:150);PlaceButtons();};
        Fit();
    }
    void Style(Label l,string text,Font font,Color color){l.AutoSize=true;l.UseMnemonic=false;l.BackColor=Color.Transparent;l.ForeColor=color;l.Font=font;l.Text=text;}
    void StyleButton(Button b,string text,bool primary) {
        b.Text=text;b.AutoSize=false;b.Size=new Size(S(150),S(40));b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderSize=primary?0:1;b.FlatAppearance.BorderColor=Line;
        b.BackColor=primary?Amber:Surface;b.ForeColor=primary?Background:Ink;b.UseVisualStyleBackColor=false;b.Cursor=Cursors.Hand;b.Margin=new Padding(S(8),0,0,0);b.Padding=new Padding(S(8),0,S(8),0);
    }
    // A scrolling text area: the label grows with its text, the panel scrolls when the window is too small for it.
    static void Scroller(Panel panel,Label label) {
        panel.Dock=DockStyle.Fill;panel.AutoScroll=true;panel.BackColor=Color.Transparent;panel.Margin=new Padding(0);panel.Controls.Add(label);label.Location=new Point(0,0);
    }
    // Wrapping widths follow the window: every label wraps inside its row, so nothing is pushed out of view.
    void Fit() {
        int inner=Math.Max(S(120),root.ClientSize.Width-root.Padding.Horizontal);
        title.MaximumSize=new Size(inner,0);pathLabel.MaximumSize=new Size(inner,0);
        int text=Math.Max(S(100),inner-SystemInformation.VerticalScrollBarWidth-S(6));
        info.MaximumSize=new Size(text,0);status.MaximumSize=new Size(text,0);
        FitCheck(saves,inner);FitCheck(deleteAll,inner);savesNote.AutoSize=false;savesNote.Size=savesNote.GetPreferredSize(new Size(Math.Max(S(100),inner-S(22)),0));
        // The options row is sized by hand (nested auto-sizing panels can make a table layout loop).
        int h=0;foreach(Control c in options.Controls)if(c.Visible)h+=c.Height+c.Margin.Vertical;
        root.RowStyles[3].Height=Math.Max(S(8),h+options.Margin.Vertical);PlaceOptions();PlaceButtons();
    }
    // Plain panels placed by hand: FlowLayoutPanel hung while it was disposed.
    void PlaceOptions(){int y=0;foreach(Control c in options.Controls)if(c.Visible){c.Location=new Point(c.Margin.Left,y+c.Margin.Top);y+=c.Height+c.Margin.Vertical;}}
    void PlaceButtons(){int x=buttons.ClientSize.Width,top=Math.Max(0,buttons.ClientSize.Height-finish.Height);foreach(var b in new[]{finish,start,cancel})if(b.Visible){x-=b.Width;b.Location=new Point(Math.Max(0,x),top);x-=S(8);}}
    // A check box that wraps its text inside the row width (AutoSize check boxes do not wrap).
    void FitCheck(CheckBox c,int width) {
        int glyph=S(24);var size=TextRenderer.MeasureText(c.Text,c.Font,new Size(Math.Max(S(60),width-glyph),0),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
        c.Size=new Size(width,Math.Max(S(24),size.Height+S(6)));
    }
    // Shown as 00.00%; never moves backwards (step values and exact values interleave).
    public void ShowPercent(double value) {
        value=Math.Max(0,Math.Min(100,value));if(value<shown)value=shown;shown=value;
        progress.Value=(int)Math.Round(value*100);percent.Text=value.ToString("00.00",System.Globalization.CultureInfo.InvariantCulture)+"%";
    }
    void Post(Action a){if(IsHandleCreated&&!IsDisposed)BeginInvoke(a);}
    void RequestStop() {
        if(stop==null||stop.IsCancellationRequested)return;
        stop.Cancel();cancel.Enabled=false;cancel.Text="Stopping...";status.Text="Stopping after the current file. Everything done so far stays done, and Start continues from there.";
    }
    void SetBusy(bool on) {
        busy=on;start.Enabled=!on;saves.Enabled=!on&&saves.Enabled;
        if(on){cancel.Text="Cancel";cancel.Enabled=true;}
    }
    // The removal runs off the UI thread; every report is posted back to it. Nothing starts until this is called (the Start button).
    public async Task StartUninstall() {
        if(busy||finished)return;
        bool wantSaves=saves.Checked&&saves.Enabled;SetBusy(true);saves.Enabled=false;shown=0;progress.Value=0;percent.Text="00.00%";
        stop=new CancellationTokenSource();
        try {
            var e=new Engine();e.Cancel=stop.Token;
            e.Progress=(s,n)=>Post(()=>{status.Text=s;ShowPercent(n);});
            e.Fine=v=>Post(()=>ShowPercent(v));
            var options=new UninstallOptions{MoveSaves=wantSaves,SavesTarget=SavesTarget,InPlaceHelper=InPlace?typeof(Engine).Assembly.Location:null};
            var result=await Task.Run(()=>UninstallSupport.Remove(e,destination,options));
            last=result;status.Text=result.Summary;
            if(result.Cancelled){start.Text="Continue";cancel.Text="Close";cancel.Enabled=true;saves.Enabled=!wantSaves&&UninstallSupport.FindSaves(destination).Count>0;}
            else if(result.Failed>0){ShowPercent(100);start.Text="Try again";cancel.Text="Close";cancel.Enabled=true;}
            else {
                ShowPercent(100);finished=true;start.Visible=false;cancel.Visible=false;finish.Visible=true;PlaceButtons();
                if(!result.FolderRemoved&&result.LeftOver.Count>0){deleteAll.Visible=true;Fit();}
            }
        } catch(Exception ex) {
            status.Text=ex.Message+"\r\n\r\nUninstall can safely be repeated: fix the problem and press the button again.";start.Text="Try again";cancel.Text="Close";cancel.Enabled=true;saves.Enabled=!wantSaves&&UninstallSupport.FindSaves(destination).Count>0;
        } finally { SetBusy(false); }
    }
    // Check hook: a check that drives the window inside its own process cannot afford to wait for window disposal on a busy desktop (accessibility clients can stall it); the real window is closed and disposed normally.
    public static bool SkipDisposeInChecks;
    public bool WasClosed {get;private set;}
    protected override void Dispose(bool disposing){if(disposing&&SkipDisposeInChecks)return;base.Dispose(disposing);}
    void FinishNow() {
        if(deleteAll.Visible&&deleteAll.Checked&&last!=null) {
            Func<string,bool> ask=Confirm??(m=>MessageBox.Show(this,m,"Delete the mod folder",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes);
            if(!ask("Delete "+destination+" and everything in it, including "+last.LeftOver.Count+" of your files? This cannot be undone."))return;
            int failed;try{failed=UninstallSupport.DeleteModFolder(last);}catch(Exception ex){status.Text=ex.Message;return;}
            if(failed>0){status.Text="The mod folder was deleted except "+failed+" items that a program still uses. Close it and delete the folder by hand.";deleteAll.Checked=false;deleteAll.Visible=false;Fit();return;}
        }
        // A successful finish only: a rare cosmetic joke that can never fail or delay closing.
        if(finished)CatEncounter.Maybe(this,true);
        Close();
    }
}
}
