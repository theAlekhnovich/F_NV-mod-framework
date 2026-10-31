using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using FalloutModSetup;
// Speed work: parallel extraction/verification must give byte-identical results to the sequential path, and the receipt must describe the real files.
// Arguments: work folder [benchmark megabytes]. With a size, the suite also prints (not asserts) timings on a synthetic package of that size.
class SpeedChecks {
 static int count;
 static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL "+text);count++;Console.WriteLine("PASS "+text);}
 static string Msg(Action a){try{a();return null;}catch(Exception ex){return ex.Message;}}
 static Exception Err(Action a){try{a();return null;}catch(Exception ex){return ex;}}
 // Semi-compressible bytes (a quarter noise, the rest repeating), deterministic per seed.
 static byte[] Data(int seed,int length) {
  var b=new byte[length];ulong x=(ulong)seed*2654435761UL+88172645463325252UL;var pattern=new byte[256];
  for(int i=0;i<256;i++){x^=x<<13;x^=x>>7;x^=x<<17;pattern[i]=(byte)x;}
  for(int i=0;i<length;i+=4096){
   int noise=Math.Min(1024,length-i);for(int k=0;k<noise;k++){x^=x<<13;x^=x>>7;x^=x<<17;b[i+k]=(byte)(x>>11);}
   for(int k=noise;k<4096&&i+k<length;k++)b[i+k]=pattern[k&255];
  }
  return b;
 }
 static string Sha(byte[] b){using(var h=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(h.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
 class Fixture { public Package P;public string Folder;public int Files;public long Bytes; }
 // parts x (small files + big files). Part 2 also holds a superseded copy and a retired file, part 1 holds the old copy.
 static Fixture Build(string dir,int parts,int small,int smallSize,int big,int bigSize) {
  Directory.CreateDirectory(dir);var files=new List<FileItem>();var archives=new List<ArchiveItem>();int seed=1;long bytes=0;
  for(int p=1;p<=parts;p++) {
   string name="Fixture-"+p.ToString("00")+".bin";
   using(var fs=new FileStream(Path.Combine(dir,name),FileMode.Create))using(var zip=new ZipArchive(fs,ZipArchiveMode.Create)) {
    Action<string,byte[],string> add=(path,data,part)=>{var e=zip.CreateEntry(path,CompressionLevel.Fastest);using(var s=e.Open())s.Write(data,0,data.Length);if(part!="skip"){files.Add(new FileItem{path=path,hash=Sha(data),size=data.Length,part=part});bytes+=data.Length;}};
    for(int i=0;i<small;i++)add("app/mods/Mod"+p+"_"+(i/50)+"/sub"+(i%7)+"/f"+i+".dat",Data(seed++,smallSize+(i*37)%900),null);
    for(int i=0;i<big;i++)add("app/mods/Big"+p+"/archive"+i+".bsa",Data(seed++,bigSize),null);
    add("tools/helper"+p+".bin",Data(seed++,5000),null);
    add("profiles/Default/list"+p+".txt",Data(seed++,3000),null);
    if(p==1){var e=zip.CreateEntry("app/mods/Dup/f.dat");var old=Data(7001,4000);using(var s=e.Open())s.Write(old,0,old.Length);}
    if(p==2){
     var fresh=Data(7002,4100);var e=zip.CreateEntry("app/mods/Dup/f.dat");using(var s=e.Open())s.Write(fresh,0,fresh.Length);files.Add(new FileItem{path="app/mods/Dup/f.dat",hash=Sha(fresh),size=fresh.Length,part=name});bytes+=fresh.Length;
     var gone=zip.CreateEntry("app/mods/Old/gone.dat");var g=Data(7003,1000);using(var s=gone.Open())s.Write(g,0,g.Length);
    }
   }
   string full=Path.Combine(dir,name);archives.Add(new ArchiveItem{name=name,hash=Engine.Hash(full),size=new FileInfo(full).Length});
  }
  var pkg=new Package{version="speed-fixture",files=files.ToArray(),archives=archives.ToArray(),installedBytes=bytes,retired=new[]{new FileItem{path="app/mods/Old/gone.dat"}},masters=new MasterItem[0],patches=new PatchItem[0],required=new string[0]};
  return new Fixture{P=pkg,Folder=dir,Files=files.Count,Bytes=bytes};
 }
 static Dictionary<string,string> Tree(string dest){
  var map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var f in Directory.EnumerateFiles(dest,"*",SearchOption.AllDirectories))map[f.Substring(dest.Length+1).Replace('\\','/')]=Engine.Hash(f);
  return map;
 }
 static bool SameTree(Dictionary<string,string> a,Dictionary<string,string> b){return a.Count==b.Count&&a.All(kv=>b.ContainsKey(kv.Key)&&b[kv.Key]==kv.Value);}
 static Engine Make(Fixture f,int workers){return new Engine(f.P,f.Folder){WorkersOverride=workers};}
 static string Extract(Engine e,string root){string dest=Path.Combine(root,"Mods");e.ExtractFiles(dest,Path.Combine(dest,"_setup"));return dest;}
 static List<FileItem> Receipt(string dest){return Engine.Json().Deserialize<OwnedFiles>(File.ReadAllText(Path.Combine(dest,"_setup","installed-files.json"))).files.OrderBy(x=>x.path,StringComparer.Ordinal).ToList();}
 static void Main(string[] args) {
  string root=Path.GetFullPath(args[0]);if(Directory.Exists(root))Directory.Delete(root,true);Directory.CreateDirectory(root);
  var fx=Build(Path.Combine(root,"pkg"),3,160,6000,2,1500000);
  // drive detection and worker choice
  int auto=SpeedSupport.Workers(root);Check(auto>=1&&auto<=4,"the automatic worker count is between 1 and 4 ("+SpeedSupport.Describe(root)+")");
  Check(new Engine(fx.P,fx.Folder){WorkersOverride=3}.WorkersFor(root)==3&&Make(fx,1).WorkersFor(root)==1,"a forced worker count is honoured");
  Check(SpeedSupport.Workers(@"\\server\share\x")==1,"a network path always gets one worker");
  // sequential and parallel extraction give the same files
  var seq=Make(fx,1);string d1=Extract(seq,Path.Combine(root,"seq"));
  var fine=new List<double>();var steps=new List<int>();var par=Make(fx,4);par.Fine=v=>{lock(fine)fine.Add(v);};par.Progress=(s,n)=>{lock(steps)steps.Add(n);};string d4=Extract(par,Path.Combine(root,"par"));
  var t1=Tree(d1);var t4=Tree(d4);
  Check(SameTree(t1,t4)&&t1.Count==fx.Files,"parallel extraction writes exactly the same files as the sequential path ("+t1.Count+" files)");
  Check(seq.ExtractWorkers==1&&par.ExtractWorkers>=2&&par.ExtractWorkers<=4&&seq.ExtractedFiles==fx.Files&&par.ExtractedFiles==fx.Files&&par.ExtractedBytes==fx.Bytes,"every packaged file was extracted once and counted ("+fx.Files+" files)");
  Check(File.ReadAllBytes(Path.Combine(d4,"mods","Dup","f.dat")).Length==4100&&!File.Exists(Path.Combine(d4,"mods","Old","gone.dat")),"the part and retired rules still apply (superseded copy skipped, retired file not written)");
  Check(fx.P.files.All(f=>{string rel=f.path.StartsWith("app/")?f.path.Substring(4):"_setup/"+f.path;return t4[rel]==f.hash;}),"every extracted file matches its inventory hash");
  Check(par.Extracted.Count==fx.Files&&seq.Extracted.Count==fx.Files&&par.Extracted.All(kv=>seq.Extracted[kv.Key].hash==kv.Value.hash&&kv.Value.hash==t4[kv.Key]),"the recorded extraction hashes are the same on both paths and equal the files");
  lock(fine)Check(fine.Zip(fine.Skip(1),(a,b)=>b>=a).All(x=>x),"the exact percentage never moves backwards while workers report");
  lock(steps)Check(steps.Zip(steps.Skip(1),(a,b)=>b>=a).All(x=>x),"the step percentages stay ordered under parallel reporting");
  // receipts
  UninstallSupport.ReuseMargin=TimeSpan.Zero;
  UninstallSupport.Capture(seq,d1);UninstallSupport.Capture(par,d4);
  var r1=Receipt(d1);var r4=Receipt(d4);
  Check(r1.Count==r4.Count&&r1.Zip(r4,(a,b)=>a.path==b.path&&a.hash==b.hash&&a.size==b.size).All(x=>x),"the uninstall receipt is identical for sequential and parallel extraction");
  Check(r4.All(r=>r.hash==Engine.Hash(Path.Combine(d4,r.path.Replace('/','\\')))),"the receipt hashes equal the real file hashes");
  Check(UninstallSupport.LastCapture.StartsWith(fx.Files+" hashes reused"),"the receipt reused the extraction hashes instead of reading every file ("+UninstallSupport.LastCapture+")");
  // reuse is conditional: a changed file is read again, an untouched one keeps its recorded hash
  string edited=Path.Combine(d4,"mods","Mod1_0","sub0","f0.dat");string sameSize=Path.Combine(d4,"mods","Mod1_0","sub1","f1.dat");string untouched=Path.Combine(d4,"mods","Mod1_0","sub2","f2.dat");
  byte[] b0=File.ReadAllBytes(edited);b0[10]^=0xFF;DateTime stamp0=File.GetLastWriteTimeUtc(edited);File.WriteAllBytes(edited,b0);File.SetLastWriteTimeUtc(edited,stamp0.AddSeconds(5));   // same size, new write time
  byte[] b1=File.ReadAllBytes(sameSize);b1[20]^=0x55;DateTime stamp=File.GetLastWriteTimeUtc(sameSize);File.WriteAllBytes(sameSize,b1);File.SetLastWriteTimeUtc(sameSize,stamp.AddSeconds(9));
  string e0="mods/Mod1_0/sub0/f0.dat",e1="mods/Mod1_0/sub1/f1.dat",e2="mods/Mod1_0/sub2/f2.dat";
  par.Extracted[e2].hash="forged-hash-proves-reuse";
  File.Delete(Path.Combine(d4,"_setup","installed-files.json"));UninstallSupport.Capture(par,d4);var r5=Receipt(d4).ToDictionary(x=>x.path);
  Check(r5[e0].hash==Engine.Hash(edited)&&r5[e1].hash==Engine.Hash(sameSize),"a file modified after extraction is hashed again (receipt records the actual installed bytes)");
  Check(r5[e2].hash=="forged-hash-proves-reuse","an untouched file keeps the hash recorded at extraction (proof: a forged record is used)");
  File.WriteAllText(untouched,"now changed and longer than before");File.Delete(Path.Combine(d4,"_setup","installed-files.json"));UninstallSupport.Capture(par,d4);
  Check(Receipt(d4).Single(x=>x.path==e2).hash==Engine.Hash(untouched),"a changed size or write time always overrides the recorded hash");
  UninstallSupport.ReuseMargin=TimeSpan.FromSeconds(3600);par.Extracted[e0].hash="forged-too";File.Delete(Path.Combine(d4,"_setup","installed-files.json"));UninstallSupport.Capture(par,d4);
  Check(Receipt(d4).Single(x=>x.path==e0).hash==Engine.Hash(edited)&&UninstallSupport.LastCapture.StartsWith("0 hashes reused"),"files written in the last moments of extraction are always read again (safety margin)");
  UninstallSupport.ReuseMargin=TimeSpan.FromSeconds(3);
  // failures: damaged data, cancellation
  var bad=Build(Path.Combine(root,"bad"),3,160,6000,2,1500000);bad.P.files[bad.P.files.Length/2].hash=new string('0',64);string badName=bad.P.files[bad.P.files.Length/2].path;
  foreach(int w in new[]{1,4}) {
   string m=Msg(()=>Extract(Make(bad,w),Path.Combine(root,"badrun"+w)));
   Check(m!=null&&m.StartsWith("Damaged extracted file: ")&&m.Contains(badName),"a damaged file is reported with its name ("+w+" worker"+(w>1?"s":"")+")");
  }
  var cancelled=new CancellationTokenSource();cancelled.Cancel();var ec=Make(fx,4);ec.Cancel=cancelled.Token;
  var ex0=Err(()=>Extract(ec,Path.Combine(root,"cancel0")));string c0=Path.Combine(root,"cancel0");
  Check(ex0 is OperationCanceledException&&(!Directory.Exists(c0)||!Directory.EnumerateFiles(c0,"*",SearchOption.AllDirectories).Any()),"an installation that is already cancelled extracts nothing");
  foreach(int w in new[]{1,4}) {
   var cts=new CancellationTokenSource();var ek=Make(fx,w);ek.Cancel=cts.Token;int said=0;ek.Progress=(s,n)=>{if(s.StartsWith("Installing files:")&&Interlocked.Increment(ref said)==1)cts.Cancel();};
   var ex=Err(()=>Extract(ek,Path.Combine(root,"cancel"+w)));int written=Directory.EnumerateFiles(Path.Combine(root,"cancel"+w),"*",SearchOption.AllDirectories).Count();
   Check(ex is OperationCanceledException&&written<fx.Files,"cancelling during extraction stops every worker promptly and reports a cancellation ("+w+" worker"+(w>1?"s":"")+", "+written+" of "+fx.Files+" files written)");
  }
  var wrong=Build(Path.Combine(root,"wrong"),2,40,3000,1,200000);wrong.P.files=wrong.P.files.Take(wrong.P.files.Length-3).ToArray();
  Check((Msg(()=>Extract(Make(wrong,4),Path.Combine(root,"wrongrun")))??"").Contains("inventory"),"a part with a file the inventory does not list is refused");
  // verification of the data parts
  foreach(int w in new[]{1,4}){var ev=Make(fx,w);ev.VerifyArchives();Check(ev.VerifyWorkers==Math.Min(w,fx.P.archives.Length)&&ev.VerifySeconds>=0,"verifying the data parts passes ("+w+" worker"+(w>1?"s":"")+")");}
  var dmg=Build(Path.Combine(root,"dmg"),3,60,3000,1,300000);string victim=Path.Combine(dmg.Folder,dmg.P.archives[1].name);
  using(var fs=new FileStream(victim,FileMode.Open,FileAccess.ReadWrite)){fs.Seek(fs.Length/2,SeekOrigin.Begin);int b=fs.ReadByte();fs.Seek(-1,SeekOrigin.Current);fs.WriteByte((byte)(b^1));}
  foreach(int w in new[]{1,4}){string m=Msg(()=>Make(dmg,w).VerifyArchives());Check(m!=null&&m.StartsWith("Missing or damaged data part: "+dmg.P.archives[1].name),"a damaged data part is rejected by name before anything is changed ("+w+" worker"+(w>1?"s":"")+")");}
  File.Delete(Path.Combine(dmg.Folder,dmg.P.archives[2].name));
  Check((Msg(()=>Make(dmg,4).VerifyArchives())??"").StartsWith("Missing or damaged data part: "),"a missing data part is rejected");
  Console.WriteLine("Speed checks passed: "+count);
  if(args.Length>1)Benchmark(Path.Combine(root,"bench"),Int32.Parse(args[1]));
 }
 // Timings on a synthetic package; printed, never asserted.
 static void Benchmark(string dir,int megabytes) {
  Console.WriteLine("BENCH building a "+megabytes+" MB synthetic package (many small files, some large ones)...");
  int bigFiles=Math.Max(1,(int)(megabytes*0.75/4/70)),smallFiles=Math.Max(200,(int)(megabytes*0.25*1048576/24000/4));
  var sw=Stopwatch.StartNew();var fx=Build(dir,4,smallFiles,24000,bigFiles,70*1024*1024);
  Console.WriteLine("BENCH package ready: "+fx.Files+" files, "+(fx.Bytes/1048576)+" MB of data in "+fx.P.archives.Sum(a=>a.size)/1048576+" MB of parts, "+Engine.Secs(sw.Elapsed.TotalSeconds)+" to build; drive: "+SpeedSupport.Describe(dir)+"; logical cores: "+Environment.ProcessorCount);
  foreach(int w in new[]{1,2,4}) {
   var e=Make(fx,w);e.VerifyArchives();Console.WriteLine("BENCH verify parts, "+w+" at a time: "+Engine.Secs(e.VerifySeconds));
  }
  string forced=Environment.GetEnvironmentVariable("FNVMF_BENCH_WORKERS");int[] list=String.IsNullOrEmpty(forced)?new[]{1,2,4,1,4}:forced.Split(',').Select(s=>Int32.Parse(s)).ToArray();
  foreach(int w in list) {
   string run=Path.Combine(dir,"run"+w+"-"+Guid.NewGuid().ToString("N").Substring(0,6));var e=Make(fx,w);string dest=Extract(e,run);
   Console.WriteLine("BENCH extract, "+w+" at a time: "+Engine.Secs(e.ExtractSeconds)+" ("+(e.ExtractedBytes/1048576.0/Math.Max(0.001,e.ExtractSeconds)).ToString("0")+" MB/s)");
   UninstallSupport.ReuseMargin=TimeSpan.Zero;var c=Stopwatch.StartNew();UninstallSupport.Capture(e,dest);double reuse=c.Elapsed.TotalSeconds;
   e.Extracted.Clear();c.Restart();UninstallSupport.Capture(e,dest);double full=c.Elapsed.TotalSeconds;UninstallSupport.ReuseMargin=TimeSpan.FromSeconds(3);
   Console.WriteLine("BENCH receipt: "+Engine.Secs(reuse)+" with reused hashes, "+Engine.Secs(full)+" hashing every file again");
   Directory.Delete(run,true);
  }
 }
}
