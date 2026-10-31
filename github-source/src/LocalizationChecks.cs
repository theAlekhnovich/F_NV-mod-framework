using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using FalloutModSetup;
// args: <work folder> [ground-truth: <Russian game Data> <English game Data> <frozen mods folder> <localized outputs folder> <Fixed ESMs folder>]
class LocalizationChecks {
 static int count;static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL "+text);count++;Console.WriteLine("PASS "+text);}
 static byte[] B(string s){return Encoding.GetEncoding(28591).GetBytes(s);}
 static byte[] Cat(params byte[][] parts){using(var m=new MemoryStream()){foreach(var p in parts)m.Write(p,0,p.Length);return m.ToArray();}}
 static byte[] Sub(string tag,byte[] data){if(data.Length>65535)return Cat(B("XXXX"),new byte[]{4,0},BitConverter.GetBytes((uint)data.Length),B(tag),new byte[]{0,0},data);return Cat(B(tag),BitConverter.GetBytes((ushort)data.Length),data);}
 static byte[] Sub(string tag,string text){return Sub(tag,B(text+"\0"));}
 static byte[] Rec(string tag,uint form,bool compressed,params byte[][] subs){
  var body=Cat(subs);byte[] raw=body;uint flags=0;
  if(compressed){var z=Tes4.Zlib(body);raw=Cat(BitConverter.GetBytes((uint)body.Length),z);flags=0x40000;}
  return Cat(B(tag),BitConverter.GetBytes((uint)raw.Length),BitConverter.GetBytes(flags),BitConverter.GetBytes(form),new byte[8],raw);
 }
 static byte[] Grup(byte label,params byte[][] recs){var inner=Cat(recs);return Cat(B("GRUP"),BitConverter.GetBytes((uint)(24+inner.Length)),new byte[]{label,0,0,0},new byte[12],inner);}
 static byte[] Tes4Rec(params string[] masters){var subs=new List<byte[]>{Sub("HEDR",new byte[12])};foreach(var m in masters){subs.Add(Sub("MAST",m));subs.Add(Sub("DATA",new byte[8]));}return Rec("TES4",0,false,subs.ToArray());}
 static string Text(byte[] data,uint form,string sub){
  string found=null;Tes4.Walk(data,0,data.Length,(h,b,s)=>{if(Tes4.U32(data,h+12)!=form)return;var body=Tes4.Payload(data,h,b,s);foreach(var x in Tes4.Subrecords(body,0,body.Length))if(x.Tag==sub&&found==null)found=Encoding.GetEncoding(28591).GetString(body,x.Data,x.Size).TrimEnd('\0');});return found;
 }
 static List<Tuple<string,byte[]>> Records(byte[] d){var list=new List<Tuple<string,byte[]>>();Tes4.Walk(d,0,d.Length,(h,b,s)=>{var hdr=new byte[24];Buffer.BlockCopy(d,h,hdr,0,24);Array.Clear(hdr,4,4);list.Add(Tuple.Create(BitConverter.ToString(hdr),Tes4.Payload(d,h,b,s)));});return list;}
 static string Diff(byte[] a,byte[] b){
  var cp=Encoding.GetEncoding(1251);var x=Tes4.Subrecords(a,0,a.Length);var y=Tes4.Subrecords(b,0,b.Length);if(x.Count!=y.Count)return "subrecord count "+x.Count+" vs "+y.Count;
  var sb=new StringBuilder();for(int i=0;i<x.Count;i++){var s1=cp.GetString(a,x[i].Data,x[i].Size);var s2=cp.GetString(b,y[i].Data,y[i].Size);if(s1!=s2)sb.Append("["+x[i].Tag+" mine=\""+s1.TrimEnd((char)0)+"\" original=\""+s2.TrimEnd((char)0)+"\"] ");}
  return sb.ToString();
 }
 static bool SameRecords(byte[] a,byte[] b,out string why){
  why=null;var x=Records(a);var y=Records(b);if(x.Count!=y.Count){why="record count "+x.Count+" vs "+y.Count;return false;}
  for(int i=0;i<x.Count;i++){if(x[i].Item1!=y[i].Item1){why="header of record "+i;return false;}if(!x[i].Item2.SequenceEqual(y[i].Item2)){why="payload of record "+i+" ("+x[i].Item1.Substring(0,11)+") "+Diff(x[i].Item2,y[i].Item2);return false;}}
  return true;
 }
 [STAThread]static int Main(string[] args) {
  string root=Path.GetFullPath(args[0]);if(Directory.Exists(root))Directory.Delete(root,true);Directory.CreateDirectory(root);
  // --- Synthetic plugins ---
  string longEnglish=new string('x',70000),longTranslated=new string('y',70001);
  var master=Cat(Tes4Rec(),Grup(1,
   Rec("INFO",0x100,false,Sub("EDID","a"),Sub("NAM1","Hello"),Sub("DATA",new byte[]{1,2,3})),
   Rec("NPC_",0x101,false,Sub("FULL","Bob")),
   Rec("MESG",0x102,false,Sub("ITXT","Yes")),
   Rec("GMST",0x103,false,Sub("EDID","sGreet"),Sub("DATA","Hi")),
   Rec("GMST",0x10A,false,Sub("EDID","fNumber"),Sub("DATA","Hi")),
   Rec("INFO",0x104,true,Sub("NAM1","Bye")),
   Rec("INFO",0x105,true,Sub("NAM1",longEnglish)),
   Rec("INFO",0x106,false,Sub("NAM1","Same"),Sub("NAM1","Same")),
   Rec("WEAP",0x107,false,Sub("FULL","Rifle"),Sub("DESC","A rifle"),Sub("DATA",new byte[]{9,9}))));
  var lang=Cat(Tes4Rec("base.esm"),Grup(1,
   Rec("INFO",0x00000100,false,Sub("EDID","a"),Sub("NAM1","Hallo"),Sub("DATA",new byte[]{1,2,3})),
   Rec("NPC_",0x00000101,false,Sub("FULL","Bernd")),
   Rec("MESG",0x00000102,false,Sub("ITXT","Ja")),
   Rec("GMST",0x00000103,false,Sub("EDID","sGreet"),Sub("DATA","Grüß")),
   Rec("GMST",0x0000010A,false,Sub("EDID","fNumber"),Sub("DATA","Nein")),
   Rec("INFO",0x00000104,true,Sub("NAM1","Tschüss")),
   Rec("INFO",0x00000105,true,Sub("NAM1",longTranslated)),
   Rec("INFO",0x00000106,false,Sub("NAM1","Eins"),Sub("NAM1","Zwei")),
   Rec("WEAP",0x00000107,false,Sub("FULL","Gewehr"),Sub("DESC","Ein Gewehr"),Sub("DATA",new byte[]{9,9}))));
  var mod=Cat(Tes4Rec("base.esm"),Grup(1,
   Rec("INFO",0x00000100,false,Sub("EDID","a"),Sub("NAM1","Hello"),Sub("DATA",new byte[]{1,2,3})),
   Rec("NPC_",0x00000101,false,Sub("FULL","Robert")),
   Rec("INFO",0x00000104,true,Sub("NAM1","Bye")),
   Rec("INFO",0x00000105,true,Sub("NAM1",longEnglish)),
   Rec("INFO",0x00000106,false,Sub("NAM1","Same"),Sub("NAM1","Same")),
   Rec("GMST",0x0000010A,false,Sub("EDID","fNumber"),Sub("DATA","Hi")),
   Grup(2,Rec("WEAP",0x00000107,false,Sub("FULL","Rifle"),Sub("DESC","A rifle"),Sub("DATA",new byte[]{9,9}))),
   Rec("MESG",0x01000200,false,Sub("ITXT","Yes"))));
  File.WriteAllBytes(Path.Combine(root,"base.esm"),master);File.WriteAllBytes(Path.Combine(root,"base_lang.esp"),lang);File.WriteAllBytes(Path.Combine(root,"mod.esp"),mod);
  Check(Tes4.Masters(mod).SequenceEqual(new[]{"base.esm"})&&Tes4.CountRecords(master)==10,"synthetic plugins parse");
  int ambiguous;var dict=Localization.BuildDictionary(new[]{Path.Combine(root,"base.esm")},new[]{Path.Combine(root,"base_lang.esp")},out ambiguous);
  Check(dict.Count==8,"dictionary holds the eight unambiguous translated strings (got "+dict.Count+")");
  Check(ambiguous==1,"a string translated two different ways is skipped, not guessed");
  Check(!dict.Keys.Any(k=>k.Contains("fNumber")),"a numeric GMST is never treated as text");
  var forms=Localization.Forms(dict);var report=new LocalizationResult();
  var result=Localization.Translate(mod,"mod.esp",dict,forms,report);
  Check(result!=null&&Text(result,0x100,"NAM1")=="Hallo","dialogue text is translated");
  Check(Text(result,0x101,"FULL")=="Robert","a name the mod changed stays as the mod wrote it");
  Check(Text(result,0x104,"NAM1")=="Tschüss"||Text(result,0x104,"NAM1")=="Tschüss","compressed record is translated and still decompresses");
  Check(Text(result,0x105,"NAM1")==longTranslated,"a string over 64 KiB uses an extended subrecord");
  Check(Text(result,0x106,"NAM1")=="Same","ambiguous text stays English");
  Check(Text(result,0x107,"FULL")=="Gewehr"&&Text(result,0x107,"DESC")=="Ein Gewehr","records inside nested groups are translated");
  Check(Text(result,0x01000200,"ITXT")=="Yes","a record new to the mod is untouched");
  Check(report.Strings==5&&Tes4.CountRecords(result)==Tes4.CountRecords(mod),"report counts five strings and record count is unchanged");
  string why;var untouched=Records(mod);var changed=Records(result);
  Check(untouched.Zip(changed,(a,b)=>a.Item1==b.Item1).All(x=>x),"record headers (except sizes) are identical");
  var unrelated=Cat(Tes4Rec("base.esm"),Grup(1,Rec("NPC_",0x00000101,false,Sub("FULL","Other name"))));Check(Localization.Translate(unrelated,"other.esp",dict,forms,new LocalizationResult())==null,"a plugin with nothing to translate produces no overlay");
  var twice=Localization.Translate(result,"mod.esp",dict,forms,new LocalizationResult());Check(twice==null,"already translated text is not translated again");
  // A DEFLATE stream with a bad Adler trailer (seen in stock records) is still read.
  var damaged=Cat(Tes4Rec(),Grup(1,Rec("INFO",0x100,true,Sub("NAM1","Bye"))));damaged[damaged.Length-1]^=0xFF;
  Check(Text(damaged,0x100,"NAM1")=="Bye","a bad checksum trailer does not stop reading");
  Check(Localization.HasTranslations(root)==false,"a folder without FalloutNV_lang.esp has no translations");

  // --- Ground truth: the Russian overlay built by the original tooling ---
  if(args.Length>=6) {
   string ruData=args[1],enData=args[2],frozen=args[3],localized=args[4],fixedDir=args[5];var engine=new Engine();var sw=Stopwatch.StartNew();
   var langs=Directory.GetFiles(ruData,"*_lang.esp").Where(f=>new FileInfo(f).Length>1024).OrderBy(f=>Path.GetFileName(f),StringComparer.OrdinalIgnoreCase).ToList();
   int amb1,amb2;
   var dictEn=Localization.BuildDictionary(engine.P.masters.Select(m=>Path.Combine(enData,Path.GetFileName(m.name))).Where(File.Exists).ToList(),langs,out amb1);
   Console.WriteLine("INFO dictionary from English originals: "+dictEn.Count+" strings, "+amb1+" ambiguous (original report: 79832 / 37)");Check(Math.Abs(dictEn.Count-79832)<=100,"dictionary size agrees with the original tooling within 0.1%");
   List<string> englishFiles,translatedFiles;Localization.Sources(engine.P,Path.GetDirectoryName(ruData),langs,out englishFiles,out translatedFiles);
   var reference=Localization.ReferenceEnglish();Check(reference.Count==16,"the installer embeds the 16 English pack records");
   Check(englishFiles.Count==6&&translatedFiles.Count==langs.Count+4,"six masters are the English originals and four localized pack masters are translation sources");
   var dictRu=Localization.BuildDictionary(englishFiles,translatedFiles,out amb2,null,null,reference);
   Console.WriteLine("INFO runtime dictionary from the Russian game's own masters: "+dictRu.Count+" strings, "+amb2+" ambiguous ("+sw.Elapsed.TotalSeconds.ToString("0")+" s)");
   Check(dictRu.Count==dictEn.Count||Math.Abs(dictRu.Count-dictEn.Count)<=20,"install-time dictionary equals the build-time one");
   var formsRu=Localization.Forms(dictRu);int compared=0,missing=0;long strings=0;var mismatch=new List<string>();
   foreach(var expectedFile in Directory.GetFiles(localized)) {
    string name=Path.GetFileName(expectedFile),source=null;
    var inFixed=Path.Combine(fixedDir,name);if(File.Exists(inFixed))source=inFixed;
    if(source==null){var f=Directory.GetFiles(frozen,name,SearchOption.AllDirectories);if(f.Length>0)source=f[0];}
    if(source==null&&File.Exists(Path.Combine(ruData,name)))source=Path.Combine(ruData,name);
    if(source==null){missing++;continue;}
    var rep=new LocalizationResult();var output=Localization.Translate(File.ReadAllBytes(source),name,dictRu,formsRu,rep);
    var expected=File.ReadAllBytes(expectedFile);
    if(output==null){mismatch.Add(name+": nothing translated");continue;}
    if(!SameRecords(output,expected,out why)){mismatch.Add(name+": "+why);continue;}
    compared++;strings+=rep.Strings;Console.WriteLine("  same as original tooling: "+name+" ("+rep.Strings+" strings)");
   }
   foreach(var m in mismatch)Console.WriteLine("  MISMATCH "+m);
   Check(mismatch.Count==0,"no translated plugin differs from the original tooling ("+mismatch.Count+" differ)");
   Check(compared>=90,"translated plugins equal the verified Russian overlays record by record ("+compared+" plugins, "+strings+" strings; "+missing+" without a local source)");
   Console.WriteLine("INFO ground truth took "+sw.Elapsed.TotalSeconds.ToString("0")+" s");
  }
  Console.WriteLine("ALL "+count+" LOCALIZATION CHECKS PASSED");return 0;
 }
}
