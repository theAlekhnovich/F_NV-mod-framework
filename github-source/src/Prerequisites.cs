using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
namespace FalloutModSetup {
public class RuntimePackage { public string id,title,path,arguments; public FileItem[] files,reference; }
public class RuntimeStatus { public string Id,Title,Detail; public bool Ready; }
public class DxDll { public string Name; public int Major,Minor,Build; }
public static class Prerequisites {
    public static RuntimePackage[] Packages() { using(var s=new StreamReader(typeof(Engine).Assembly.GetManifestResourceStream("prerequisites.json")))return Engine.Json().Deserialize<RuntimePackage[]>(s.ReadToEnd()); }
    static bool Dll(string folder,string name,int major=0,int minor=0,int build=0) {
        string path=Path.Combine(folder,name);if(!File.Exists(path))return false;
        if(major==0)return true;
        var v=FileVersionInfo.GetVersionInfo(path);return new Version(v.FileMajorPart,v.FileMinorPart,v.FileBuildPart,v.FilePrivatePart)>=new Version(major,minor,build,0);
    }
    // The 32-bit DirectX libraries the game and the pack's plugins load (see dist\V1.8-RUNTIMES-NOTES.txt).
    public static readonly DxDll[] DirectX={new DxDll{Name="d3dx9_38.dll",Major=9,Minor=23,Build=949},new DxDll{Name="d3dx9_43.dll",Major=9,Minor=29,Build=952},new DxDll{Name="xinput1_3.dll",Major=9,Minor=18,Build=944}};
    // What the last Ensure call found and did, for the installer log (Installer.cs appends it to install.log).
    public static readonly List<string> Report=new List<string>();
    public static List<RuntimeStatus> Inspect() {
        string win=Environment.GetFolderPath(Environment.SpecialFolder.Windows),x86=Path.Combine(win,"SysWOW64"),x64=Path.Combine(win,"System32");
        var list=new List<RuntimeStatus>();
        foreach(var p in Packages()) {
            bool ready=false;string detail="Missing or older than required";
            if(p.id=="vc14x86"||p.id=="vc14x64") { string folder=p.id=="vc14x86"?x86:x64;var names=new List<string>{"vcruntime140.dll","msvcp140.dll","msvcp140_1.dll","msvcp140_2.dll","msvcp140_atomic_wait.dll"};if(p.id=="vc14x64")names.Add("vcruntime140_1.dll");
                var gone=names.Where(n=>!Dll(folder,n,14,40,33810)).ToList();ready=gone.Count==0;if(!ready)detail="Missing or older than required: "+String.Join(", ",gone); }
            if(p.id=="vc12x86")ready=Dll(x86,"msvcr120.dll",12,0,40660)&&Dll(x86,"msvcp120.dll",12,0,40660);
            // Galaxy's imported VC11 ABI is present in earlier VC11 releases too.
            // Do not force an unrelated system upgrade when these dependencies already load.
            if(p.id=="vc11x86")ready=Dll(x86,"msvcr110.dll",11)&&Dll(x86,"msvcp110.dll",11);
            if(p.id=="vc9x86")ready=Directory.EnumerateDirectories(Path.Combine(win,"WinSxS"),"x86_microsoft.vc90.crt_*").Any(d=>Dll(d,"msvcr90.dll",9,0,30729)&&Dll(d,"msvcp90.dll",9,0,30729));
            // x86 only: FalloutNV.exe imports d3dx9_38 + xinput1_3, NVTF.dll imports d3dx9_43, DiaMoveNVSE.dll imports xinput1_3. MO2 (x64) imports no DirectX DLL.
            // Minimum versions are the June 2010 redistributable's own, so an empty or stub file does not count as installed.
            if(p.id=="dx9") {
                var gone=DirectX.Where(d=>!Dll(x86,d.Name,d.Major,d.Minor,d.Build)).Select(d=>d.Name).ToList();
                ready=gone.Count==0;if(!ready)detail="Missing or older than required: "+String.Join(", ",gone);
            }
            list.Add(new RuntimeStatus{Id=p.id,Title=p.title,Ready=ready,Detail=ready?"Installed":detail});
        }
        bool net=false;using(var h=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry32))using(var k=h.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))if(k!=null)net=Convert.ToInt32(k.GetValue("Release",0))>=528040;
        list.Add(new RuntimeStatus{Id="net48",Title="Microsoft .NET Framework 4.8 or newer",Ready=net,Detail=net?"Installed":"Install .NET Framework 4.8, then reopen setup"});return list;
    }
    public static string Folder(string packageFolder) {
        foreach(string p in new[]{Path.Combine(packageFolder,"add-software"),Path.Combine(Path.GetDirectoryName(packageFolder),"add-software")})if(Directory.Exists(p))return Engine.Full(p);
        throw new Exception("Required software is missing. Keep the supplied add-software folder beside the installer folder or beside this EXE.");
    }
    public static void Verify(RuntimePackage p,string root) {
        foreach(var f in p.files) { string path=Engine.SafeJoin(root,f.path);if(!File.Exists(path)||new FileInfo(path).Length!=f.size||Engine.Hash(path)!=f.hash)throw new Exception("Missing or changed prerequisite: "+f.path+". Restore the supplied Microsoft installer files."); }
    }
    // Inspects the runtimes and checks that everything missing can be installed from the supplied, verified installers. Returns what is missing; installs nothing, needs no administrator rights.
    public static List<RuntimeStatus> Plan(string packageFolder,bool install,Func<List<RuntimeStatus>> inspect=null) {
        inspect=inspect??Inspect;var initial=inspect();Report.Clear();
        foreach(var s in initial)Report.Add("Required software: "+s.Title+" - "+(s.Ready?"present":"missing ("+s.Detail+")"));
        if(initial.Any(x=>x.Id=="net48"&&!x.Ready))throw new Exception("Install Microsoft .NET Framework 4.8 from dotnet.microsoft.com/download/dotnet-framework/net48, then reopen setup.");
        var missing=initial.Where(x=>!x.Ready).ToList();if(missing.Count==0)return missing;
        if(!install)throw new Exception("Required software is missing: "+String.Join(", ",missing.Select(x=>x.Title))+". Enable Install missing required software on the Required software step.");
        string root=Folder(packageFolder);
        foreach(var item in missing)Verify(Packages().Single(p=>p.id==item.Id),root);
        return missing;
    }
    public static bool IsAdmin(){using(var id=System.Security.Principal.WindowsIdentity.GetCurrent())return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);}
    public static void Ensure(string packageFolder,bool install,Action<string> log,Func<List<RuntimeStatus>> inspect=null,Func<RuntimePackage,string,int> run=null) {
        inspect=inspect??Inspect;run=run??Run;
        var missing=Plan(packageFolder,install,inspect);if(missing.Count==0){log("Required Microsoft runtimes are already installed. Nothing to install.");return;}
        string root=Folder(packageFolder);
        foreach(var item in missing) {
            var p=Packages().Single(x=>x.id==item.Id);log("Installing "+p.title+" ("+(missing.IndexOf(item)+1)+" of "+missing.Count+"). This can take a minute.");int code=run(p,root);Report.Add("Required software: "+p.title+" installer exit code "+code);
            // 3010 = installed, restart advised (a running program held a file). The libraries are usable when they now pass inspection; 1641 means a restart was started.
            if(code==3010&&inspect().Single(x=>x.Id==item.Id).Ready){Report.Add("Required software: "+p.title+" installed; a Windows restart is advised");log(p.title+" installed. A Windows restart is advised after setup.");continue;}
            if(code==3010||code==1641)throw new Exception(p.title+" requires a Windows restart. Restart and run setup again; no game files have been changed."+Diagnostics(p));
            if(code!=0&&code!=1638)throw new Exception(p.title+" installation failed (exit "+code+")."+Diagnostics(p)+" No game files have been changed.");
            var after=inspect().Single(x=>x.Id==item.Id);
            if(!after.Ready)throw new Exception(p.title+" is still missing after setup ("+after.Detail+"). Restart Windows or repair that runtime, then retry."+Diagnostics(p));
        }
        log("All required Microsoft runtimes are available.");
    }
    // DXSETUP writes its own log to the Windows folder; its last lines say which cabinet or file it could not install.
    static string Diagnostics(RuntimePackage p) {
        if(p.id!="dx9")return "";
        try {
            string win=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach(string f in new[]{Path.Combine(win,"DXError.log"),Path.Combine(win,"Logs","DXError.log")})if(File.Exists(f)) {
                var lines=File.ReadAllLines(f).Where(l=>l.Trim().Length>0).Reverse().Take(3).Reverse().ToArray();
                if(lines.Length>0)return " DXError.log: "+String.Join(" | ",lines.Select(l=>l.Trim()))+".";
            }
        } catch {}
        return "";
    }
    static int Run(RuntimePackage package,string folder) {
        var info=new ProcessStartInfo(Engine.SafeJoin(folder,package.path),package.arguments){WorkingDirectory=folder,UseShellExecute=true};
        // Setup's single administrator step already runs elevated; only a direct call from an unelevated process asks Windows for approval.
        if(!IsAdmin())info.Verb="runas";
        try { using(var p=Process.Start(info)){p.WaitForExit();return p.ExitCode;} }
        catch(System.ComponentModel.Win32Exception ex) { if(ex.NativeErrorCode!=1223)throw; throw new Exception(package.title+" was not installed because the Windows administrator prompt was declined. Run setup again and accept the prompt, or install the file from the add-software folder yourself."); }
    }
}
// ONE elevated step for everything that needs administrator rights (required software, Defender exclusions). The wizard stays unelevated, so files keep the right owner
// and shortcuts land in the right profile. The elevated copy of this EXE reads a small JSON request, appends progress lines to a file that the wizard follows, and
// writes a JSON result before it exits.
public class BatchRequest { public string progress,result,destination,game; public bool runtimes,defender; }
public class BatchResult { public bool runtimesOk=true,declined; public string runtimeError,defenderState="off",defenderError; public int defenderAdded,defenderPresent; public List<string> report=new List<string>(); }
public class BatchSummary { public bool DefenderSkipped; public string DefenderLine=""; public List<string> Lines=new List<string>(); }
// The two privileged steps. Tests replace them with fakes.
public class BatchSteps {
    public Action<string,Action<string>> Runtimes=(folder,say)=>Prerequisites.Ensure(folder,true,say);
    public Func<string,string,DefenderResult> Defender=DefenderSupport.Batch;
}
public class BatchDeclined:Exception { public BatchDeclined():base("The Windows approval prompt was declined."){} }
public static class PrivilegedBatch {
    static string Flat(string s){return (s??"").Replace("\r"," ").Replace("\n"," ").Replace("\t"," ");}
    static void Append(string path,string line){using(var s=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite))using(var w=new StreamWriter(s,Engine.Utf8))w.WriteLine(line);}
    public static string DefenderLine(BatchResult r) {
        if(r.defenderState=="added")return "Defender exclusions: "+r.defenderAdded+" added, "+r.defenderPresent+" were already present.";
        if(r.defenderState=="skipped")return "Defender exclusions: skipped: "+(String.IsNullOrWhiteSpace(r.defenderError)?"Defender did not accept them":r.defenderError);
        return "Defender exclusions: skipped: the required-software step failed first.";
    }
    // Runs inside the elevated process. Never throws: every failure ends up in the result file.
    public static int Execute(string requestPath,string packageFolder,BatchSteps steps=null) {
        BatchRequest req;
        try{req=Engine.Json().Deserialize<BatchRequest>(File.ReadAllText(requestPath,Engine.Utf8));if(String.IsNullOrEmpty(req.progress)||String.IsNullOrEmpty(req.result))return 2;}catch{return 2;}
        steps=steps??new BatchSteps();var res=new BatchResult();
        Action<string> say=s=>{try{Append(req.progress,"S\t"+Flat(s));}catch{}};
        try {
            if(req.runtimes) {
                try{steps.Runtimes(packageFolder,say);}catch(Exception ex){res.runtimesOk=false;res.runtimeError=ex.Message;}
                res.report.AddRange(Prerequisites.Report);
            }
            if(req.defender) {
                if(!res.runtimesOk)res.defenderState="notrun";
                else {
                    say("Adding Microsoft Defender file exclusions...");
                    try{var d=steps.Defender(req.destination,req.game);res.defenderState="added";res.defenderAdded=d.Added;res.defenderPresent=d.Present;}
                    catch(Exception ex){res.defenderState="skipped";res.defenderError=DefenderSupport.CleanError(ex.Message);}
                }
            }
        }catch(Exception ex){res.runtimesOk=false;res.runtimeError=ex.Message;}
        File.WriteAllText(req.result+".tmp",Engine.Json().Serialize(res),Engine.Utf8);
        if(File.Exists(req.result))File.Delete(req.result);
        File.Move(req.result+".tmp",req.result);return 0;
    }
    // --privileged-batch --request <file>
    public static int Entry(string requestPath,string packageFolder) {
        if(!Prerequisites.IsAdmin())return 3;
        return Execute(requestPath,packageFolder);
    }
    static int StartElevated(string args,Action started,Action poll) {
        string exe=typeof(Engine).Assembly.Location;
        var info=new ProcessStartInfo(exe,args){UseShellExecute=true,Verb="runas",WorkingDirectory=Path.GetDirectoryName(exe)};
        Process p;
        try{p=Process.Start(info);}
        catch(System.ComponentModel.Win32Exception ex){if(ex.NativeErrorCode==1223)throw new BatchDeclined();throw;}
        using(p){started();while(!p.WaitForExit(300))poll();poll();return p.ExitCode;}
    }
    // Starts the elevated step and follows it. start(args,approved,poll) returns the exit code; tests replace it. Returns declined=true when the user said no.
    public static BatchResult Run(BatchRequest req,Action<string> status,string approved,Func<string,Action,Action,int> start=null) {
        start=start??StartElevated;
        string dir=Path.Combine(Path.GetTempPath(),"FNVMF-batch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            req.progress=Path.Combine(dir,"progress.txt");req.result=Path.Combine(dir,"result.json");File.WriteAllText(req.progress,"");
            string request=Path.Combine(dir,"request.json");File.WriteAllText(request,Engine.Json().Serialize(req),Engine.Utf8);
            long offset=0;
            Action poll=delegate {
                try {
                    byte[] bytes;using(var s=new FileStream(req.progress,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){s.Seek(offset,SeekOrigin.Begin);bytes=new byte[Math.Max(0,s.Length-offset)];int n=0;while(n<bytes.Length){int k=s.Read(bytes,n,bytes.Length-n);if(k<=0)break;n+=k;}}
                    string text=Engine.Utf8.GetString(bytes);int end=text.LastIndexOf('\n');if(end<0)return;
                    offset+=Engine.Utf8.GetByteCount(text.Substring(0,end+1));
                    foreach(var line in text.Substring(0,end).Split('\n')){var t=line.TrimEnd('\r');if(t.StartsWith("S\t"))status(t.Substring(2));}
                }catch(IOException){}
            };
            status("Waiting for Windows approval. Accept the prompt once; everything that needs it runs in that one step.");
            int code;
            try{code=start("--privileged-batch --request "+Engine.Q(request),delegate{status(approved);},poll);}
            catch(BatchDeclined){return new BatchResult{declined=true};}
            poll();
            if(!File.Exists(req.result))throw new Exception("The administrator step ended without a result (exit code "+code+"). Nothing was installed by it; run setup again and accept the Windows prompt.");
            return Engine.Json().Deserialize<BatchResult>(File.ReadAllText(req.result,Engine.Utf8));
        } finally {try{Directory.Delete(dir,true);}catch{}}
    }
    // The whole privileged part of an installation. Throws when something required could not be done; a Defender refusal alone is reported in the summary.
    public static BatchSummary Perform(string packageFolder,string destination,string game,List<RuntimeStatus> missing,bool defender,Action<string> status,Action<string> log,Func<string,Action,Action,int> start=null) {
        var summary=new BatchSummary();missing=missing??new List<RuntimeStatus>();
        if(missing.Count==0&&!defender){summary.DefenderLine="Defender exclusions: not requested.";return summary;}
        string what=missing.Count>0&&defender?"required software and Defender exclusions":missing.Count>0?"required software":"Defender exclusions";
        var r=Run(new BatchRequest{destination=destination,game=game,runtimes=missing.Count>0,defender=defender},status,"Approval received. Setting up "+what+"; you can leave this window.",start);
        if(r.declined) {
            if(missing.Count>0)throw new Exception("Windows administrator approval was declined, but this setup has to install required Microsoft software first ("+String.Join(", ",missing.Select(x=>x.Title))+"). Nothing was changed. Run setup again and accept the Windows prompt, or install those files from the add-software folder yourself.");
            summary.DefenderSkipped=true;summary.DefenderLine="Defender exclusions: skipped: the Windows approval prompt was declined.";return summary;
        }
        foreach(var line in r.report)if(!Prerequisites.Report.Contains(line))summary.Lines.Add(line);
        if(!r.runtimesOk)throw new Exception(r.runtimeError);
        if(missing.Count>0)summary.Lines.Add("Required software: installed and verified.");
        if(defender){summary.DefenderLine=DefenderLine(r);summary.DefenderSkipped=r.defenderState!="added";}
        else summary.DefenderLine="Defender exclusions: not requested.";
        foreach(var line in summary.Lines)log(line);
        return summary;
    }
}
}
