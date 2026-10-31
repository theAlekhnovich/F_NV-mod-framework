using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FalloutModSetup {
// part/was belong to the revision-1 update: a file with a part is read from that archive only (older parts may hold a superseded copy); was is the hash of the copy it replaces.
public class FileItem { public string path, hash, part, was; public string[] wasAlso; public long size; }
public class ArchiveItem { public string name, hash; public long size; }
public class MasterItem { public string name, hash; }
public class PatchItem { public string source, sourceHash, target, targetHash, patch; public long size; }
public class Package {
    public LanguageItem[] languages; public string[] optionalGameFiles;
    public string version, profile; public int activeMods, plugins; public long installedBytes;
    public FileItem[] files; public ArchiveItem[] archives; public MasterItem[] masters;
    public PatchItem[] patches; public string[] required;
    // Revision 1 (October 2026): files that older parts still contain but the package no longer installs, and the edits that turn the packaged profile lists into the current ones.
    public int revision; public FileItem[] retired; public ProfileEdit[] profileEdits;
    // Revision 2: where single mods sit in the mod list (moved on a fresh install and on an update of an older setup; see ModlistMoves).
    public ModMove[] modMoves;
    // Revision 5: single keys of the profile INIs (falloutprefs.ini and the like) that the packaged defaults changed; an update sets a key only while the setup still has the old packaged value.
    public IniEdit[] iniEdits;
}
public class Options {
    public string Game, Destination, Store, SteamConfig, ImportSaves;
    public string Language="Auto";
    public int Width=1920, Height=1080;
    public bool LocalSaves=true, Windowed=true, InstallRuntimes=true, DefenderExclusions, DesktopShortcut, StartMenuShortcut, SteamPlay;
}
public class Change { public string path, before, after; public bool existed, touched; }
public class InstallState { public string status, game, destination, version; public List<Change> changes=new List<Change>(); }

// What the extraction wrote: lets the uninstall receipt reuse the hash of a file that is still exactly as written.
public class ExtractRecord { public long size,ticks; public string hash; }
// Chooses how many files may be unpacked or hashed at once. An SSD/NVMe (no seek penalty) takes up to 4 workers; a hard disk, a network share or a drive
// whose type cannot be read takes 1, because several readers would only make the head seek.
public static class SpeedSupport {
    [StructLayout(LayoutKind.Sequential)] struct StoragePropertyQuery { public int PropertyId,QueryType;public byte Extra; }
    [StructLayout(LayoutKind.Sequential)] struct SeekPenalty { public int Version,Size;public byte IncursSeekPenalty; }
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device,uint code,ref StoragePropertyQuery input,int inputSize,out SeekPenalty output,int outputSize,out int returned,IntPtr overlapped);
    static readonly Dictionary<string,bool?> Known=new Dictionary<string,bool?>(StringComparer.OrdinalIgnoreCase);
    // true: no seek penalty (SSD/NVMe), false: rotational, null: unknown.
    public static bool? IsSolidState(string path) {
        try {
            string root=Path.GetPathRoot(Path.GetFullPath(path));
            if(String.IsNullOrEmpty(root)||root.StartsWith(@"\\"))return null;
            lock(Known){bool? cached;if(Known.TryGetValue(root,out cached))return cached;}
            bool? result=null;
            if(new DriveInfo(root).DriveType==DriveType.Fixed)
                using(var h=CreateFile(@"\\.\"+root.TrimEnd('\\'),0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                    if(!h.IsInvalid) {
                        var query=new StoragePropertyQuery{PropertyId=7,QueryType=0};SeekPenalty answer;int returned;
                        if(DeviceIoControl(h,0x2D1400,ref query,Marshal.SizeOf(typeof(StoragePropertyQuery)),out answer,Marshal.SizeOf(typeof(SeekPenalty)),out returned,IntPtr.Zero)&&returned>=9)result=answer.IncursSeekPenalty==0;
                    }
                }
            lock(Known)Known[root]=result;return result;
        } catch { return null; }
    }
    public static int Workers(string path) {
        return IsSolidState(path)==true?Math.Max(1,Math.Min(4,Environment.ProcessorCount)):1;
    }
    public static string Describe(string path) {
        bool? ssd=IsSolidState(path);int w=Workers(path);
        return (ssd==true?"SSD/NVMe (no seek penalty)":ssd==false?"hard disk (seek penalty)":"type unknown")+": "+w+(w==1?" worker":" parallel workers");
    }
}

