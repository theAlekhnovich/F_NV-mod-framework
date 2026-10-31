using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FalloutModSetup;
using Microsoft.Win32;
// Uninstaller checks: progress percent, shared runtimes untouched, originals restored, registry entry, form without a TextBox, locked files, the real helper launch.
class UninstallChecks {
 static int count;static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL "+text);count++;Console.WriteLine("PASS "+text);}
 static void Reject(Action action,string text){bool no=false;try{action();}catch{no=true;}Check(no,text);}
 static IEnumerable<Control> All(Control p){yield return p;foreach(Control c in p.Controls)foreach(var x in All(c))yield return x;}
 static void Put(string p,string text){Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllText(p,text);}
 static string Hash(string p){return Engine.Hash(p);}
 static string Runtimes(){return Engine.Json().Serialize(Prerequisites.Inspect());}
 class Fx { public string exeBefore,root,game,dest,exeBackup,bsaBackup,dll,loader,exe,bsa; public List<double> capture=new List<double>(); }
 // A finished setup: patched exe, replaced Invalidation.bsa, two files setup added to the game folder, a mod folder with mods, saves, downloads, a read-only file and a fake runtime installer.
 static Fx Build(string root,int bigFiles=0) {
  var f=new Fx{root=root,game=Path.Combine(root,"Game"),dest=Path.Combine(root,"Mod Folder")};
  f.exe=Path.Combine(f.game,"FalloutNV.exe");f.bsa=Path.Combine(f.game,"Data","Fallout - Invalidation.bsa");f.dll=Path.Combine(f.game,"nvse_1_4.dll");f.loader=Path.Combine(f.game,"nvse_loader.exe");
  Put(f.exe,"original game exe");Put(Path.Combine(f.game,"FalloutNVLauncher.exe"),"launcher");Put(f.bsa,"original invalidation bsa");
  string setup=Path.Combine(f.dest,"_setup");Put(Path.Combine(setup,"owned-marker"),"t");
  f.exeBackup=Path.Combine(setup,"backup","FalloutNV.exe");f.bsaBackup=Path.Combine(setup,"backup","Data","Fallout - Invalidation.bsa");
  Directory.CreateDirectory(Path.GetDirectoryName(f.exeBackup));Directory.CreateDirectory(Path.GetDirectoryName(f.bsaBackup));File.Copy(f.exe,f.exeBackup);File.Copy(f.bsa,f.bsaBackup);
  var state=new InstallState{game=f.game,destination=f.dest,status="installed",version="test"};
  Put(f.exe,"patched game exe");Put(f.bsa,"mod invalidation bsa");Put(f.dll,"nvse dll");Put(f.loader,"nvse loader");
  state.changes.Add(new Change{path="FalloutNV.exe",existed=true,touched=true,before=Hash(f.exeBackup),after=Hash(f.exe)});
  state.changes.Add(new Change{path="Data/Fallout - Invalidation.bsa",existed=true,touched=true,before=Hash(f.bsaBackup),after=Hash(f.bsa)});
  state.changes.Add(new Change{path="nvse_1_4.dll",existed=false,touched=true,after=Hash(f.dll)});
  state.changes.Add(new Change{path="nvse_loader.exe",existed=false,touched=true,after=Hash(f.loader)});
  f.exeBefore=state.changes[0].before;Engine.SaveState(state);
  using(var icon=typeof(Engine).Assembly.GetManifestResourceStream("Lucky38.ico"))using(var o=File.Create(Path.Combine(setup,"Lucky38.ico")))icon.CopyTo(o);
  File.Copy(typeof(Engine).Assembly.Location,Path.Combine(setup,"Setup and Restore.exe"));
  Put(Path.Combine(setup,"add-software","vc_redist.x64.exe"),"fake shared runtime installer");
  Put(Path.Combine(f.dest,"mods","Fixture","asset.txt"),"installed mod file");Put(Path.Combine(f.dest,"mods","ReadOnly","ro.txt"),"read-only file");File.SetAttributes(Path.Combine(f.dest,"mods","ReadOnly","ro.txt"),FileAttributes.ReadOnly);
  Put(Path.Combine(f.dest,"ModOrganizer.ini"),"[General]\r\ngameName=New Vegas\r\n");
  Put(Path.Combine(f.dest,"profiles","Default","saves","s.fos"),"save");Put(Path.Combine(f.dest,"downloads","d.7z"),"download");Put(Path.Combine(f.dest,"overwrite","o.txt"),"overwrite");
  var rnd=new Random(7);for(int i=0;i<bigFiles;i++){var b=new byte[4*1024*1024];rnd.NextBytes(b);Directory.CreateDirectory(Path.Combine(f.dest,"mods","Big"));File.WriteAllBytes(Path.Combine(f.dest,"mods","Big","b"+i+".bin"),b);}
  var e=new Engine();e.Fine=v=>f.capture.Add(v);UninstallSupport.Capture(e,f.dest);
  // After the record was made: MO2 edits its ini, the user adds a file.
  File.AppendAllText(Path.Combine(f.dest,"ModOrganizer.ini"),"; edited later\r\n");Put(Path.Combine(f.dest,"user-added.txt"),"mine");
  return f;
 }
 static readonly HashSet<int> preexisting=new HashSet<int>();
 static readonly DateTime started=DateTime.UtcNow.AddMinutes(-1);
 // The real-process checks leave their temporary uninstaller copies (a running program cannot delete itself); the checks remove the ones they made.
 static void RemoveTempCopies(){try{foreach(var d in Directory.GetDirectories(Path.GetTempPath(),"FNV-Framework-Uninstall-*"))if(Directory.GetCreationTimeUtc(d)>started)try{Directory.Delete(d,true);}catch{}}catch{}}
 static void KillChildren(){try{foreach(var p in Process.GetProcessesByName("Framework Uninstall"))if(!preexisting.Contains(p.Id))try{p.Kill();p.WaitForExit(3000);}catch{}}catch{}}
 // Window steps run on their own STA thread with a time limit: a stalled window system fails the check with a clear message instead of hanging the build.
 static void Ui(string what,Action body){
  Exception error=null;var t=new Thread(()=>{try{body();}catch(Exception ex){error=ex;}});t.SetApartmentState(ApartmentState.STA);t.IsBackground=true;t.Start();
  if(!t.Join(150000)){Console.WriteLine("FAIL window step did not finish within 150 s (the window system stalled): "+what);KillChildren();Environment.Exit(4);}
  if(error!=null)throw new Exception(error.Message,error);
 }
 // A directory junction made with the Win32 API (no cmd.exe, no child process).
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true,CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h,uint code,byte[] input,int inputSize,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
 static void MakeJunction(string link,string target) {
  Directory.CreateDirectory(link);
  using(var h=CreateFile(link,0x40000000,0,IntPtr.Zero,3,0x02000000|0x00200000,IntPtr.Zero)) {
   if(h.IsInvalid)throw new Exception("junction: cannot open "+link+" ("+System.Runtime.InteropServices.Marshal.GetLastWin32Error()+")");
   byte[] sub=Encoding.Unicode.GetBytes("\\??\\"+Path.GetFullPath(target)),print=Encoding.Unicode.GetBytes(Path.GetFullPath(target));int path=sub.Length+2+print.Length+2;var b=new byte[16+path];
   BitConverter.GetBytes(0xA0000003u).CopyTo(b,0);BitConverter.GetBytes((ushort)(8+path)).CopyTo(b,4);BitConverter.GetBytes((ushort)sub.Length).CopyTo(b,10);BitConverter.GetBytes((ushort)(sub.Length+2)).CopyTo(b,12);BitConverter.GetBytes((ushort)print.Length).CopyTo(b,14);
   sub.CopyTo(b,16);print.CopyTo(b,16+sub.Length+2);int ret;
   if(!DeviceIoControl(h,0x000900A4,b,b.Length,IntPtr.Zero,0,out ret,IntPtr.Zero))throw new Exception("junction: cannot set the reparse point ("+System.Runtime.InteropServices.Marshal.GetLastWin32Error()+")");
  }
 }
 static void Unlock(string dest){var ro=Path.Combine(dest,"mods","ReadOnly","ro.txt");if(File.Exists(ro))File.SetAttributes(ro,FileAttributes.Normal);}
 static void ForceDelete(string dir){try{foreach(var p in Directory.GetFiles(dir,"*",SearchOption.AllDirectories))File.SetAttributes(p,FileAttributes.Normal);Directory.Delete(dir,true);}catch{}}
 [STAThread]static void Main(string[] args) {
  string root=args[0],src=args[1];ForceDelete(root);Directory.CreateDirectory(root);
  UninstallForm.SkipDisposeInChecks=true;string testKey=@"Software\FnvUninstallChecks\"+Guid.NewGuid().ToString("N");UninstallSupport.RegistryParent=testKey;
  UninstallSupport.PaceMilliseconds=0;
  foreach(var p in Process.GetProcessesByName("Framework Uninstall"))preexisting.Add(p.Id);
  // The whole suite is bounded: after 8 minutes it reports and stops, and never leaves a window or child process behind.
  var dog=new Thread(()=>{Thread.Sleep(480000);Console.WriteLine("WATCHDOG: the checks were stuck for 8 minutes");KillChildren();Environment.Exit(3);}){IsBackground=true};dog.Start();
  try{Run(root,src,testKey);}catch(Exception ex){Console.WriteLine(ex.ToString());KillChildren();RemoveTempCopies();Environment.Exit(1);}finally{KillChildren();RemoveTempCopies();try{Registry.CurrentUser.DeleteSubKeyTree(@"Software\FnvUninstallChecks",false);}catch{}}
  Console.WriteLine("ALL "+count+" UNINSTALL CHECKS PASSED");
 }
 static void Run(string root,string src,string testKey) {
  // ---- shared runtimes: static guards ----
  foreach(var n in new[]{"_setup/add-software/vc_redist.x64.exe","VC_redist.x86.exe","vcredist_x86.exe","DXSETUP.exe","dxwebsetup.exe","ndp48-x86-x64-allos-enu.exe","dotnet-runtime-8.0.1-win-x64.exe","windowsdesktop-runtime-8.0.1-win-x64.exe","add-software/anything.bin"})Check(UninstallSupport.IsSharedRuntime(n),"runtime installer is never deleted: "+n);
  foreach(var n in new[]{"mods/Fixture/asset.txt","ModOrganizer.exe","mods/X/directx.esp"})Check(!UninstallSupport.IsSharedRuntime(n),"ordinary mod file is not mistaken for a runtime: "+n);
  Check(UninstallSupport.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows))&&UninstallSupport.IsProtectedFolder(Environment.SystemDirectory)&&UninstallSupport.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))&&!UninstallSupport.IsProtectedFolder(root),"Windows and Program Files can never be uninstalled; a mod folder can");
  Reject(()=>UninstallSupport.Remove(new Engine(),Environment.GetFolderPath(Environment.SpecialFolder.Windows)),"uninstall refuses the Windows folder");
  string source=File.ReadAllText(Path.Combine(src,"UninstallSupport.cs"));
  Check(!Regex.IsMatch(source,@"msiexec|Prerequisites\.|\.Ensure\(|Prerequisites\b[^\r\n]*Run",RegexOptions.IgnoreCase)&&!source.Contains("Process.Start(new ProcessStartInfo(\"msiexec"),"uninstall code never calls Prerequisites or msiexec (no runtime is installed or removed)");
  Check(source.Contains("RuntimeNotice")&&UninstallSupport.RuntimeNotice.Contains("Visual C++")&&UninstallSupport.RuntimeNotice.Contains("DirectX")&&UninstallSupport.RuntimeNotice.Contains(".NET"),"the uninstall screen states that C++, DirectX and .NET stay");

  // ---- full flow through the window, off the UI thread ----
  string runtimesBefore=Runtimes();
  var A=Build(Path.Combine(root,"A"),12);
  Check(A.capture.Count>0&&A.capture.All(v=>v>=98&&v<=100)&&A.capture.Zip(A.capture.Skip(1),(a,b)=>b>=a).All(x=>x),"recording the installed files reports exact progress between 98% and 100%, never backwards");
  Check(UninstallSupport.WriteRegistry(A.dest),"the Windows Apps entry is written");
  string keyName;using(var parent=Registry.CurrentUser.OpenSubKey(testKey)){var names=parent.GetSubKeyNames();Check(names.Length==1&&names[0].StartsWith("FalloutNewVegasModFramework-"),"one entry per mod folder");keyName=names[0];}
  using(var k=Registry.CurrentUser.OpenSubKey(testKey+"\\"+keyName)) {
   string helper=Path.Combine(A.dest,"_setup","Setup and Restore.exe");
   Check((string)k.GetValue("DisplayName")=="Fallout New Vegas - mod framework","Apps entry name");
   Check((string)k.GetValue("DisplayIcon")==Path.Combine(A.dest,"_setup","Lucky38.ico")&&File.Exists((string)k.GetValue("DisplayIcon")),"Apps entry uses the Lucky38 icon file");
   Check((string)k.GetValue("UninstallString")=="\""+helper+"\" --uninstall-ui --destination "+Engine.Q(A.dest),"Apps entry uninstall command is the helper with --uninstall-ui --destination");
   Check((string)k.GetValue("InstallLocation")==A.dest&&Convert.ToInt32(k.GetValue("NoModify"))==1,"Apps entry names the mod folder");
  }
  Check(UninstallSupport.RegisteredFolders().SequenceEqual(new[]{A.dest}),"the registry lists the mod folder");
  Check(UninstallSupport.Locate("",Path.Combine(root,"nowhere","x.exe"))==A.dest,"no folder given: the mod folder comes from the registry");
  Check(UninstallSupport.Locate(Path.Combine(root,"stale folder"),Path.Combine(root,"nowhere","x.exe"))==A.dest,"a stale folder (setup moved, shortcut broken): the registry still finds the mod folder");
  Check(UninstallSupport.Locate(A.dest,Path.Combine(root,"nowhere","x.exe"))==A.dest&&UninstallSupport.Locate("",Path.Combine(A.dest,"_setup","Setup and Restore.exe"))==A.dest,"the folder on the command line, or the helper's own folder, is found without the registry");
  Check(UninstallSupport.Inspect(A.dest)==null,"a healthy setup has nothing to warn about");
  var moved=new InstallState{game=A.game,destination=Path.Combine(root,"elsewhere"),status="installed"};var keep=File.ReadAllText(Path.Combine(A.dest,"_setup","state.json"));File.WriteAllText(Path.Combine(A.dest,"_setup","state.json"),Engine.Json().Serialize(moved));
  Check((UninstallSupport.Inspect(A.dest)??"").Contains(PortableSupport.RepairCommand),"a moved mod folder is explained before the button is pressed");File.WriteAllText(Path.Combine(A.dest,"_setup","state.json"),keep);

  Ui("window contents",()=>{
  using(var form=new UninstallForm(A.dest)) {
   var h=form.Handle;form.Show();Application.DoEvents();
   var controls=All(form).ToList();
   Check(!controls.OfType<TextBoxBase>().Any(),"the uninstall window has no text box");
   Check(!controls.OfType<ProgressBar>().Any()&&controls.OfType<AmberProgressBar>().Count()==1,"the bar is the amber one, not the green system bar");
   var bar=controls.OfType<AmberProgressBar>().Single();var labels=controls.OfType<Label>().ToList();
   var pct=labels.Single(l=>l.Text=="00.00%");var desc=labels.Single(l=>l.Text.StartsWith("Ready"));
   Check(R(form,bar).Bottom<=R(form,pct).Top+2&&R(form,pct).Bottom<=R(form,desc).Top+2,"order: progress bar, then 00.00%, then the description text");
   Check(pct.ForeColor==Color.FromArgb(243,167,112)&&pct.Font.Bold,"the percentage is a bold amber label like the installer's");
   Check(form.Icon!=null&&form.ShowInTaskbar&&form.Icon.Width>0,"the window has the Lucky38 icon and appears in the taskbar");
   Check(labels.Any(l=>l.Text.Contains("not removed")&&l.Text.Contains("DirectX")&&l.Text.Contains("Visual C++")),"the window says shared runtimes are not removed");
   Check(labels.Any(l=>l.Text.Contains("Invalidation.bsa")&&l.Text.Contains("FalloutNV.exe")),"the window names the game files it puts back");
   var startB=Find<Button>(form,"StartButton");var cancelB=Find<Button>(form,"CancelButton");var finishB=Find<Button>(form,"FinishButton");var savesB=Find<CheckBox>(form,"SavesBox");var deleteB=Find<CheckBox>(form,"DeleteFolderBox");
   Check(startB.Text=="Start"&&startB.Visible&&startB.Enabled&&cancelB.Text=="Cancel"&&cancelB.Visible&&cancelB.Enabled&&!finishB.Visible,"Start and Cancel are there; Finish only appears at the end");
   Check(!savesB.Checked&&savesB.Visible&&savesB.Text.Contains("Move my save files to the game's default folder")&&!deleteB.Visible&&!deleteB.Checked,"the save-moving box is off by default; the folder-deleting box is hidden");
   var exeHash=Hash(A.exe);for(int i=0;i<40;i++){Application.DoEvents();Thread.Sleep(10);}
   Check(!form.Busy&&Hash(A.exe)==exeHash&&File.Exists(Path.Combine(A.dest,"mods","Fixture","asset.txt"))&&form.PercentText=="00.00%","the window does not start by itself: nothing changes until Start is pressed");
   form.ShowPercent(5);Check(form.PercentText=="05.00%","percent format 00.00% (5)");form.ShowPercent(12.3456);Check(form.PercentText=="12.35%","percent format 00.00% (12.3456)");form.ShowPercent(3);Check(form.PercentText=="12.35%","percent never moves backwards");
   form.ShowPercent(100);Check(form.PercentText=="100.00%","percent 100.00%");
   form.Close();
  }
  });
  // Cancel before Start only closes the window.
  Ui("cancel before start, amber bar, start to finish",()=>{
  using(var form=new UninstallForm(A.dest)) {
   var h=form.Handle;form.Show();Application.DoEvents();Click(Find<Button>(form,"CancelButton"));Application.DoEvents();
   Check(form.WasClosed&&File.Exists(Path.Combine(A.dest,"mods","Fixture","asset.txt"))&&File.ReadAllText(A.exe)=="patched game exe","Cancel before Start simply closes the window and changes nothing");
  }
  // The amber bar really paints amber on a dark track, and it is the installer's amber.
  var wizardAmber=(Color)typeof(SetupForm).GetField("Amber",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
  Check(AmberProgressBar.Fill==wizardAmber,"the uninstaller's bar colour is the installer's amber");
  using(var b=new AmberProgressBar{Width=400,Height=20,Maximum=10000,Value=5000})using(var bmp=new Bitmap(400,20)) {
   b.DrawToBitmap(bmp,new Rectangle(0,0,400,20));var left=bmp.GetPixel(100,10);var right=bmp.GetPixel(300,10);
   Check(Math.Abs(left.R-wizardAmber.R)<4&&Math.Abs(left.G-wizardAmber.G)<4&&Math.Abs(left.B-wizardAmber.B)<4&&right.R<80&&right.B<60,"the bar paints amber up to its value and a dark track after it");
  }
  using(var form=new UninstallForm(A.dest)) {
   var h=form.Handle;form.Show();Application.DoEvents();
   var seen=new List<string>();int pumps=0;var task=form.StartUninstall();
   Check(form.Busy,"Start begins the work at once and the window stays alive");
   var until=DateTime.UtcNow.AddSeconds(120);
   while(!task.IsCompleted&&DateTime.UtcNow<until){Application.DoEvents();pumps++;if(seen.Count==0||seen.Last()!=form.PercentText)seen.Add(form.PercentText);Thread.Sleep(2);}
   Check(task.IsCompleted,"the uninstall in the window finishes in time");Application.DoEvents();
   var values=seen.Concat(new[]{form.PercentText}).Select(s=>{Check(Regex.IsMatch(s,@"^\d{2,3}\.\d\d%$"),"percent label format "+s);return double.Parse(s.TrimEnd('%'),System.Globalization.CultureInfo.InvariantCulture);}).ToList();
   Check(values.Zip(values.Skip(1),(a,b)=>b>=a).All(x=>x)&&values.Last()==100&&values.Count>=2,"the label only moves forward and ends at 100.00% ("+values.Count+" values seen while polling)");
   Check(pumps>3,"the window kept processing messages while uninstalling");
   Check(form.StatusText.StartsWith("Framework uninstalled.")&&form.StatusText.Contains("Visual C++")&&!form.Busy&&form.Finished,"the result is shown on the screen");
   Check(Find<Button>(form,"FinishButton").Visible&&!Find<Button>(form,"StartButton").Visible&&!Find<Button>(form,"CancelButton").Visible,"at the end only Finish is shown");
   Check(Find<CheckBox>(form,"DeleteFolderBox").Visible&&!Find<CheckBox>(form,"DeleteFolderBox").Checked&&form.StatusText.Contains("ModOrganizer.ini")&&form.StatusText.Contains("user-added.txt"),"leftover user files: the unchecked delete box appears and the window lists what would be lost");
   Click(Find<Button>(form,"FinishButton"));Application.DoEvents();
   Check(form.WasClosed&&Directory.Exists(A.dest)&&File.Exists(Path.Combine(A.dest,"user-added.txt")),"Finish closes the window and keeps the files");
  }
  });
  Check(Runtimes()==runtimesBefore,"Visual C++, DirectX and .NET status is identical before and after uninstall");
  Check(File.ReadAllText(A.exe)=="original game exe"&&Hash(A.exe)==A.exeBefore,"FalloutNV.exe is the original again");
  Check(File.ReadAllText(A.bsa)=="original invalidation bsa","Fallout - Invalidation.bsa is the original again");
  Check(!File.Exists(A.dll)&&!File.Exists(A.loader),"files setup added to the game folder are removed");
  Check(!File.Exists(Path.Combine(A.dest,"mods","Fixture","asset.txt"))&&!File.Exists(Path.Combine(A.dest,"mods","ReadOnly","ro.txt")),"unchanged framework files are removed, a read-only one too");
  Check(File.Exists(Path.Combine(A.dest,"profiles","Default","saves","s.fos"))&&File.Exists(Path.Combine(A.dest,"downloads","d.7z"))&&File.Exists(Path.Combine(A.dest,"overwrite","o.txt"))&&File.Exists(Path.Combine(A.dest,"user-added.txt")),"saves, downloads, overwrite and new files stay");
  Check(File.ReadAllText(Path.Combine(A.dest,"ModOrganizer.ini")).Contains("edited later"),"an edited file stays");
  Check(File.Exists(Path.Combine(A.dest,"_setup","add-software","vc_redist.x64.exe")),"a recorded runtime installer was not deleted");
  Check(File.Exists(Path.Combine(A.dest,"UNINSTALLED.txt"))&&File.ReadAllText(Path.Combine(A.dest,"UNINSTALLED.txt")).Contains("Game files restored from the backup (2)"),"UNINSTALLED.txt lists what was restored");
  using(var parent=Registry.CurrentUser.OpenSubKey(testKey))Check(parent==null||parent.GetSubKeyNames().Length==0,"the Windows Apps entry is gone after uninstall");

  // ---- percent while removing, exact (Engine.Fine) ----
  var B=Build(Path.Combine(root,"B"),25);var be=new Engine();var seq=new List<double>();var ints=new List<double>();var inLoop=new List<double>();
  be.Progress=(s,n)=>ints.Add(n);be.Fine=v=>{seq.Add(v);if(v>15&&v<97)inLoop.Add(v);};
  var rb=UninstallSupport.Remove(be,B.dest);
  Check(seq.Count>5&&seq.Zip(seq.Skip(1),(a,b)=>b>=a).All(x=>x)&&ints.Zip(ints.Skip(1),(a,b)=>b>=a).All(x=>x)&&seq.First()>=0&&seq.Last()==100&&ints.Last()==100,"Remove reports Progress and Fine together, never backwards, ending at 100 ("+seq.Count+" reports)");
  Check(inLoop.Distinct().Count()>=3&&inLoop.Zip(inLoop.Skip(1),(a,b)=>b>=a).All(x=>x),"the removal loop reports exact percent between restoring and finishing ("+inLoop.Distinct().Count()+" values)");
  Check(rb.GameRestored==2&&rb.GameRemoved==2&&rb.GameKept==0&&rb.Failed==0&&rb.RuntimeKept==1&&rb.Removed>=27&&rb.Kept>=1&&rb.Complete,"result counts: 2 originals restored, 2 added files removed, runtime installer kept, "+rb.Removed+" removed");
  Check(rb.KeptFiles.Contains("ModOrganizer.ini")&&rb.Summary.Contains("restored from the backup")&&rb.Summary.Contains("Visual C++"),"the summary says plainly what was restored and what stayed");
  Check(Runtimes()==runtimesBefore,"runtime status still identical after the second uninstall");
  Check(!Directory.Exists(Path.Combine(B.dest,"mods","Fixture"))&&!Directory.Exists(Path.Combine(B.dest,"mods","Big")),"emptied folders are removed");
  Check(rb.Removed>0&&Directory.Exists(Path.Combine(B.dest,"profiles")),"folders with saves stay");
  var again=UninstallSupport.Remove(new Engine(),B.dest);Check(again.Removed==0&&again.Failed==0&&File.ReadAllText(B.exe)=="original game exe","a repeated uninstall is safe");

  // ---- a game file edited after installation: kept and reported, uninstall goes on; the Restore command stays strict ----
  var C=Build(Path.Combine(root,"C"));File.WriteAllText(C.dll,"someone edited this nvse file");
  Reject(()=>new Engine().Restore(C.dest),"the Restore command still refuses when a game file was edited");Check(File.ReadAllText(C.exe)=="patched game exe","a refused restore changed nothing");
  var rc=UninstallSupport.Remove(new Engine(),C.dest);
  Check(rc.GameKept==1&&File.ReadAllText(C.dll)=="someone edited this nvse file"&&File.ReadAllText(C.exe)=="original game exe"&&!File.Exists(C.loader)&&rc.Summary.Contains("1 edited files kept"),"an edited game file is kept and reported; the rest is restored");

  // ---- game folder gone ----
  var G=Build(Path.Combine(root,"G"));Directory.Delete(G.game,true);
  Reject(()=>new Engine().Restore(G.dest),"the Restore command refuses a missing game folder instead of recreating it");
  var rg=UninstallSupport.Remove(new Engine(),G.dest);Check(!Directory.Exists(G.game)&&rg.Summary.Contains("was not found")&&rg.Removed>0,"uninstall with the game folder gone removes the mod files and does not recreate the game folder");

  // ---- an older setup without a receipt ----
  var D=Build(Path.Combine(root,"D"));File.Delete(Path.Combine(D.dest,"_setup","installed-files.json"));
  var rd=UninstallSupport.Remove(new Engine(),D.dest);
  Check(!rd.HadReceipt&&File.ReadAllText(D.exe)=="original game exe"&&File.Exists(Path.Combine(D.dest,"mods","Fixture","asset.txt"))&&rd.Summary.Contains("no file list"),"no receipt: game files are restored, mod files are kept, and it says so");

  // ---- a locked file does not abandon the rest; the entry stays; a second run finishes ----
  var E=Build(Path.Combine(root,"E"));UninstallSupport.WriteRegistry(E.dest);
  string locked=Path.Combine(E.dest,"mods","Fixture","asset.txt");
  RegistryParentCount(testKey,1,"entry written for the locked fixture");
  UninstallResult re;using(var hold=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.None)) {re=UninstallSupport.Remove(new Engine(),E.dest);}
  Check(re.Failed==1&&re.FailedFiles[0].Contains("asset.txt")&&!re.Complete&&File.Exists(locked)&&!File.Exists(Path.Combine(E.dest,"mods","ReadOnly","ro.txt"))&&File.ReadAllText(E.exe)=="original game exe","a locked file is reported; every other file was still handled");
  Check(re.Summary.Contains("could not be removed")&&re.Summary.Contains("run Uninstall again"),"the result tells what to do");
  RegistryParentCount(testKey,1,"the Apps entry stays while something is left");
  var re2=UninstallSupport.Remove(new Engine(),E.dest);Check(re2.Failed==0&&re2.Removed==1&&!File.Exists(locked)&&re2.Complete,"running Uninstall again removes the remaining file");
  RegistryParentCount(testKey,0,"the Apps entry is removed once nothing failed");

  // ---- relocate ----
  var H=Build(Path.Combine(root,"H"));UninstallSupport.WriteRegistry(H.dest);string newDest=Path.Combine(root,"H","Moved Mod Folder");
  Check(UninstallSupport.RelocateRegistry(H.dest,newDest),"relocating writes the new entry");RegistryParentCount(testKey,1,"relocation replaces the entry instead of adding one");
  using(var parent=Registry.CurrentUser.OpenSubKey(testKey))using(var k=parent.OpenSubKey(parent.GetSubKeyNames()[0]))Check((string)k.GetValue("InstallLocation")==newDest&&((string)k.GetValue("UninstallString")).Contains(Engine.Q(newDest)),"the entry points at the new folder");
  UninstallSupport.RemoveRegistry(newDest);RegistryParentCount(testKey,0,"RemoveRegistry removes it");

  // ---- saves: copied to the game's folder, verified, never overwriting; the originals go only after every copy is verified ----
  var V=Build(Path.Combine(root,"V"));
  string pa=Path.Combine(V.dest,"profiles","Default","saves"),pb=Path.Combine(V.dest,"profiles","Second","saves"),target=Path.Combine(root,"V","My Games","FalloutNV","Saves");
  Put(Path.Combine(pa,"Save1.fos"),"A1");Put(Path.Combine(pa,"Save1.nvse"),"A1 co");Put(Path.Combine(pa,"Save2.fos"),"A2");Put(Path.Combine(pb,"Save1.fos"),"B1");Put(Path.Combine(pb,"Save1.nvse"),"B1 co");Put(Path.Combine(pb,"readme.txt"),"not a save");
  Put(Path.Combine(target,"Save2.fos"),"A2");Put(Path.Combine(target,"Save1.fos"),"D1 different existing save");
  Check(UninstallSupport.FindSaves(V.dest).Count==6&&UninstallSupport.DefaultSavesFolder().EndsWith(Path.Combine("My Games","FalloutNV","Saves")),"saves are found in every profile; the default folder is Documents\\My Games\\FalloutNV\\Saves");
  var rv=UninstallSupport.Remove(new Engine(),V.dest,new UninstallOptions{MoveSaves=true,SavesTarget=target});
  Check(File.ReadAllText(Path.Combine(target,"Save1.fos"))=="D1 different existing save"&&File.ReadAllText(Path.Combine(target,"Save2.fos"))=="A2","a different file that was already there is never overwritten");
  Check(File.ReadAllText(Path.Combine(target,"Save1 (from mod folder).fos"))=="A1"&&File.ReadAllText(Path.Combine(target,"Save1 (from mod folder).nvse"))=="A1 co","the clashing save is kept under a new name together with its co-save");
  Check(File.ReadAllText(Path.Combine(target,"Save1 (from mod folder 2).fos"))=="B1"&&File.ReadAllText(Path.Combine(target,"Save1 (from mod folder 2).nvse"))=="B1 co"&&File.ReadAllText(Path.Combine(target,"s.fos"))=="save","a second profile's clash gets its own name; a new save is copied as it is");
  Check(rv.SavesCopied==5&&rv.SavesAlready==1&&rv.SavesRenamed==2&&rv.SavesMoved==6&&rv.Summary.Contains("Saves: 5 copied")&&rv.Summary.Contains("verified by hash"),"counts: 5 copied, 1 identical already there, 2 renamed, 6 originals moved; reported in the summary");
  Check(UninstallSupport.FindSaves(V.dest).Count==0&&File.Exists(Path.Combine(pb,"readme.txt")),"the moved saves are gone from the mod folder; other files stay");
  // Without the box nothing is touched; with it a failed copy stops everything before anything is removed.
  var V2=Build(Path.Combine(root,"V2"));Put(Path.Combine(V2.dest,"profiles","Default","saves","Z.fos"),"Z");string blocked=Path.Combine(root,"V2","blocked");Put(blocked,"a file where the save folder should be");
  Reject(()=>UninstallSupport.Remove(new Engine(),V2.dest,new UninstallOptions{MoveSaves=true,SavesTarget=blocked}),"if the saves cannot be copied, the uninstall stops");
  Check(File.ReadAllText(V2.exe)=="patched game exe"&&File.Exists(Path.Combine(V2.dest,"profiles","Default","saves","Z.fos"))&&File.Exists(Path.Combine(V2.dest,"mods","Fixture","asset.txt")),"a failed save copy leaves the game, the saves and the mod folder untouched");

  // ---- junk goes, a folder holding only junk is deleted completely, nothing outside is touched ----
  var J=Build(Path.Combine(root,"J"));
  Put(Path.Combine(J.dest,"mods","Fixture","__pycache__","x.cpython-38.pyc"),"pyc");Put(Path.Combine(J.dest,"mods","Fixture","stray.pyo"),"pyo");Put(Path.Combine(J.dest,"logs","mo_interface.log"),"log");Put(Path.Combine(J.dest,"overwrite","DiaMoveNVSE.log"),"log");Put(Path.Combine(J.dest,"webcache","w.bin"),"w");Put(Path.Combine(J.dest,"crashDumps","x.dmp"),"d");
  File.Delete(Path.Combine(J.dest,"user-added.txt"));File.Delete(Path.Combine(J.dest,"ModOrganizer.ini"));File.Delete(Path.Combine(J.dest,"downloads","d.7z"));File.Delete(Path.Combine(J.dest,"overwrite","o.txt"));File.Delete(Path.Combine(J.dest,"_setup","add-software","vc_redist.x64.exe"));
  Put(Path.Combine(root,"J","outside.txt"),"next to the mod folder");
  foreach(var j in new[]{"mods/x/__pycache__/a.pyc","a.PYC","logs/a.log","overwrite/NVSE/x.log","crashDumps/a.dmp","webcache/a","_setup/state.json"})Check(UninstallSupport.IsJunk(j,true),"junk: "+j);
  foreach(var j in new[]{"profiles/Default/saves/a.fos","downloads/a.log","downloads/a.7z","ModOrganizer.ini","user-added.txt","_setup/state.json"})Check(!UninstallSupport.IsJunk(j,false),"not junk: "+j);
  var rj=UninstallSupport.Remove(new Engine(),J.dest,new UninstallOptions{MoveSaves=true,SavesTarget=Path.Combine(root,"J","saves-target")});
  Check(rj.FolderRemoved&&!Directory.Exists(J.dest)&&rj.LeftOver.Count==0&&rj.Junk>=6&&rj.Failed==0&&rj.Summary.Contains("mod folder removed"),"only recognised junk was left: the whole mod folder is deleted ("+rj.Junk+" junk files)");
  Check(File.Exists(Path.Combine(root,"J","outside.txt"))&&File.Exists(Path.Combine(root,"J","saves-target","s.fos"))&&File.ReadAllText(J.exe)=="original game exe"&&Directory.Exists(J.game),"nothing outside the mod folder was deleted; saves were moved first; the game is restored");

  // ---- Cancel stops safely in the middle; Start continues ----
  var L=Build(Path.Combine(root,"L"),70);var cts=new CancellationTokenSource();var le=new Engine();le.Cancel=cts.Token;
  le.Fine=v=>{if(v>=40)cts.Cancel();};
  var rl=UninstallSupport.Remove(le,L.dest);
  int bigLeft=Directory.Exists(Path.Combine(L.dest,"mods","Big"))?Directory.GetFiles(Path.Combine(L.dest,"mods","Big")).Length:0;
  Check(rl.Cancelled&&bigLeft>0&&bigLeft<70,"Cancel stopped the removal part-way ("+bigLeft+" of 70 files left)");
  Check(rl.Summary.StartsWith("Stopped by you")&&rl.Summary.Contains("Start again")&&rl.Summary.Contains("original game files restored")&&rl.Summary.Contains("framework files removed"),"the result says what was already done and that Start continues");
  Check(!rl.Complete&&Directory.Exists(Path.Combine(L.dest,"_setup"))&&File.ReadAllText(L.exe)=="original game exe"&&Directory.GetFiles(Path.Combine(L.dest,"mods","Big")).All(p=>new FileInfo(p).Length==4*1024*1024),"after a stop the records are kept and every file is either removed or whole");
  var rl2=UninstallSupport.Remove(new Engine(),L.dest);
  Check(!rl2.Cancelled&&rl2.GameAlreadyRestored&&rl2.Summary.Contains("already restored")&&!Directory.Exists(Path.Combine(L.dest,"mods","Big")),"running again continues and finishes");
  // Cancel during the save copy leaves the mod folder untouched.
  var L2=Build(Path.Combine(root,"L2"));var cts2=new CancellationTokenSource();cts2.Cancel();var le2=new Engine();le2.Cancel=cts2.Token;
  var rl3=UninstallSupport.Remove(le2,L2.dest,new UninstallOptions{MoveSaves=true,SavesTarget=Path.Combine(root,"L2","saves")});
  Check(rl3.Cancelled&&File.ReadAllText(L2.exe)=="patched game exe"&&File.Exists(Path.Combine(L2.dest,"profiles","Default","saves","s.fos"))&&File.Exists(Path.Combine(L2.dest,"mods","Fixture","asset.txt")),"Cancel before the work starts changes nothing");

  // ---- the delete-everything choice: confirmation, links are never followed ----
  var K=Build(Path.Combine(root,"K"));string outside=Path.Combine(root,"K","outside");Put(Path.Combine(outside,"precious.txt"),"keep me");
  MakeJunction(Path.Combine(K.dest,"linked"),outside);
  Check(Directory.Exists(Path.Combine(K.dest,"linked")),"fixture: a junction inside the mod folder");
  // The two blocks that press real buttons in a separate process need an idle desktop: set FNVMF_UI_CHECKS=1 to run them.
  if(Environment.GetEnvironmentVariable("FNVMF_UI_CHECKS")=="1"){
  // This one runs the real window in its own process and drives it with real window messages (Start, the delete box, the confirmation dialog, Finish).
  var kBefore=Process.GetProcessesByName("Framework Uninstall").Select(p=>p.Id).ToList();
  var kHelper=Process.Start(new ProcessStartInfo(Path.Combine(K.dest,"_setup","Setup and Restore.exe"),"--uninstall-ui --destination "+Engine.Q(K.dest)){UseShellExecute=false,WorkingDirectory=K.dest});
  Process kc=null;for(int i=0;i<150&&kc==null;i++){Thread.Sleep(100);kc=Process.GetProcessesByName("Framework Uninstall").FirstOrDefault(p=>!kBefore.Contains(p.Id));}
  IntPtr kTop=IntPtr.Zero;for(int i=0;i<50&&kTop==IntPtr.Zero;i++){kTop=Win.Top((uint)kc.Id,"Uninstall");if(kTop==IntPtr.Zero)Thread.Sleep(100);}
  Win.Click(Win.Child(kTop,"Start"));IntPtr kFinish=IntPtr.Zero;for(int i=0;i<300&&kFinish==IntPtr.Zero;i++){Thread.Sleep(100);kFinish=Win.Child(kTop,"Finish");}
  const string deleteText="Delete the whole mod folder, including my edited and new files (cannot be undone)";
  var kBox=Win.Child(kTop,deleteText);
  Check(kFinish!=IntPtr.Zero&&kBox!=IntPtr.Zero&&Win.Texts(kTop).Any(t=>t.Contains("linked")&&t.Contains("user-added.txt")),"the exact delete-folder box appears and the window lists what would be lost, the link too");
  Check(Directory.Exists(Path.Combine(K.dest,"linked"))&&File.Exists(Path.Combine(K.dest,"UNINSTALLED.txt")),"while the box is off the folder and its summary file stay");
  Win.Click(kBox);Thread.Sleep(300);var kDelete=Win.Child(kTop,"Delete folder and finish");Check(kDelete!=IntPtr.Zero&&Win.Child(kTop,"Finish")==IntPtr.Zero,"ticking the box turns the button into Delete folder and finish");
  Win.Click(kDelete);IntPtr dlg=IntPtr.Zero;for(int i=0;i<50&&dlg==IntPtr.Zero;i++){Thread.Sleep(100);dlg=Win.Top((uint)kc.Id,"Delete the mod folder");}
  Thread.Sleep(400);Check(dlg!=IntPtr.Zero&&Win.Texts(dlg).Any(t=>t.Contains("cannot be undone")&&t.Contains("Mod Folder")),"a confirmation names the folder and says it cannot be undone"+(dlg==IntPtr.Zero?" (no dialog)":" ["+String.Join(" | ",Win.Texts(dlg))+"]"));
  Win.Click(Win.Child(dlg,"&No"));Thread.Sleep(500);Check(Directory.Exists(K.dest)&&!kc.HasExited,"answering No deletes nothing and keeps the window");
  Win.Click(Win.Child(kTop,"Delete folder and finish"));dlg=IntPtr.Zero;for(int i=0;i<50&&dlg==IntPtr.Zero;i++){Thread.Sleep(100);dlg=Win.Top((uint)kc.Id,"Delete the mod folder");}
  Win.Click(Win.Child(dlg,"&Yes"));Check(kc.WaitForExit(20000)&&!Directory.Exists(K.dest),"answering Yes deletes the whole mod folder and the window closes");
  Check(File.ReadAllText(Path.Combine(outside,"precious.txt"))=="keep me"&&Directory.Exists(K.game),"the junction target outside the folder was never followed or deleted");

  }
  // ---- shortcuts and other non-game items never stop an uninstall ----
  var SC=Build(Path.Combine(root,"SC"));
  var places=new IntegrationLocations{Desktop=Path.Combine(root,"SC","Desktop"),Programs=Path.Combine(root,"SC","Programs"),RequireSteamClosed=delegate{}};Directory.CreateDirectory(places.Desktop);Directory.CreateDirectory(places.Programs);
  string deskPlay=Path.Combine(places.Desktop,Integrations.PlayName+".lnk"),deskUninstall=Path.Combine(places.Desktop,Integrations.UninstallName+".lnk"),menuManage=Path.Combine(places.Programs,Integrations.MenuFolder,Integrations.ManageName+".lnk"),menuPlay=Path.Combine(places.Programs,Integrations.MenuFolder,Integrations.PlayName+".lnk"),gamePlay=Path.Combine(SC.game,Integrations.PlayName+".lnk");
  File.WriteAllText(deskPlay,"previous shortcut");
  Integrations.Apply(new Options{Game=SC.game,Destination=SC.dest,Store="Steam",DesktopShortcut=true,StartMenuShortcut=true},s=>{},places);
  string helperPath=Path.Combine(SC.dest,"_setup","Setup and Restore.exe");
  Check(File.Exists(deskUninstall)&&File.Exists(menuManage)&&File.Exists(gamePlay)&&File.Exists(menuPlay),"fixture: setup created its desktop, Start menu and game-folder shortcuts");
  // 1. Windows (pinning, migration) rewrote one of ours: different bytes, still the helper.
  Integrations.Shortcut(deskUninstall,helperPath,"--uninstall-ui --destination \"elsewhere\"",SC.dest,helperPath+",0");
  // 2. The user repointed one to another program.
  Integrations.Shortcut(menuManage,Path.Combine(Environment.SystemDirectory,"notepad.exe"),"",SC.dest,Path.Combine(Environment.SystemDirectory,"notepad.exe")+",0");
  // 3. The desktop shortcut that existed before setup was replaced by ours; ours now has changed bytes too.
  Integrations.Shortcut(deskPlay,helperPath,"--play --destination \"moved\"",SC.dest,helperPath+",0");
  Check(UninstallSupport.PointsToHelper(UninstallSupport.ShortcutTarget(deskUninstall))&&!UninstallSupport.PointsToHelper(UninstallSupport.ShortcutTarget(menuManage)),"fixture: the rewritten shortcut still points to the helper, the repointed one does not");
  Reject(()=>Integrations.Restore(SC.dest,false,places),"the strict Restore command still refuses a shortcut that changed");
  var rsc=UninstallSupport.Remove(new Engine{Places=places},SC.dest);
  Check(rsc.Complete&&rsc.GameRestored==2&&File.ReadAllText(SC.exe)=="original game exe","the uninstall completes and restores the game although shortcuts had changed");
  Check(!File.Exists(deskUninstall)&&!File.Exists(menuPlay)&&!File.Exists(gamePlay),"a shortcut whose bytes changed but that still points to the helper is removed, with the untouched ones");
  Check(File.Exists(menuManage)&&rsc.Notes.Any(n=>n.Contains("Kept the shortcut")&&n.Contains("notepad")&&n.Contains("Manage")==false||n.Contains("Kept the shortcut")&&n.Contains("notepad")),"a shortcut redirected to another program is kept and reported");
  Check(File.ReadAllText(deskPlay)=="previous shortcut","the shortcut that existed before setup is put back from its verified backup");
  Check(rsc.Summary.Contains("Shortcuts and Steam: Kept the shortcut")&&rsc.Summary.Contains("notepad"),"the summary lists the kept shortcut");
  // A damaged shortcut backup and an unreadable journal are reported, never fatal.
  var SC2=Build(Path.Combine(root,"SC2"));var places2=new IntegrationLocations{Desktop=Path.Combine(root,"SC2","Desktop"),Programs=Path.Combine(root,"SC2","Programs"),RequireSteamClosed=delegate{}};Directory.CreateDirectory(places2.Desktop);Directory.CreateDirectory(places2.Programs);
  File.WriteAllText(Path.Combine(places2.Desktop,Integrations.PlayName+".lnk"),"previous shortcut");
  Integrations.Apply(new Options{Game=SC2.game,Destination=SC2.dest,Store="Steam",DesktopShortcut=true,StartMenuShortcut=false},s=>{},places2);
  foreach(var bk in Directory.GetFiles(Path.Combine(SC2.dest,"_setup"),"shortcut-*.lnk"))File.WriteAllText(bk,"tampered backup");
  var rsc2=UninstallSupport.Remove(new Engine{Places=places2},SC2.dest);
  Check(rsc2.Complete&&rsc2.Notes.Any(n=>n.Contains("could not be put back"))&&File.ReadAllText(SC2.exe)=="original game exe","a changed shortcut backup is reported, the previous shortcut is not restored from it, and the uninstall still completes");
  var SC3=Build(Path.Combine(root,"SC3"));File.WriteAllText(Path.Combine(SC3.dest,"_setup","integrations.json"),"{ this is not json");
  var rsc3=UninstallSupport.Remove(new Engine{Places=places2},SC3.dest);
  Check(rsc3.Complete&&rsc3.Notes.Any(n=>n.Contains("could not be restored"))&&File.ReadAllText(SC3.exe)=="original game exe","an unreadable shortcut record is reported and the game is still restored");

  // ---- the temporary copy blocked by an antivirus: clear message, fallback in place, postponed removal ----
  var T=Build(Path.Combine(root,"T"));string tHelper=Path.Combine(T.dest,"_setup","Setup and Restore.exe");
  UninstallSupport.CopyCreated=p=>File.Delete(p);string why;bool started=UninstallSupport.TryStartCopy(tHelper,T.dest,out why);UninstallSupport.CopyCreated=null;
  Check(!started&&!String.IsNullOrEmpty(why),"a temporary copy that disappears right after it was made is detected ("+why+")");
  Check(UninstallSupport.BlockedMessage.Contains("Actions > Allow on device")&&UninstallSupport.BlockedMessage.Contains("Protection history")&&!UninstallSupport.BlockedMessage.ToLowerInvariant().Contains("turn off protection")&&!UninstallSupport.BlockedMessage.ToLowerInvariant().Contains("exclusion"),"the message explains Allow on device and never suggests turning protection off or adding exclusions");
  foreach(var j in new[]{"mods","logs"})try{Directory.CreateDirectory(Path.Combine(T.dest,j));}catch{}
  File.Delete(Path.Combine(T.dest,"ModOrganizer.ini"));File.Delete(Path.Combine(T.dest,"user-added.txt"));File.Delete(Path.Combine(T.dest,"downloads","d.7z"));File.Delete(Path.Combine(T.dest,"overwrite","o.txt"));File.Delete(Path.Combine(T.dest,"profiles","Default","saves","s.fos"));File.Delete(Path.Combine(T.dest,"_setup","add-software","vc_redist.x64.exe"));
  var rt=UninstallSupport.Remove(new Engine(),T.dest,new UninstallOptions{InPlaceHelper=tHelper});
  Check(rt.InPlace&&rt.HelperPending&&rt.Complete&&!rt.FolderRemoved&&File.Exists(tHelper)&&File.ReadAllText(T.exe)=="original game exe"&&rt.Summary.Contains("Windows cannot delete a program while it runs. Close this window and delete the folder")&&rt.Summary.Contains("it only holds the uninstaller now"),"running in place: the game is restored, everything else removed, only the helper is left and the summary says to delete the folder");
  Check(Directory.GetFiles(T.dest,"*",SearchOption.AllDirectories).Length==1&&File.Exists(tHelper),"nothing but the helper is left in the folder, and no hidden command was started to remove it");
  // Old temporary copies are removed in-process, only other runs' copies and only when old enough.
  string oldCopy=Path.Combine(Path.GetTempPath(),"FNV-Framework-Uninstall-oldcheck"+Guid.NewGuid().ToString("N")),newCopy=Path.Combine(Path.GetTempPath(),"FNV-Framework-Uninstall-newcheck"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(oldCopy);File.WriteAllText(Path.Combine(oldCopy,"x.exe"),"x");Directory.SetCreationTimeUtc(oldCopy,DateTime.UtcNow.AddHours(-3));Directory.CreateDirectory(newCopy);File.WriteAllText(Path.Combine(newCopy,"x.exe"),"x");
  UninstallSupport.CleanOldCopies();Check(!Directory.Exists(oldCopy)&&Directory.Exists(newCopy),"copies of earlier runs older than an hour are removed; a recent one stays");try{Directory.Delete(newCopy,true);}catch{}
  string srcText=File.ReadAllText(Path.Combine(src,"UninstallSupport.cs"));
  Check(!Regex.IsMatch(srcText,@"cmd\.exe|ping -n|rmdir|""/c |for /l",RegexOptions.IgnoreCase),"UninstallSupport.cs contains no cmd.exe, ping, rmdir or delayed-delete command at all");
  // ---- layout: every control stays visible at 100/125/150/200% and on a 1366x768 laptop ----
  Ui("layout at several DPI scales",()=>{foreach(var sc in new[]{1.0f,1.25f,1.5f,2.0f})foreach(var area in new[]{new Size(1920,1040),new Size(1366,728)})Layout(root,A.dest,sc,area);});
  // ---- the real helper: shortcut command -> temporary copy outside the mod folder -> uninstall window ----
  var S=Build(Path.Combine(root,"S"));string helperExe=Path.Combine(S.dest,"_setup","Setup and Restore.exe");
  var before=Process.GetProcessesByName("Framework Uninstall").Select(p=>p.Id).ToList();
  var shortcut=Process.Start(new ProcessStartInfo(helperExe,"--uninstall-ui --destination "+Engine.Q(S.dest)){UseShellExecute=false,WorkingDirectory=S.dest});
  Process copy=null;for(int i=0;i<150&&copy==null;i++){Thread.Sleep(100);copy=Process.GetProcessesByName("Framework Uninstall").FirstOrDefault(p=>!before.Contains(p.Id));}
  Check(copy!=null,"the helper starts the uninstall window from a copy of itself");
  string copyPath=null;try{copyPath=copy.MainModule.FileName;}catch{}
  Check(copyPath!=null&&Path.GetFileName(Path.GetDirectoryName(copyPath)).StartsWith("FNV-Framework-Uninstall-")&&!Engine.Within(copyPath,S.dest),"the running uninstaller lives in %TEMP%, outside the mod folder");
  string title="";for(int i=0;i<50&&!title.Contains("Uninstall");i++){Thread.Sleep(100);copy.Refresh();title=copy.MainWindowTitle;}
  Check(title.Contains("Uninstall"),"the uninstall window opened ("+title+")");
  Check(shortcut.WaitForExit(10000)&&shortcut.ExitCode==0,"the helper itself exits right after starting the copy");
  Check(File.ReadAllText(S.exe)=="patched game exe","opening the window changes nothing");
  if(Environment.GetEnvironmentVariable("FNVMF_UI_CHECKS")=="1"){
  // A real user: press Start and then Finish with real window messages, in the real process.
  IntPtr top=IntPtr.Zero;for(int i=0;i<50&&top==IntPtr.Zero;i++){top=Win.Top((uint)copy.Id);if(top==IntPtr.Zero)Thread.Sleep(100);}
  var realStart=Win.Child(top,"Start");Check(realStart!=IntPtr.Zero&&Win.Child(top,"Cancel")!=IntPtr.Zero&&Win.Child(top,"Finish")==IntPtr.Zero,"the real window shows Start and Cancel, no Finish");
  Thread.Sleep(1500);Check(File.ReadAllText(S.exe)=="patched game exe","the real window waits for Start");
  Win.Click(realStart);IntPtr realFinish=IntPtr.Zero;for(int i=0;i<300&&realFinish==IntPtr.Zero;i++){Thread.Sleep(100);realFinish=Win.Child(top,"Finish");}
  Check(realFinish!=IntPtr.Zero&&File.ReadAllText(S.exe)=="original game exe"&&Win.Texts(top).Any(t=>t.StartsWith("Framework uninstalled.")),"Start runs the uninstall in the real window and Finish appears");
  Check(Win.Texts(top).Any(t=>t=="100.00%"),"the real window ends at 100.00%");
  Win.Click(realFinish);Check(copy.WaitForExit(20000),"Finish closes the real window and the process ends");
  }
  try{if(!copy.HasExited){copy.Kill();copy.WaitForExit(5000);}}catch{}
  try{if(copyPath!=null&&Directory.Exists(Path.GetDirectoryName(copyPath)))Directory.Delete(Path.GetDirectoryName(copyPath),true);}catch{}
  Console.WriteLine("preview: "+Path.Combine(root,"uninstall-preview.png"));
 }
 // Raises the click directly: Button.PerformClick takes the keyboard focus, which can block when another window owns it.
 static void Click(Button b){typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(b,new object[]{EventArgs.Empty});}
 static class Win {
  delegate bool EnumProc(IntPtr h,IntPtr l);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr l);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h,EnumProc p,IntPtr l);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h,System.Text.StringBuilder s,int n);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
  static string Text(IntPtr h){var s=new System.Text.StringBuilder(2000);GetWindowText(h,s,2000);return s.ToString();}
  public static IntPtr Top(uint pid,string title="Uninstall"){IntPtr r=IntPtr.Zero;EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&IsWindowVisible(h)&&Text(h).Contains(title)){r=h;return false;}return true;},IntPtr.Zero);return r;}
  public static IntPtr Child(IntPtr top,string text){IntPtr r=IntPtr.Zero;EnumChildWindows(top,(h,l)=>{if(IsWindowVisible(h)&&Text(h)==text){r=h;return false;}return true;},IntPtr.Zero);return r;}
  public static List<string> Texts(IntPtr top){var r=new List<string>();EnumChildWindows(top,(h,l)=>{if(IsWindowVisible(h))r.Add(Text(h));return true;},IntPtr.Zero);return r;}
  // Posted, not sent: the click may open a modal dialog, and a sent message would wait for it.
  public static void Click(IntPtr h){PostMessage(h,0xF5,IntPtr.Zero,IntPtr.Zero);}
 }
 static T Find<T>(Control p,string name) where T:Control {return All(p).OfType<T>().Single(c=>c.Name==name);}
 static Rectangle R(Form f,Control c){return f.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));}
 static void Layout(string root,string dest,float scale,Size area) {
  string tag=(int)(scale*100)+"% on "+area.Width+"x"+area.Height;
  using(var form=new UninstallForm(dest,new UninstallLook{Scale=scale,Area=area})) {
   var h=form.Handle;form.Show();Application.DoEvents();Application.DoEvents();form.PerformLayout();Application.DoEvents();
   var client=form.ClientRectangle;var problems=new List<string>();
   foreach(var name in new[]{"StartButton","CancelButton","ProgressBar","PercentLabel"}) {
    var c=All(form).Single(x=>x.Name==name);var r=R(form,c);
    if(!c.Visible||r.Width<=0||r.Height<=0||!client.Contains(r))problems.Add(name+" "+r+" outside "+client);
   }
   foreach(var name in new[]{"InfoPanel","StatusPanel"}){var c=All(form).Single(x=>x.Name==name);var r=R(form,c);if(r.Height<(int)(24*scale)||!client.Contains(r))problems.Add(name+" too small or outside: "+r);}
   if(form.Width>area.Width||form.Height>area.Height||form.Left<0||form.Top<0)problems.Add("window "+form.Bounds+" does not fit "+area);
   var start=R(form,All(form).Single(x=>x.Name=="StartButton"));var stat=R(form,All(form).Single(x=>x.Name=="StatusPanel"));var bar=R(form,All(form).Single(x=>x.Name=="ProgressBar"));
   if(start.Bottom<client.Bottom-(int)(40*scale)||start.Right<client.Right-(int)(60*scale))problems.Add("buttons are not at the bottom right: "+start+" in "+client);
   if(!(bar.Bottom<=All(form).Where(x=>x.Name=="PercentLabel").Select(x=>R(form,x).Top).Single()+3))problems.Add("percentage is not under the bar");
   Check(problems.Count==0,"every control is visible at "+tag+(problems.Count>0?" -- "+String.Join("; ",problems):""));
   try{using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));bmp.Save(Path.Combine(root,"uninstall-"+(int)(scale*100)+"-"+area.Width+"x"+area.Height+".png"));}}catch(Exception ex){Console.WriteLine("preview failed: "+ex.Message);}
   form.Close();
  }
 }
 static void RegistryParentCount(string key,int expected,string text) {
  using(var parent=Registry.CurrentUser.OpenSubKey(key)){int n=parent==null?0:parent.GetSubKeyNames().Length;Check(n==expected,text);}
 }
}



