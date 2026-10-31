using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FalloutModSetup;
// Required-software (C++ / DirectX) checks. Arguments: dist folder (holding add-software), real game folder (optional, read-only).
class RuntimeChecks {
 static int count;static void Check(bool ok,string text){if(!ok)throw new Exception(text);count++;Console.WriteLine("PASS "+text);}
 static void Reject(Action action,string text){bool no=false;try{action();}catch{no=true;}Check(no,text);}
 // Names a PE file imports (normal and delay-load), lower case; null when it is not a PE file.
 static List<string> Imports(string path,out string arch) {
  arch="?";var res=new List<string>();byte[] b=File.ReadAllBytes(path);
  if(b.Length<0x100||b[0]!='M'||b[1]!='Z')return null;
  int pe=BitConverter.ToInt32(b,0x3c);if(pe<0||pe+24>=b.Length||b[pe]!='P'||b[pe+1]!='E')return null;
  ushort mach=BitConverter.ToUInt16(b,pe+4),nsec=BitConverter.ToUInt16(b,pe+6),osz=BitConverter.ToUInt16(b,pe+20);int opt=pe+24;bool p64=BitConverter.ToUInt16(b,opt)==0x20b;
  arch=p64?"x64":(mach==0x14c?"x86":"?");int dd=opt+(p64?112:96);
  var sec=new List<uint[]>();for(int i=0;i<nsec;i++){int o=opt+osz+i*40;sec.Add(new uint[]{BitConverter.ToUInt32(b,o+12),BitConverter.ToUInt32(b,o+8),BitConverter.ToUInt32(b,o+16),BitConverter.ToUInt32(b,o+20)});}
  Func<uint,int> off=rva=>{foreach(var s in sec)if(rva>=s[0]&&rva<s[0]+Math.Max(s[1],s[2]))return (int)(rva-s[0]+s[3]);return 0;};
  Func<int,string> str=o=>{var sb=new StringBuilder();while(o>0&&o<b.Length&&b[o]!=0&&sb.Length<260)sb.Append((char)b[o++]);return sb.ToString().ToLowerInvariant();};
  foreach(var d in new[]{new{Dir=1,Size=20,Name=12},new{Dir=13,Size=32,Name=4}}) {
   uint rva=BitConverter.ToUInt32(b,dd+d.Dir*8);if(rva==0)continue;int o=off(rva);
   while(o>0&&o+d.Size<=b.Length){uint nm=BitConverter.ToUInt32(b,o+d.Name);if(nm==0)break;res.Add(str(off(nm)));o+=d.Size;}
  }
  return res;
 }
 // The privileged part of an installation: one elevated step, run here in-process with fake steps through the real request/progress/result files.
 static void BatchChecks() {
  var missing=new List<RuntimeStatus>{new RuntimeStatus{Id="vc14x86",Title="VC++ 2015-2022 x86"},new RuntimeStatus{Id="dx9",Title="DirectX 9 runtime"}};
  int prompts=0,runtimeRuns=0,defenderRuns=0;List<string> said=null,logged=null;
  Func<BatchSteps,Func<string,Action,Action,int>> starter=steps=>(args,approved,poll)=>{
   prompts++;string path=System.Text.RegularExpressions.Regex.Match(args,"--privileged-batch --request \"(.*)\"").Groups[1].Value.Replace('/','\\');
   Check(File.Exists(path),"the request is a small JSON file whose path is the command-line argument");
   approved();int rc=PrivilegedBatch.Execute(path,@"X:\pkg",steps);poll();return rc;};
  Func<DefenderResult,BatchSteps> fake=d=>new BatchSteps{Runtimes=(f,say)=>{runtimeRuns++;say("Installing VC++ (1 of 2). This can take a minute.");say("Installing DirectX (2 of 2). This can take a minute.");},Defender=(dest,game)=>{defenderRuns++;if(d==null)throw new Exception("Microsoft Defender could not add the file exclusions. Usually another antivirus is the active one.");return d;}};
  Action reset=()=>{said=new List<string>();logged=new List<string>();prompts=runtimeRuns=defenderRuns=0;};
  reset();var r=PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",missing,true,said.Add,logged.Add,starter(fake(new DefenderResult{Added=6,Present=2})));
  Check(prompts==1&&runtimeRuns==1&&defenderRuns==1,"required software and Defender exclusions share ONE elevated step (one Windows prompt)");
  Check(said[0].StartsWith("Waiting for Windows approval")&&said.Any(s=>s.StartsWith("Approval received"))&&said.IndexOf(said.First(s=>s.StartsWith("Approval received")))>0,"live status: Waiting for Windows approval, then Approval received");
  Check(said.Any(s=>s.StartsWith("Installing VC++ (1 of 2)"))&&said.Any(s=>s.StartsWith("Adding Microsoft Defender file exclusions"))&&said.Last().StartsWith("Adding Microsoft Defender"),"live status lines from the elevated step reach the wizard (software, then Defender)");
  Check(r.DefenderLine=="Defender exclusions: 6 added, 2 were already present."&&!r.DefenderSkipped,"Defender result text names the added and the already present exclusions");
  reset();r=PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",new List<RuntimeStatus>(),true,said.Add,logged.Add,starter(fake(new DefenderResult{Added=0,Present=8})));
  Check(r.DefenderLine=="Defender exclusions: 0 added, 8 were already present."&&runtimeRuns==0&&prompts==1,"all eight exclusions already present is stated explicitly (nothing hidden); no software step runs when nothing is missing");
  reset();r=PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",missing,true,said.Add,logged.Add,starter(fake(null)));
  Check(r.DefenderSkipped&&r.DefenderLine.StartsWith("Defender exclusions: skipped: Microsoft Defender could not add")&&runtimeRuns==1,"a Defender failure is reported as skipped with the reason and stays non-fatal");
  reset();Reject(()=>PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",missing,true,said.Add,logged.Add,starter(new BatchSteps{Runtimes=(f,say)=>{runtimeRuns++;throw new Exception("DirectX 9 runtime installation failed (exit 5).");},Defender=(d,g)=>{defenderRuns++;return new DefenderResult();}})),"a failing required-software install stops the installation");
  Check(defenderRuns==0,"Defender exclusions are not added when the required software failed");
  reset();Reject(()=>PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",missing,true,said.Add,logged.Add,(a,ok,poll)=>{prompts++;throw new BatchDeclined();}),"declining the Windows prompt stops the installation when required software is missing");
  string declinedText="";try{PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",missing,true,said.Add,logged.Add,(a,ok,poll)=>{throw new BatchDeclined();});}catch(Exception ex){declinedText=ex.Message;}
  Check(declinedText.Contains("declined")&&declinedText.Contains("VC++ 2015-2022 x86")&&declinedText.Contains("Nothing was changed"),"the decline message names the software and says nothing was changed");
  reset();r=PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",new List<RuntimeStatus>(),true,said.Add,logged.Add,(a,ok,poll)=>{prompts++;throw new BatchDeclined();});
  Check(r.DefenderSkipped&&r.DefenderLine=="Defender exclusions: skipped: the Windows approval prompt was declined.","declining the prompt for Defender alone is non-fatal and reported");
  reset();r=PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",new List<RuntimeStatus>(),false,said.Add,logged.Add,(a,ok,poll)=>{prompts++;return 0;});
  Check(prompts==0&&!r.DefenderSkipped&&r.DefenderLine=="Defender exclusions: not requested.","no prompt at all when nothing needs administrator rights");
  reset();r=PrivilegedBatch.Perform(@"X:\pkg",null,null,missing,false,said.Add,logged.Add,starter(fake(null)));
  Check(prompts==1&&defenderRuns==0&&runtimeRuns==1,"the Required software page button uses the same single step without Defender");
  Reject(()=>PrivilegedBatch.Perform(@"X:\pkg",@"C:\Mods",@"C:\Game",missing,false,said.Add,logged.Add,(a,ok,poll)=>0),"an elevated step that leaves no result file is an error, not a silent success");
  // Defender counts (the text above comes from these numbers).
  string root=Path.Combine(Path.GetTempPath(),"fnv-batch-defender-"+Guid.NewGuid().ToString("N")),dd=Path.Combine(root,"Mods"),gg=Path.Combine(root,"Game");
  Directory.CreateDirectory(Path.Combine(dd,"_setup"));Directory.CreateDirectory(gg);File.WriteAllText(Path.Combine(dd,"_setup","owned-marker"),"x");
  var have=new HashSet<string>(DefenderSupport.Targets(gg,dd).Take(3),StringComparer.OrdinalIgnoreCase);
  var counts=DefenderSupport.ApplyCore(gg,dd,()=>have,(p,add)=>{if(add)have.Add(p);else have.Remove(p);});
  Check(counts.Added==5&&counts.Present==3&&have.Count==8,"ApplyCore reports 5 added and 3 already present when three exclusions existed");
  try{Directory.Delete(root,true);}catch{}
 } static void Main(string[] args) {
  string dist=args[0],game=args.Length>1?args[1]:"",soft=Path.Combine(dist,"add-software");
  var packages=Prerequisites.Packages();var dx=packages.Single(p=>p.id=="dx9");
  Check(new[]{"vc14x86","vc14x64","vc12x86","vc11x86","vc9x86","dx9"}.All(id=>packages.Any(p=>p.id==id)),"every required runtime package is declared");
  // Evidence from the PE import scan of the pack and game (dist\V1.8-RUNTIMES-NOTES.txt): these 32-bit DirectX DLLs are loaded.
  Check(Prerequisites.DirectX.Select(d=>d.Name).OrderBy(x=>x).SequenceEqual(new[]{"d3dx9_38.dll","d3dx9_43.dll","xinput1_3.dll"}),"DirectX detection covers d3dx9_38, d3dx9_43 and xinput1_3");
  Check(Prerequisites.DirectX.All(d=>d.Major==9&&d.Minor>0&&d.Build>0),"DirectX detection checks a minimum file version, not only existence");
  // Every file in add-software (except the README) is pinned, so nothing shipped can change unnoticed.
  var pinned=new HashSet<string>(packages.SelectMany(p=>p.files.Concat(p.reference??new FileItem[0])).Select(f=>f.path.Replace('\\','/')),StringComparer.OrdinalIgnoreCase);
  var onDisk=Directory.GetFiles(soft,"*",SearchOption.AllDirectories).Select(f=>f.Substring(soft.Length+1).Replace('\\','/')).Where(f=>!f.Equals("README.txt",StringComparison.OrdinalIgnoreCase)).ToList();
  Check(onDisk.All(pinned.Contains),"every shipped installer file in add-software is pinned by SHA-256 ("+onDisk.Count+" files)");
  Check(pinned.All(p=>onDisk.Contains(p,StringComparer.OrdinalIgnoreCase)),"every pinned installer file exists in add-software");
  foreach(var p in packages)Prerequisites.Verify(p,soft);Check(true,"all pinned runtime installers verify (size and SHA-256)");
  Check(dx.reference!=null&&dx.reference.Length==1&&dx.reference[0].path=="directx_Jun2010_redist.exe","original DirectX June 2010 redistributable is pinned as a reference file");
  var rf=dx.reference[0];string rp=Path.Combine(soft,rf.path);Check(new FileInfo(rp).Length==rf.size&&Engine.Hash(rp)==rf.hash,"directx_Jun2010_redist.exe matches its pinned size and SHA-256");
  Check(rf.size>90*1024*1024&&rf.size<110*1024*1024,"directx_Jun2010_redist.exe is the ~96 MB end-user runtime, not the ~570 MB SDK");
  // The DirectX cabinets the game and plugins need must be in the pinned set.
  foreach(string cab in new[]{"DirectX/JUN2008_d3dx9_38_x86.cab","DirectX/Jun2010_d3dx9_43_x86.cab","DirectX/APR2007_xinput_x86.cab","DirectX/DXSETUP.exe","DirectX/dxupdate.cab","DirectX/dxdllreg_x86.cab"})
   Check(dx.files.Any(f=>f.path.Equals(cab,StringComparison.OrdinalIgnoreCase)),"DirectX set includes "+cab.Substring(8));
  Check(dx.path=="DirectX/DXSETUP.exe"&&dx.arguments=="/silent","DirectX is installed through Microsoft's own DXSETUP /silent");
  // Ensure: reporting, restart handling.
  Func<bool,List<RuntimeStatus>> statuses=r=>new List<RuntimeStatus>{new RuntimeStatus{Id="vc14x86",Title="VC test",Ready=r,Detail="Missing or older than required: x.dll"},new RuntimeStatus{Id="net48",Ready=true}};
  bool ready=false;Prerequisites.Ensure(dist,true,s=>{},()=>statuses(ready),(p,f)=>{ready=true;return 3010;});
  Check(ready&&Prerequisites.Report.Any(l=>l.Contains("restart is advised")),"restart advised (3010) after a successful install does not abort setup");
  Reject(()=>Prerequisites.Ensure(dist,true,s=>{},()=>statuses(false),(p,f)=>3010),"restart advised but runtime still missing stops setup");
  Reject(()=>Prerequisites.Ensure(dist,true,s=>{},()=>statuses(false),(p,f)=>1641),"restart started by installer (1641) stops setup");
  ready=false;Prerequisites.Ensure(dist,true,s=>{},()=>statuses(ready),(p,f)=>{ready=true;return 1638;});Check(ready,"newer version already installed (1638) is accepted");
  Prerequisites.Ensure(dist,true,s=>{},()=>statuses(true),(p,f)=>{throw new Exception("must not run");});
  Check(Prerequisites.Report.Count==2&&Prerequisites.Report.All(l=>l.StartsWith("Required software: ")),"report lists every runtime's state for install.log");
  ready=false;Prerequisites.Ensure(dist,true,s=>{},()=>statuses(ready),(p,f)=>{ready=true;return 0;});
  Check(Prerequisites.Report.Any(l=>l.Contains("missing (Missing or older than required: x.dll)"))&&Prerequisites.Report.Any(l=>l.Contains("exit code 0")),"report records what was missing and the installer exit code");
  // Real detection on this PC: structure is complete and matches the files that are really there.
  var found=Prerequisites.Inspect();
  Check(new[]{"vc14x86","vc14x64","vc12x86","vc11x86","vc9x86","dx9","net48"}.All(id=>found.Any(s=>s.Id==id)),"inspection reports all seven requirements");
  string x86=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"SysWOW64");
  bool allThere=Prerequisites.DirectX.All(d=>File.Exists(Path.Combine(x86,d.Name)));
  Check(allThere||!found.Single(s=>s.Id=="dx9").Ready,"DirectX status follows the 32-bit system folder ("+(allThere?"present":"absent")+" here)");
  Check(found.Where(s=>!s.Ready).All(s=>!String.IsNullOrEmpty(s.Detail)),"every missing requirement carries a reason");
  BatchChecks();
  // PE evidence on the real game, when available.
  string exe=Path.Combine(game,"FalloutNV.exe");
  if(game!=""&&File.Exists(exe)) {
   string arch;var im=Imports(exe,out arch);
   Check(arch=="x86"&&im.Contains("d3dx9_38.dll")&&im.Contains("xinput1_3.dll"),"FalloutNV.exe is 32-bit and imports d3dx9_38.dll and xinput1_3.dll");
   string nvse=Path.Combine(game,"nvse_1_4.dll");
   if(File.Exists(nvse)){var n=Imports(nvse,out arch);Check(arch=="x86"&&n.Contains("vcruntime140.dll")&&n.Contains("msvcp140.dll"),"nvse_1_4.dll is 32-bit and needs the v14 x86 runtime");}
  }
  Console.WriteLine("Runtime checks passed: "+count);
 }
}