public class Engine {
    public static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
    public readonly Package P;
    public readonly string PackageFolder;
    public Action<string,int> Progress=delegate {};
    // Exact overall percentage (0-100) during the long steps (verifying and extracting data parts), at most every 60 ms. Progress still carries the step text.
    public Action<double> Fine=delegate {};
    readonly Stopwatch fineClock=Stopwatch.StartNew();
    void Exact(double percent) { if(percent>=100||fineClock.ElapsedMilliseconds>=60){fineClock.Restart();Fine(Math.Max(0,Math.Min(100,percent)));} }
    public CancellationToken Cancel;
    public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=Int32.MaxValue }; }
    public Engine() {
        using(var r=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("package.json"),Utf8))
            P=Json().Deserialize<Package>(r.ReadToEnd());
        PackageFolder=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    }
    // Lets a setup be attached or checked against another package inventory (used by the portability checks).
    public Engine(Package package,string packageFolder) { P=package; PackageFolder=packageFolder; }
    public static string Hash(string file) { using(var h=SHA256.Create()) using(var s=File.OpenRead(file)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant(); }
    public static string Full(string p) {
        if(String.IsNullOrWhiteSpace(p) || !Regex.IsMatch(p,@"^[A-Za-z]:[\\/]")) throw new Exception("Choose a full local drive path, for example D:\\Games\\Fallout New Vegas.");
        p=Path.GetFullPath(p).TrimEnd('\\','/');
        if(p.Length<4) throw new Exception("Choose a folder, not the root of a drive.");
        NoLinks(p); return p;
    }
    public static bool Within(string p,string root) { return p.Equals(root,StringComparison.OrdinalIgnoreCase) || p.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase); }
    public static void NoLinks(string p) {
        for(var d=new DirectoryInfo(p);d!=null;d=d.Parent)
            if(d.Exists && (d.Attributes&FileAttributes.ReparsePoint)!=0) throw new Exception("Choose a real folder rather than a junction or symbolic link: "+d.FullName);
        if(File.Exists(p) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new Exception("Symbolic link is not supported: "+p);
    }
    public static string SafeJoin(string root,string relative) {
        if(String.IsNullOrEmpty(relative)||relative.Contains(":")||relative.StartsWith("/")||relative.StartsWith("\\")) throw new Exception("Invalid package path.");
        string p=Path.GetFullPath(Path.Combine(root,relative.Replace('/','\\')));
        if(!Within(p,root)||p.Equals(root,StringComparison.OrdinalIgnoreCase)) throw new Exception("Package path escapes its folder.");
        NoLinks(p);return p;
    }
    public static void EnsureParent(string p) { Directory.CreateDirectory(Path.GetDirectoryName(p)); }
    public static void NotRunning(string game,string dest) {
        foreach(string name in new[]{"FalloutNV","FalloutNVLauncher","ModOrganizer","nvse_loader"})
            foreach(var proc in Process.GetProcessesByName(name)) using(proc) {
                string path=null;
                try { path=proc.MainModule.FileName; } catch { throw new Exception("Close "+name+" before installing or restoring."); }
                if(Within(path,game)||Within(path,dest)) throw new Exception("Close "+name+" for the selected installation first.");
            }
    }
    void Say(string s,int n) { Progress(s,n); }
    void CheckCancel() { Cancel.ThrowIfCancellationRequested(); }
    public string Validate(Options o,bool verifyArchives) {
        if(!Environment.Is64BitOperatingSystem)throw new Exception("64-bit Windows is required.");
        o.Game=Full(o.Game);o.Destination=Full(o.Destination);
        if(o.Width<640||o.Height<480||o.Width>16384||o.Height>16384)throw new Exception("Enter a valid screen resolution.");
        if(Within(o.Game,o.Destination)||Within(o.Destination,o.Game)||Within(PackageFolder,o.Destination)||Within(o.Destination,PackageFolder))throw new Exception("The game, installer and new Mod Organizer folders must be separate.");
        if(File.Exists(o.Destination)||(Directory.Exists(o.Destination)&&Directory.EnumerateFileSystemEntries(o.Destination).Any()))throw new Exception("The new Mod Organizer folder must be empty. Choose a new folder to protect an existing setup.");
        CheckGame(o);
        foreach(var f in P.files.Where(x=>x.path.StartsWith("app/")))if(SafeJoin(o.Destination,f.path.Substring(4)).Length>=248)throw new Exception("The Mod Organizer path is too long for a file in this setup. Choose a shorter location, such as D:\\FNV-Mods.");
        long need=P.installedBytes+2L*1024*1024*1024;
        if(new DriveInfo(Path.GetPathRoot(o.Destination)).AvailableFreeSpace<need)throw new Exception("The Mod Organizer drive needs at least "+Math.Ceiling(need/1073741824.0)+" GiB free.");
        CheckGameSpace(o);
        Integrations.Preflight(o);
        if(verifyArchives)VerifyArchives();
        return "Ready: "+P.activeMods+" active mods, "+P.plugins+" plugins. New Mod Organizer: "+o.Destination+". Game: "+o.Game;
    }
    public static void CheckGameSpace(Options o) {
        if(new DriveInfo(Path.GetPathRoot(o.Game)).AvailableFreeSpace<150L*1024*1024)throw new Exception("The game drive needs at least 150 MB free for backup and patching.");
    }
    // Everything that must be true of the game folder before any file is changed. Shared by a fresh install and by attaching a copied setup to a new game folder.
    public void CheckGame(Options o) {
        o.Game=Full(o.Game);
        if(o.Store=="Auto")o.Store=GameLocator.StoreFromFolder(o.Game)??"Steam";
        if(!new[]{"Steam","GOG","Epic"}.Contains(o.Store))throw new Exception("Select Steam, GOG or Epic.");
        string exe=SafeJoin(o.Game,"FalloutNV.exe");
        if(!File.Exists(exe)||!File.Exists(SafeJoin(o.Game,"FalloutNVLauncher.exe")))throw new Exception("Select the game folder containing FalloutNV.exe and FalloutNVLauncher.exe.");
        NotRunning(o.Game,o.Destination);
        var v=FileVersionInfo.GetVersionInfo(exe);
        if(v.FileMajorPart!=1||v.FileMinorPart!=4||v.FileBuildPart!=0||v.FilePrivatePart!=525)throw new Exception("This executable is not New Vegas 1.4.0.525. Epic users must run the official Epic Games Patcher first. See Courier\u2019s Field Guide.html.");
        foreach(var r in P.required)if(!File.Exists(SafeJoin(o.Game,r)))throw new Exception("Missing game/DLC archive: "+r+". Install the game and all DLC through your store.");
        LanguageSupport.Resolve(P,o);
        int i=0;
        foreach(var m in P.masters) {
            CheckCancel();Say("Checking game master: "+Path.GetFileName(m.name),2+i++);
            string p=SafeJoin(o.Game,m.name);
            if(!File.Exists(p))throw new Exception("Missing game/DLC master: "+m.name+". Install the game and all DLC through your store.");
            string expected=LanguageSupport.MasterHash(P,o,m);
            if(expected==null)continue;
            string actual=Hash(p);
            if(actual!=expected)throw new Exception((o.Language=="English"?"If your game is not in English, go back and choose Other languages.\r\n":"")+"Unsupported "+o.Language+" game master: "+m.name+". This version needs game files that match the English release (other languages must keep their translations in the *_lang.esp files) or the supported Russian release. A language/release mismatch does not mean the game is damaged.\r\nExpected: "+expected+"\r\nFound: "+actual+"\r\nNo game files have been changed.");
        }
        LanguageSupport.Preflight(P,o);
        if(o.Store=="Epic"&&!HasLoaderPatch(exe))throw new Exception("Epic preparation is incomplete. Run the official Epic Games Patcher, then select its English game folder. See Courier\u2019s Field Guide.html.");
        foreach(var f in P.files.Where(x=>x.path.StartsWith("game/")))SafeJoin(o.Game,f.path.Substring(5));
    }
    // ---- Speed: workers, timing, parallel verification and extraction ----
    // 0 = decide by drive type (SSD/NVMe: up to 4, otherwise 1). A check or the FNVMF_WORKERS variable forces a count.
    public int WorkersOverride;
    public double VerifySeconds,ExtractSeconds;public int VerifyWorkers,ExtractWorkers;public long ExtractedFiles,ExtractedBytes;
    // Hash, size and write time of every file the extraction wrote (relative to the mod folder, '/'), so the uninstall receipt does not have to read them all again.
    public readonly Dictionary<string,ExtractRecord> Extracted=new Dictionary<string,ExtractRecord>(StringComparer.OrdinalIgnoreCase);
    public DateTime ExtractedAtUtc=DateTime.MinValue;
    readonly object progressLock=new object();
    volatile bool poolAbort;
    public static string Secs(double s){return s.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" s";}
    public int WorkersFor(string path) {
        if(WorkersOverride>0)return WorkersOverride;
        int forced;if(Int32.TryParse(Environment.GetEnvironmentVariable("FNVMF_WORKERS"),out forced)&&forced>0)return Math.Min(forced,16);
        return SpeedSupport.Workers(path);
    }
    // Runs item(i,worker) for every i on up to `workers` threads (the calling thread when workers is 1; items are taken in ascending order).
    // The first failure stops the others, then it is rethrown here. Not re-entrant.
    void ParallelFor(int count,int workers,Action<int,int> item,Action<int> workerDone=null) {
        workers=Math.Max(1,Math.Min(workers,count));poolAbort=false;
        int next=-1;Exception first=null;object gate=new object();
        Action<int> run=delegate(int w) {
            try {
                for(;;){if(poolAbort)break;int i=Interlocked.Increment(ref next);if(i>=count)break;item(i,w);}
            } catch(Exception ex) {
                lock(gate){if(first==null)first=ex;}
                poolAbort=true;
            } finally {
                if(workerDone!=null)try{workerDone(w);}catch{}
            }
        };
        if(workers==1)run(0);
        else {
            var threads=new List<Thread>();
            for(int w=0;w<workers;w++){int id=w;var t=new Thread(()=>run(id)){IsBackground=true,Name="setup-worker-"+w};threads.Add(t);t.Start();}
            foreach(var t in threads)t.Join();
        }
        if(first!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }
    void CheckAbort() { CheckCancel();if(poolAbort)throw new OperationCanceledException(); }
    public void VerifyArchives() {
        var clock=Stopwatch.StartNew();int n=P.archives.Length;
        int workers=Math.Max(1,Math.Min(WorkersFor(PackageFolder),n));VerifyWorkers=workers;
        long total=Math.Max(1,P.archives.Sum(a=>a.size));var doneBytes=new long[n];int finished=0;
        Say("Verifying "+n+" data parts"+(workers>1?" ("+workers+" at a time)":""),12);
        ParallelFor(n,workers,delegate(int i,int w) {
            CheckCancel();var a=P.archives[i];
            string p=SafeJoin(PackageFolder,a.name);
            if(!File.Exists(p)||new FileInfo(p).Length!=a.size||HashWithProgress(p,bytes=>{CheckAbort();Interlocked.Exchange(ref doneBytes[i],bytes);lock(progressLock){long sum=0;for(int k=0;k<n;k++)sum+=Interlocked.Read(ref doneBytes[k]);Exact(12+sum/(double)total*12.0);}})!=a.hash)throw new Exception("Missing or damaged data part: "+a.name+". Keep every .bin file beside the EXE.");
            int count=Interlocked.Increment(ref finished);
            lock(progressLock)Say("Verified data part "+count+" of "+n,12+count*12/n);
        });
        VerifySeconds=clock.Elapsed.TotalSeconds;
        Say("Data parts verified in "+Secs(VerifySeconds)+(workers>1?" ("+workers+" at a time)":""),24);
    }
    // Same SHA-256 as Hash, reporting how many bytes were read so far.
    public static string HashWithProgress(string file,Action<long> read) {
        using(var h=SHA256.Create()) using(var s=File.OpenRead(file)) {
            byte[] b=new byte[4*1024*1024];int n;long total=0;
            while((n=s.Read(b,0,b.Length))>0){h.TransformBlock(b,0,n,null,0);total+=n;read(total);}
            h.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(h.Hash).Replace("-","").ToLowerInvariant();
        }
    }
    public static bool HasLoaderPatch(string exe) {
        byte[] b=File.ReadAllBytes(exe);
        if(b.Length<64||b[0]!=77||b[1]!=90)return false;
        int pe=BitConverter.ToInt32(b,60);
        if(pe<0||pe>b.Length-26||BitConverter.ToUInt32(b,pe)!=0x4550)return false;
        return (BitConverter.ToUInt16(b,pe+22)&32)!=0 && Encoding.ASCII.GetString(b).Contains("nvse_steam_loader.dll");
    }
    public static string Run(string exe,string args,string cwd,int seconds,bool patcher) {
        var start=new ProcessStartInfo(exe,args){WorkingDirectory=cwd,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true};
        using(var p=Process.Start(start)) {
            var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
            p.StandardInput.WriteLine();p.StandardInput.Close();
            if(!p.WaitForExit(seconds*1000)) {p.Kill();p.WaitForExit();throw new Exception("Patching timed out; game files were not committed.");}
            string result=stdout.Result+stderr.Result;
            if(patcher) {
                if(result.IndexOf("patched!",StringComparison.OrdinalIgnoreCase)<0 || (p.ExitCode!=0&&p.ExitCode!=1))throw new Exception("4GB patcher rejected this executable: "+result.Trim());
            } else if(p.ExitCode!=0)throw new Exception("Patch failed: "+result.Trim());
            return result;
        }
    }
    public void Install(Options o) {
        var total=Stopwatch.StartNew();
        Validate(o,true);
        bool created=false;InstallState state=null;string support=null;
        var logs=new List<string>();Action<string> log=delegate(string s){lock(logs)logs.Add(DateTime.Now.ToString("s")+" "+s);};
        // Where the time goes (install.log): the drive decides how many files are handled at once.
        log("Drives: new mod folder on "+SpeedSupport.Describe(o.Destination)+"; data parts on "+SpeedSupport.Describe(PackageFolder)+".");
        if(VerifyWorkers>0)log("Timing: verifying the data parts took "+Secs(VerifySeconds)+" ("+VerifyWorkers+" at a time).");
        // Everything that needs administrator rights happens first, in ONE elevated step with ONE Windows prompt: the required software and the optional Defender exclusions.
        // The installation itself stays unelevated (right file owner, shortcuts in this profile).
        var missing=Prerequisites.Plan(PackageFolder,o.InstallRuntimes);
        foreach(var line in Prerequisites.Report)log(line);
        bool madeEarly=false;
        if(o.DefenderExclusions&&!Directory.Exists(o.Destination)) {
            // The Defender step needs the ownership marker in the new mod folder; it is removed again if the privileged step stops the installation.
            Directory.CreateDirectory(o.Destination);madeEarly=true;
        }
        BatchSummary batch;
        try {
            if(o.DefenderExclusions){string early=SafeJoin(o.Destination,"_setup");Directory.CreateDirectory(early);File.WriteAllText(Path.Combine(early,"owned-marker"),P.version,Utf8);}
            batch=PrivilegedBatch.Perform(PackageFolder,o.Destination,o.Game,missing,o.DefenderExclusions,s=>Say(s,24),log);
        } catch {
            try{if(o.DefenderExclusions){File.Delete(SafeJoin(o.Destination,"_setup/owned-marker"));Directory.Delete(SafeJoin(o.Destination,"_setup"),false);if(madeEarly)Directory.Delete(o.Destination,false);}}catch{}
            throw;
        }
        Say(batch.DefenderLine,25);log(batch.DefenderLine);
        try {
            Directory.CreateDirectory(o.Destination);created=true;
            support=SafeJoin(o.Destination,"_setup");Directory.CreateDirectory(support);
            File.WriteAllText(Path.Combine(support,"owned-marker"),P.version,Utf8);
            bool defenderSkipped=batch.DefenderSkipped;
            CheckCancel();
            // The new empty destination is the staging area. Game files are committed last.
            ExtractFiles(o.Destination,support);
            log("All packaged files passed SHA-256 verification.");
            log("Timing: extraction took "+Secs(ExtractSeconds)+" ("+ExtractedFiles+" files, "+(ExtractedBytes/1048576)+" MB, "+ExtractWorkers+" at a time, "+(ExtractedBytes/1048576.0/Math.Max(0.001,ExtractSeconds)).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" MB/s).");
            Say("Files installed in "+Secs(ExtractSeconds)+(ExtractWorkers>1?" ("+ExtractWorkers+" at a time)":""),80);
            UpdateSupport.ApplyProfileEdits(this,o.Destination,true);
            UpdateSupport.ApplyIniEdits(this,o.Destination,0,true,log);
            File.WriteAllText(SafeJoin(support,UpdateSupport.RevisionName),P.revision.ToString(),Utf8);
            string scratch=SafeJoin(support,"patchwork");Directory.CreateDirectory(scratch);
            // The master rebuilds and the executable patch do not depend on each other, so they run side by side when the drive allows (each rebuild in its own scratch folder).
            var rebuild=Stopwatch.StartNew();string originalExeHash=null;int rebuildWorkers=Math.Max(1,Math.Min(WorkersFor(o.Destination),P.patches.Length+1));
            ParallelFor(P.patches.Length+1,rebuildWorkers,delegate(int i,int w) {
                CheckAbort();
                if(i<P.patches.Length) {
                    var p=P.patches[i];string own=Path.Combine(scratch,"esm"+i);Directory.CreateDirectory(own);
                    lock(progressLock)Say("Rebuilding Fixed ESMs: "+Path.GetFileName(p.source),82);
                    RebuildMaster(o,p,support,own);
                    log("Rebuilt and verified "+p.source);
                } else {
                    lock(progressLock)Say("Preparing game executable and xNVSE",88);
                    originalExeHash=PrepareExecutable(o,support,scratch,log);
                }
            });
            log("Timing: rebuilding the Fixed ESMs and preparing the executable took "+Secs(rebuild.Elapsed.TotalSeconds)+" ("+rebuildWorkers+" at a time).");
            LanguageSupport.Apply(this,o,log);
            PreparePluginOrder(o);
            Configure(o);
            CopySaves(o,log);
            state=new InstallState{game=o.Game,destination=o.Destination,version=P.version,status="installing"};
            CommitGame(o,state,support,scratch,originalExeHash,log);
            File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(support,"Setup and Restore.exe"),true);
            Integrations.Apply(o,log);
            state.status="installed";SaveState(state);
            UninstallSupport.WriteRegistry(o.Destination,log);
            foreach(var n in new[]{"INSTALLATION.html","MOD-LIST.json"})if(File.Exists(Path.Combine(PackageFolder,n)))File.Copy(Path.Combine(PackageFolder,n),Path.Combine(o.Destination,n));
            log("Installation complete. Store: "+o.Store+". Game: "+o.Game);
            File.WriteAllLines(Path.Combine(support,"install.log"),logs,Utf8);
            var capture=Stopwatch.StartNew();
            UninstallSupport.Capture(this,o.Destination);
            log("Timing: recording the uninstall receipt took "+Secs(capture.Elapsed.TotalSeconds)+" ("+UninstallSupport.LastCapture+").");
            log("Timing: the whole installation took "+Secs(total.Elapsed.TotalSeconds)+".");
            // The log changed after the receipt was written: record its final bytes.
            string finalLog=Path.Combine(support,"install.log");File.WriteAllLines(finalLog,logs,Utf8);PortableSupport.UpdateReceipt(o.Destination,new[]{finalLog},null);
            Say("Installed successfully. Use \""+Integrations.PlayName+"\" in your new mod folder or your selected shortcuts."+(defenderSkipped?"\r\n\r\n"+batch.DefenderLine+" This is fine unless Defender or another antivirus blocks the game or xNVSE; the guide (section \"If Defender exclusions fail\") explains what to do then.":""),100);
        } catch(Exception ex) {
            log("FAILED: "+ex.Message);
            if(created)try{DefenderSupport.Request(o.Destination,o.Game,false);}catch(Exception re){log("DEFENDER RESTORE REQUIRED: "+re.Message);}
            if(created)try{Integrations.Restore(o.Destination,true);log("Optional integrations rolled back.");}catch(Exception re){log("INTEGRATION RESTORE REQUIRED: "+re.Message);}
            if(state!=null&&state.changes.Count>0&&File.Exists(Path.Combine(support,"state.json"))) {
                try {RestoreState(state,true);log("Game files restored from verified backups.");}
                catch(Exception re) {log("RESTORE REQUIRED: "+re.Message);}
            }
            if(created) {
                File.WriteAllLines(Path.Combine(support,"install.log"),logs,Utf8);
                if(!File.Exists(Path.Combine(support,"Setup and Restore.exe")))File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(support,"Setup and Restore.exe"));
            }
            throw new Exception(ex.Message+(created?"\r\nInstallation stopped. See "+Path.Combine(support,"install.log")+". The partial Mod Organizer folder is retained for inspection; use a new empty folder to retry.":""),ex);
        }
    }
    // Verified extraction of every data part into the new mod folder (app/ files) and its _setup folder (everything else). A file with a part is taken from that part only;
    // retired files and the copies an update part supersedes are skipped. The rules are applied first, in archive order; then the files are unpacked by up to
    // WorkersFor(destination) workers (1 on a hard disk), each file written with CreateNew and checked against the inventory hash exactly as before.
    class ExtractItem { public string Name,Dst,Key;public FileItem File; }
    class ExtractUnit { public string Part;public List<ExtractItem> Items=new List<ExtractItem>(); }
    public void ExtractFiles(string destination,string support) {
        var clock=Stopwatch.StartNew();
        var expected=P.files.ToDictionary(x=>x.path,StringComparer.OrdinalIgnoreCase);var extracted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retired=new HashSet<string>((P.retired??new FileItem[0]).Select(x=>x.path),StringComparer.OrdinalIgnoreCase);
        string full=Full(destination);var units=new List<ExtractUnit>();
        foreach(var a in P.archives) using(var zip=ZipFile.OpenRead(SafeJoin(PackageFolder,a.name))) {
            ExtractUnit unit=null;long unitBytes=0;
            foreach(var e in zip.Entries) {
                CheckCancel();FileItem f;
                if(retired.Contains(e.FullName))continue;
                if(!expected.TryGetValue(e.FullName,out f))throw new Exception("Data part contents do not match the embedded package inventory.");
                if(f.part!=null&&!String.Equals(f.part,a.name,StringComparison.OrdinalIgnoreCase))continue;
                if(!extracted.Add(e.FullName)||e.Length!=f.size)throw new Exception("Data part contents do not match the embedded package inventory.");
                string root=e.FullName.StartsWith("app/")?destination:support;
                string rel=e.FullName.StartsWith("app/")?e.FullName.Substring(4):e.FullName;
                string dst=SafeJoin(root,rel);
                // Chunks of about 48 MB or 256 files from one part: a worker opens a part once and reads it front to back.
                if(unit==null||unitBytes>=48L*1024*1024||unit.Items.Count>=256){unit=new ExtractUnit{Part=a.name};units.Add(unit);unitBytes=0;}
                unit.Items.Add(new ExtractItem{Name=e.FullName,File=f,Dst=dst,Key=Within(dst,full)?dst.Substring(full.TrimEnd('\\').Length+1).Replace('\\','/'):null});unitBytes+=f.size;
            }
        }
        if(extracted.Count!=expected.Count)throw new Exception("The data parts are incomplete.");
        int workers=Math.Max(1,Math.Min(WorkersFor(destination),units.Count));ExtractWorkers=workers;
        long done=0;int filesDone=0;var zips=new ZipArchive[workers];var zipPart=new string[workers];var buffers=new byte[workers][];
        lock(Extracted)Extracted.Clear();
        ParallelFor(units.Count,workers,delegate(int index,int w) {
            var unit=units[index];
            if(zipPart[w]!=unit.Part){if(zips[w]!=null)zips[w].Dispose();zips[w]=null;zipPart[w]=null;zips[w]=ZipFile.OpenRead(SafeJoin(PackageFolder,unit.Part));zipPart[w]=unit.Part;}
            if(buffers[w]==null)buffers[w]=new byte[1024*1024];byte[] b=buffers[w];
            foreach(var item in unit.Items) {
                CheckAbort();var f=item.File;
                var e=zips[w].GetEntry(item.Name);
                if(e==null||e.Length!=f.size)throw new Exception("Data part contents do not match the embedded package inventory.");
                EnsureParent(item.Dst);
                using(var src=e.Open())using(var output=new FileStream(item.Dst,FileMode.CreateNew,FileAccess.Write))using(var h=SHA256.Create()) {
                    int n;
                    while((n=src.Read(b,0,b.Length))>0) {
                        CheckAbort();output.Write(b,0,n);h.TransformBlock(b,0,n,null,0);Interlocked.Add(ref done,n);
                        lock(progressLock)Exact(25+Math.Min(55.0,Interlocked.Read(ref done)*55.0/Math.Max(1,P.installedBytes)));
                    }
                    h.TransformFinalBlock(new byte[0],0,0);
                    if(BitConverter.ToString(h.Hash).Replace("-","").ToLowerInvariant()!=f.hash)throw new Exception("Damaged extracted file: "+e.FullName);
                }
                if(item.Key!=null){var info=new FileInfo(item.Dst);lock(Extracted)Extracted[item.Key]=new ExtractRecord{size=info.Length,ticks=info.LastWriteTimeUtc.Ticks,hash=f.hash};}
                int count=Interlocked.Increment(ref filesDone);
                if(count%150==0||f.size>100000000)lock(progressLock)Say("Installing files: "+count+" / "+P.files.Length,25+(int)(Interlocked.Read(ref done)*55/Math.Max(1,P.installedBytes)));
            }
        },delegate(int w){if(zips[w]!=null){zips[w].Dispose();zips[w]=null;}});
        ExtractSeconds=clock.Elapsed.TotalSeconds;ExtractedFiles=filesDone;ExtractedBytes=done;ExtractedAtUtc=DateTime.UtcNow;
    }
    // Rebuilds one Fixed ESM in the mod folder from the game's own master and the packaged delta. A damaged target is replaced.
    public void RebuildMaster(Options o,PatchItem p,string support,string scratch) {
        File.Copy(SafeJoin(o.Game,p.source),Path.Combine(scratch,"source.esm"),true);
        if(Hash(Path.Combine(scratch,"source.esm"))!=p.sourceHash)throw new Exception("A source game master changed during installation.");
        File.Copy(SafeJoin(support,p.patch),Path.Combine(scratch,"delta.vcdiff"),true);
        Run(SafeJoin(support,"tools/xdelta3decode.exe"),"-f -d -s source.esm delta.vcdiff result.esm",scratch,600,false);
        string result=Path.Combine(scratch,"result.esm");
        if(Hash(result)!=p.targetHash)throw new Exception("Fixed ESM verification failed.");
        string target=SafeJoin(o.Destination,p.target.Substring(4));EnsureParent(target);
        if(File.Exists(target))File.Delete(target);
        File.Move(result,target);
    }
    // Work on a copy so a rejected or interrupted patch cannot damage the actual game. Returns the hash of the untouched executable.
    public string PrepareExecutable(Options o,string support,string scratch,Action<string> log) {
        File.Copy(SafeJoin(o.Game,"FalloutNV.exe"),Path.Combine(scratch,"FalloutNV.exe"),true);
        string originalExeHash=Hash(Path.Combine(scratch,"FalloutNV.exe"));
        if(o.Store!="Epic")log(Run(SafeJoin(support,"tools/FNVpatch.exe"),"",scratch,60,true));
        if(!HasLoaderPatch(Path.Combine(scratch,"FalloutNV.exe")))throw new Exception("The executable is missing the 4GB flag or xNVSE loading patch.");
        return originalExeHash;
    }
    // The game-folder commit: verified backups and the state record first, then each file, verified after copying. The caller owns rollback.
    public void CommitGame(Options o,InstallState state,string support,string scratch,string originalExeHash,Action<string> log) {
        CheckCancel();NotRunning(o.Game,o.Destination);
        var pending=P.files.Where(x=>x.path.StartsWith("game/")).Select(x=>x.path.Substring(5)).Where(x=>!(P.optionalGameFiles??new string[0]).Contains(x)||!File.Exists(SafeJoin(o.Game,x))).ToList();pending.Add("FalloutNV.exe");
        string backup=SafeJoin(support,"backup");Directory.CreateDirectory(backup);
        foreach(var rel in pending) {
            string target=SafeJoin(o.Game,rel);
            string source=rel=="FalloutNV.exe"?Path.Combine(scratch,"FalloutNV.exe"):SafeJoin(support,"game/"+rel);
            var c=new Change{path=rel,existed=File.Exists(target),after=Hash(source)};
            if(c.existed) { c.before=Hash(target);string b=SafeJoin(backup,rel);EnsureParent(b);File.Copy(target,b);if(Hash(b)!=c.before)throw new Exception("Backup verification failed: "+rel); }
            state.changes.Add(c);
        }
        if(state.changes.Last().before!=originalExeHash)throw new Exception("The game executable changed during installation; retry with the game and store closed.");
        SaveState(state);log("Verified backups saved before game-folder changes.");
        foreach(var c in state.changes) {
            string target=SafeJoin(o.Game,c.path);
            if(c.existed ? Hash(target)!=c.before : File.Exists(target))throw new Exception("Game file changed during installation: "+c.path);
            string source=c.path=="FalloutNV.exe"?Path.Combine(scratch,"FalloutNV.exe"):SafeJoin(support,"game/"+c.path);
            c.touched=true;SaveState(state);
            EnsureParent(target);File.Copy(source,target,true);
            if(Hash(target)!=c.after)throw new Exception("Game-file verification failed: "+c.path);
        }
    }
    public static void SaveState(InstallState s) {
        string dir=SafeJoin(s.destination,"_setup"), path=Path.Combine(dir,"state.json"), temp=Path.Combine(dir,"state.tmp");
        File.WriteAllText(temp,Json().Serialize(s),Utf8);
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
    public void Restore(string destination) {
        destination=Full(destination);
        string p=SafeJoin(destination,"_setup/state.json");
        if(!File.Exists(p))throw new Exception("This folder has no installer backup record.");
        var s=Json().Deserialize<InstallState>(File.ReadAllText(p,Utf8));
        if(!String.Equals(Full(s.destination),destination,StringComparison.OrdinalIgnoreCase))throw new Exception("The Mod Organizer folder moved. Run \""+PortableSupport.RepairCommand+"\" in that folder first, or restore it to the recorded location.");
        s.game=Full(s.game);Say("Checking that the game and Mod Organizer are closed",3);NotRunning(s.game,destination);RestoreState(s,false);
        Say("Original game files restored. Mod Organizer and its saves have been kept.",100);
    }
    // Uninstall sets KeepChangedGameFiles: a game file someone edited after installation stays as it is and is reported, instead of stopping the whole uninstall.
    // The three lists say plainly what happened to each game file (put back from the backup, already original, deleted because the installer had added it, kept because it was edited).
    public bool KeepChangedGameFiles;
    // Set when the Defender exclusions could not be taken back (declined prompt, Defender off): said plainly in the uninstall result.
    public string DefenderNote;
    // Set when the game folder was missing and nothing could be put back there.
    public string GameNote;
    // Where shortcuts live (null: the real desktop and Start menu); checks point it at a fixture.
    public IntegrationLocations Places;
    // True when this run found the game files already restored by an earlier (stopped or repeated) uninstall.
    public bool GameAlreadyRestored;
    // Shortcuts and Steam launch options. An uninstall (KeepChangedGameFiles) never stops for them: whatever cannot be undone is listed in IntegrationNotes and shown in the result.
    void IntegrationsRestore(InstallState s,bool failure) {
        if(KeepChangedGameFiles){try{Integrations.Restore(s.destination,false,Places,n=>IntegrationNotes.Add(n));}catch(Exception ex){IntegrationNotes.Add("Shortcuts and Steam launch options could not be restored ("+ex.Message+").");}}
        else if(failure){try{Integrations.Restore(s.destination,true,Places);}catch{/* The caller logs optional-integration recovery separately. Still restore the game. */}}
        else Integrations.Restore(s.destination,false,Places);
    }
    void DefenderRemoval(InstallState s,int percent) {
        // Taking the exclusions back must not block restoring the game files or removing the mod files (Defender may be off or managed elsewhere by now).
        try{DefenderSupport.Request(s.destination,s.game,false);DefenderNote=null;}
        catch(Exception dx){
            string why=DefenderSupport.CleanError(dx.Message);
            DefenderNote="Defender exclusions added by setup could not be removed ("+why+"). Remove them in Windows Security > Virus & threat protection > Manage settings > Exclusions, or run Uninstall again and accept the Windows prompt.";
            Say("Defender exclusions could not be removed: "+why,percent);
        }
    }
    public readonly List<string> GameRestored=new List<string>(),GameRemoved=new List<string>(),GameKept=new List<string>(),IntegrationNotes=new List<string>();
    void RestoreState(InstallState s,bool failure) {
        if(s.status=="restored"){GameRestored.Clear();GameRemoved.Clear();GameKept.Clear();GameNote=null;GameAlreadyRestored=true;IntegrationNotes.Clear();DefenderRemoval(s,13);IntegrationsRestore(s,false);return;}
        GameAlreadyRestored=false;
        var allowed=new HashSet<string>(P.files.Where(x=>x.path.StartsWith("game/")).Select(x=>x.path.Substring(5)),StringComparer.OrdinalIgnoreCase);allowed.Add("FalloutNV.exe");
        if(s.changes.Any(c=>!allowed.Contains(c.path))||s.changes.Select(c=>c.path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.changes.Count)throw new Exception("The backup record contains unexpected files.");
        // Validate every backup and current file before restoring any of them.
        GameRestored.Clear();GameRemoved.Clear();GameKept.Clear();IntegrationNotes.Clear();GameNote=null;var edited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A game folder that no longer exists (game uninstalled in Steam) must not be recreated with a handful of files.
        bool gameThere=Directory.Exists(s.game);
        if(!gameThere) {
            if(!KeepChangedGameFiles)throw new Exception("The game folder was not found: "+s.game+". Put it back, or attach this setup to the current game folder with \""+PortableSupport.RepairCommand+"\".");
            GameNote="The game folder "+s.game+" was not found, so no game file could be restored.";
        }
        Say("Verifying the backed-up original game files",5);
        foreach(var c in s.changes) {
            string current=SafeJoin(s.game,c.path), backup=SafeJoin(s.destination,"_setup/backup/"+c.path);
            if(c.existed&&(!File.Exists(backup)||Hash(backup)!=c.before))throw new Exception("A backup is missing or changed: "+c.path);
            if(!failure && File.Exists(current)) {
                string hash=Hash(current);
                if(hash!=c.after&&hash!=c.before) {
                    if(!KeepChangedGameFiles)throw new Exception("Restore stopped because this file was changed after installation: "+c.path+". Preserve that change before restoring the backup manually.");
                    edited.Add(c.path);GameKept.Add(c.path);
                }
            }
        }
        // Taking the exclusions back must not block restoring the game files (Defender may be off or managed elsewhere by now).
        Say("Removing the Microsoft Defender exclusions added by setup (Windows may ask for approval)",12);
        DefenderRemoval(s,13);
        Say("Removing the shortcuts and Steam launch option added by setup",20);
        IntegrationsRestore(s,failure);
        int total=Math.Max(1,s.changes.Count(c=>c.touched)),done=0;
        if(gameThere)RevertChanges(s,edited,(kind,path)=>{
            done++;Say((kind=="removed"?"Removing the file setup added to the game folder: ":kind=="original"?"Already the original file: ":"Restoring the original game file: ")+path,30+60*done/total);
            if(kind=="restored"||kind=="original")GameRestored.Add(path);else GameRemoved.Add(path);
        });
        s.status="restored";SaveState(s);
    }
    // Puts every touched game file back from the verified backups (or deletes files the installer added). skip: files left alone because they were edited after installation.
    // report(kind,path): kind is "restored" (copied back and verified), "original" (was already the original) or "removed" (added by the installer).
    public static void RevertChanges(InstallState s,ICollection<string> skip=null,Action<string,string> report=null) {
        foreach(var c in s.changes.AsEnumerable().Reverse()) {
            if(!c.touched)continue;
            if(skip!=null&&skip.Contains(c.path))continue;
            string current=SafeJoin(s.game,c.path), backup=SafeJoin(s.destination,"_setup/backup/"+c.path);
            if(c.existed&&File.Exists(current)&&Hash(current)==c.before){if(report!=null)report("original",c.path);continue;}
            if(c.existed) {EnsureParent(current);File.Copy(backup,current,true);if(Hash(current)!=c.before)throw new Exception("Restore verification failed: "+c.path);if(report!=null)report("restored",c.path);}
            else if(File.Exists(current)){File.Delete(current);if(report!=null)report("removed",c.path);}
        }
    }
    public static string Ini(string text,string section,string key,string value) {
        var lines=text.Replace("\r","").Split('\n').ToList();int start=lines.FindIndex(l=>l.Trim().Equals("["+section+"]",StringComparison.OrdinalIgnoreCase));
        if(start<0){lines.Add("["+section+"]");lines.Add(key+"="+value);return String.Join("\r\n",lines);}
        int end=lines.FindIndex(start+1,l=>l.TrimStart().StartsWith("["));if(end<0)end=lines.Count;
        var matches=new List<int>();
        for(int i=start+1;i<end;i++){int equals=lines[i].IndexOf('=');if(equals>=0&&lines[i].Substring(0,equals).Trim().Equals(key,StringComparison.OrdinalIgnoreCase))matches.Add(i);}
        if(matches.Count==0)lines.Insert(end,key+"="+value);
        else {lines[matches[0]]=key+"="+value;foreach(int i in matches.Skip(1).Reverse())lines.RemoveAt(i);}
        return String.Join("\r\n",lines);
    }
    public static string Q(string s) { return "\""+s.Replace("\\","/").Replace("\"","")+"\""; }
    // gamePath is a QByteArray in MO2; ASCII hex escapes preserve UTF-8 folder names.
    public static string GamePathValue(string game) { return "@ByteArray("+String.Concat(Utf8.GetBytes(game.Replace('\\','/')).Select(b=>"\\x"+b.ToString("x2")))+")"; }
    void PreparePluginOrder(Options o) {
        string profile=SafeJoin(o.Destination,"profiles/Default"),list=Path.Combine(profile,"modlist.txt");
        var lines=File.ReadAllLines(list,Utf8).ToList();
        const string wmxCompanion="WMX - Hide Notes and Uninstall Menu";
        if(File.Exists(SafeJoin(o.Destination,"mods/"+wmxCompanion+"/Config/WMXPersonal.ini"))) {
            lines.RemoveAll(x=>x=="+"+wmxCompanion||x=="-"+wmxCompanion);
            int at=lines.FindIndex(x=>!x.StartsWith("#"));
            lines.Insert(at<0?lines.Count:at,"+"+wmxCompanion);
        }
        foreach(var name in new[]{"Local game files","Framework support"}){lines.RemoveAll(x=>x=="+"+name);int at=lines.FindIndex(x=>!x.StartsWith("#"));lines.Insert(at<0?lines.Count:at,"+"+name);}
        // Revision 2: the entries above start at the highest priority; the package says where they (and Performance Tweaks) belong.
        ModlistMoves.Apply(lines,P.modMoves,0);
        string local=SafeJoin(o.Destination,"mods/Local game files");Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local,"meta.ini"),"[General]\r\nnotes=Copies of your installed game plugins, with timestamps matching this profile. Original game files are unchanged.\r\n",Utf8);
        StampPluginOrder(o.Destination,o.Game,lines,true);
        File.WriteAllLines(list,lines,Utf8);
    }
    // Gives every installer-owned plugin file the timestamp of its place in loadorder.txt. A strict run (installation) refuses an inconsistent order;
    // a lenient run (update of a setup the user may have changed) skips what it cannot place.
    public void StampPluginOrder(string destination,string game,IList<string> modlist,bool strict) {
        string profile=SafeJoin(destination,"profiles/Default"),local=SafeJoin(destination,"mods/Local game files");
        var mods=modlist.Where(x=>x.StartsWith("+")).Select(x=>SafeJoin(destination,"mods/"+x.Substring(1))).ToList();
        var active=new HashSet<string>(File.ReadAllLines(Path.Combine(profile,"plugins.txt"),Utf8).Where(x=>x.Length>0&&!x.StartsWith("#")),StringComparer.OrdinalIgnoreCase);
        var order=File.ReadAllLines(Path.Combine(profile,"loadorder.txt"),Utf8).Where(x=>x.Length>0&&!x.StartsWith("#")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int index=0;
        foreach(var name in order) {
            if(Path.GetFileName(name)!=name){if(strict)throw new Exception("Unexpected plugin name in the saved order.");continue;}
            string file=mods.Select(m=>SafeJoin(m,name)).FirstOrDefault(File.Exists);
            if(file==null) {
                string source=SafeJoin(game,"Data/"+name);
                if(File.Exists(source)){Directory.CreateDirectory(local);file=SafeJoin(local,name);if(!File.Exists(file)){File.Copy(source,file,false);if(Hash(file)!=Hash(source))throw new Exception("Game plugin copy verification failed: "+name);}}
            }
            if(file==null){if(strict&&active.Contains(name))throw new Exception("An active snapshot plugin is missing: "+name);continue;}
            // FNV/MO2 can read file timestamps on startup, not only loadorder.txt.
            // Only installer-owned mod copies are timestamped; the game stays intact.
            File.SetLastWriteTimeUtc(file,new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc).AddMinutes(index++));
            active.Remove(name);
        }
        if(strict&&active.Count>0)throw new Exception("Active plugins are missing from the saved load order: "+String.Join(", ",active));
    }
    [DllImport("d3d9.dll",CallingConvention=CallingConvention.StdCall)]
    static extern IntPtr Direct3DCreate9(uint version);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int AdapterIdentifier(IntPtr self,uint adapter,uint flags,IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint ReleaseD3D(IntPtr self);
    public static string GraphicsAdapterName() {
        IntPtr d3d=Direct3DCreate9(32),buffer=IntPtr.Zero;
        if(d3d==IntPtr.Zero)throw new Exception("DirectX could not detect a graphics adapter. Run the game launcher once and check your graphics drivers.");
        try {
            IntPtr table=Marshal.ReadIntPtr(d3d);buffer=Marshal.AllocHGlobal(1104);
            var identify=(AdapterIdentifier)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table,5*IntPtr.Size),typeof(AdapterIdentifier));
            if(identify(d3d,0,0,buffer)<0)throw new Exception("Could not read the graphics adapter used by the game.");
            string name=Marshal.PtrToStringAnsi(IntPtr.Add(buffer,512));
            if(String.IsNullOrWhiteSpace(name))throw new Exception("The graphics adapter returned an empty name. Run the game launcher once before installing.");
            return name.Trim();
        } finally {
            if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);
            var release=(ReleaseD3D)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(d3d),2*IntPtr.Size),typeof(ReleaseD3D));release(d3d);
        }
    }
    void Configure(Options o) {
        string g=o.Game.Replace('\\','/'),d=o.Destination.Replace('\\','/');
        var ini=new StringBuilder();
        ini.AppendLine("[General]\r\ngameName=New Vegas\r\nselected_profile=@ByteArray(Default)\r\nversion=2.5.2\r\nfirst_start=false");
        ini.AppendLine("gamePath="+GamePathValue(o.Game));
        ini.AppendLine("game_edition="+(o.Store=="Epic"?"Epic Games":o.Store));
        ini.AppendLine("[Settings]\r\nbase_directory="+Q(d)+"\r\nmod_directory="+Q(d+"/mods")+"\r\nprofiles_directory="+Q(d+"/profiles")+"\r\noverwrite_directory="+Q(d+"/overwrite")+"\r\ndownload_directory="+Q(d+"/downloads")+"\r\nprofile_local_inis=true\r\nprofile_local_saves="+o.LocalSaves.ToString().ToLowerInvariant()+"\r\nprofile_archive_invalidation=true\r\nlanguage=en\r\ncheck_for_updates=false");
        ini.AppendLine("[Widgets]\r\nMainWindow_executablesListBox_index=0");
        ini.AppendLine("[customExecutables]\r\nsize=5");
        string appId=o.Store=="Steam"?SteamAppId(o.Game):"";
        for(int i=1;i<=2;i++) {
            ini.AppendLine(i+"\\title="+(i==1?"New Vegas":"Fallout Launcher"));
            ini.AppendLine(i+"\\binary="+Q(g+(i==1?"/FalloutNV.exe":"/FalloutNVLauncher.exe")));
            ini.AppendLine(i+"\\workingDirectory="+Q(g));
            ini.AppendLine(i+"\\arguments="+(o.Store=="Epic"?"-EpicPortal":"")+"\r\n"+i+"\\ownicon=true\r\n"+i+"\\hide=false\r\n"+i+"\\toolbar=false\r\n"+i+"\\steamAppID="+appId);
        }
        ini.AppendLine("3\\title=GECK\r\n3\\binary="+Q(g+"/GECK.exe")+"\r\n3\\workingDirectory="+Q(g)+"\r\n3\\arguments=\r\n3\\steamAppID=\r\n3\\hide=false");
        ini.AppendLine("4\\title=FNVEdit\r\n4\\binary="+Q(d+"/tools/FNVEdit/FNVEdit.exe")+"\r\n4\\workingDirectory="+Q(g)+"\r\n4\\arguments="+(o.Language=="Russian"?"-cp:1251":"")+"\r\n4\\steamAppID=\r\n4\\hide=false");
        ini.AppendLine(GeckNvseEntry(5,g));
        File.WriteAllText(Path.Combine(o.Destination,"ModOrganizer.ini"),ini.ToString(),Utf8);
        string prefs=SafeJoin(o.Destination,"profiles/Default/falloutprefs.ini");string text=File.ReadAllText(prefs,Utf8);
        text=Ini(text,"Display","iSize W",o.Width.ToString());text=Ini(text,"Display","iSize H",o.Height.ToString());
        text=Ini(text,"Display","bFull Screen",o.Windowed?"0":"1");
        text=Ini(text,"Display","iAdapter","0");text=Ini(text,"Display","sD3DDevice",GraphicsAdapterName());
        File.WriteAllText(prefs,text,Utf8);
        string profile=SafeJoin(o.Destination,"profiles/Default");
        string custom=Path.Combine(profile,"falloutcustom.ini");
        File.WriteAllText(custom,Ini(File.ReadAllText(custom,Utf8),"Display","bFull Screen",o.Windowed?"0":"1"),Utf8);
        string settings=Path.Combine(profile,"settings.ini");
        File.WriteAllText(settings,Ini(Ini(File.ReadAllText(settings,Utf8),"General","LocalSaves",o.LocalSaves.ToString().ToLowerInvariant()),"General","LocalSettings","true"),Utf8);
        string mainIni=Path.Combine(profile,"fallout.ini");
        string mainText=Ini(File.ReadAllText(mainIni,Utf8),"General","sLocalSavePath",o.LocalSaves?"__MO_Saves\\":"Saves\\");
        mainText=Ini(mainText,"General","bUseMyGamesDirectory","1");
        File.WriteAllText(mainIni,mainText,Utf8);
        File.WriteAllText(Path.Combine(profile,"savepath.ini"),"[General]\r\nsLocalSavePath=Saves\\\r\nbUseMyGamesDirectory=1\r\n",Utf8);
        UpdateSupport.EnsureGeckProfileInis(profile);
        foreach(var n in new[]{"downloads","overwrite","profiles/Default/saves"})Directory.CreateDirectory(SafeJoin(o.Destination,n));
    }
    // GECK started through the xNVSE loader (-editor) so GECK Extender and other editor plugins load. g is the game folder with forward slashes.
    public const string GeckNvseTitle="GECK (NVSE)";
    public static string GeckNvseEntry(int index,string g) {
        return index+"\\title="+GeckNvseTitle+"\r\n"+index+"\\binary="+Q(g+"/nvse_loader.exe")+"\r\n"+index+"\\workingDirectory="+Q(g)+"\r\n"+index+"\\arguments=-editor\r\n"+index+"\\steamAppID=\r\n"+index+"\\hide=false";
    }
    public static string SteamAppId(string game) {
        string h=Hash(Path.Combine(game,"FalloutNV.exe"));
        return h=="40d5a7c967a1ec5df1dc19c25f8df3ca74480aa95e8755171567f1129a3001fa"||h=="72e935504b16c7516e5ba2c55ae9b40e65b417af838c009d5068746f61d7f085"?"22490":"22380";
    }
    void CopySaves(Options o,Action<string> log) {
        if(String.IsNullOrWhiteSpace(o.ImportSaves))return;
        string source=Full(o.ImportSaves),target=SafeJoin(o.Destination,"profiles/Default/saves");int count=0;
        foreach(var p in Directory.EnumerateFiles(source)) {
            string ext=Path.GetExtension(p).ToLowerInvariant();if(ext!=".fos"&&ext!=".nvse"&&ext!=".bak")continue;
            CheckCancel();NoLinks(p);string dst=SafeJoin(target,Path.GetFileName(p));File.Copy(p,dst,false);
            if(Hash(p)!=Hash(dst))throw new Exception("Save copy verification failed: "+Path.GetFileName(p));count++;
        }
        log("Copied "+count+" save/sidecar files to the profile. Originals retained.");
    }
}

static class Program {
    [STAThread] static int Main(string[] args) {
        var e=new Engine();
        if(args.Length==0) {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            // Started from the helper inside a setup that was moved: offer to repair it instead of a fresh install.
            string installed=PortableSupport.InstalledFolderOf(Assembly.GetExecutingAssembly().Location);
            if(installed!=null)try{if(PortableSupport.Run(installed,"",false,true,null))return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"Move or repair setup",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}
            Application.Run(new SetupForm(e));return 0;
        }
        var opts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for(int i=1;i+1<args.Length;i+=2)opts[args[i]]=args[i+1];
        Func<string,string,string> get=(k,d)=>opts.ContainsKey(k)?opts[k]:d;
        string log=get("--log",Path.Combine(e.PackageFolder,"command.log"));
        try {
            e.Progress=delegate(string s,int n){File.AppendAllText(log,DateTime.Now.ToString("s")+" "+n+"% "+s+Environment.NewLine,Engine.Utf8);};
            if(args[0]=="--relocate"){bool quiet=Boolean.Parse(get("--quiet","false"));PortableSupport.Run(get("--destination",""),get("--game",""),quiet,false,s=>File.AppendAllText(log,DateTime.Now.ToString("s")+" "+s+Environment.NewLine,Engine.Utf8),null,true,get("--attach-game",""),get("--store","Auto"),Boolean.Parse(get("--defender-exclusions","false")));return 0;}
            if(args[0]=="--play"||args[0]=="--manage") {
                // A moved setup is repaired first, with consent, instead of failing with a path error.
                try{PortableSupport.Run(get("--destination",""),"",false,true,null,null,false);}
                catch(Exception ex){MessageBox.Show(ex.Message,"Move or repair setup",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}
                LaunchSupport.Launch(get("--destination",""),args[0]=="--play");return 0;
            }
            if(args[0]=="--update"){Application.EnableVisualStyles();return UpdateSupport.Run(e,get("--destination",""),Boolean.Parse(get("--quiet","false")),s=>File.AppendAllText(log,DateTime.Now.ToString("s")+" "+s+Environment.NewLine,Engine.Utf8))?0:1;}
            if(args[0]=="--uninstall-ui"){UninstallSupport.Show(get("--destination",""));return 0;}
            if(args[0]=="--uninstall"){UninstallSupport.Remove(e,get("--destination",""));return 0;}
            if(args[0]=="--cat")return CatEncounter.Run(get("--output",""));
            if(args[0]=="--privileged-batch")return PrivilegedBatch.Entry(get("--request",""),e.PackageFolder);
            if(args[0]=="--check-runtimes"){File.WriteAllText(log,Engine.Json().Serialize(Prerequisites.Inspect()),Engine.Utf8);return 0;}
            if(args[0]=="--defender-helper") {
                string destination=Engine.Full(get("--destination","")),result=Engine.SafeJoin(destination,"_setup/defender-result.txt");
                try{string action=get("--action","");if(action!="add"&&action!="remove"&&action!="rehome")throw new Exception("Unknown Defender action.");if(action=="rehome")DefenderSupport.ElevatedRehome(destination,get("--game",""),get("--old-destination",""),get("--old-game",""));else DefenderSupport.Elevated(destination,get("--game",""),action=="add");File.WriteAllText(result,"Completed",Engine.Utf8);return 0;}
                catch(Exception ex){File.WriteAllText(result,ex.Message,Engine.Utf8);return 1;}
            }
            if(args[0]=="--preview") {Application.EnableVisualStyles();using(var f=new SetupForm(e)){f.RenderPreview(Int32.Parse(get("--page","0")),get("--output",Path.Combine(e.PackageFolder,"preview.png")));}return 0;}
            if(args[0]=="--verify-package"){e.VerifyArchives();return 0;}
            if(args[0]=="--restore"){e.Restore(get("--destination",""));return 0;}
            var o=new Options{Game=get("--game",""),Destination=get("--destination",""),Store=get("--store","Auto"),Language=get("--language","Auto"),Width=Int32.Parse(get("--width","1920")),Height=Int32.Parse(get("--height","1080")),InstallRuntimes=Boolean.Parse(get("--install-runtimes","true")),DefenderExclusions=Boolean.Parse(get("--defender-exclusions","false")),Windowed=Boolean.Parse(get("--windowed","true")),LocalSaves=Boolean.Parse(get("--local-saves","true")),DesktopShortcut=Boolean.Parse(get("--desktop","false")),StartMenuShortcut=Boolean.Parse(get("--start-menu","false")),SteamPlay=Boolean.Parse(get("--steam-play","false")),SteamConfig=get("--steam-config",""),ImportSaves=get("--import-saves","")};
            if(args[0]=="--validate")e.Progress(e.Validate(o,true),100);
            else if(args[0]=="--install")e.Install(o);
            else throw new Exception("Unknown command. Use --validate, --install, --restore, or --verify-package.");
            return 0;
        }catch(Exception ex){try{File.AppendAllText(log,"ERROR: "+ex.ToString()+Environment.NewLine,Engine.Utf8);}catch{}if(new[]{"--play","--manage","--uninstall-ui"}.Contains(args[0]))MessageBox.Show(ex.Message,"Modification Framework",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}
    }
}
}
