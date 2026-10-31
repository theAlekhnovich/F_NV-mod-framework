using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
namespace FalloutModSetup {
public class Tes4Sub { public string Tag; public int Start,Data,Size,End; }
public class EnglishRecord { public string Tag; public Dictionary<string,List<byte[]>> Fields=new Dictionary<string,List<byte[]>>(); }
public class LocalizationResult { public string Plugin; public int Strings; public Dictionary<string,int> Fields=new Dictionary<string,int>(); }

// Reads and rewrites TES4-family plugins. Only known string fields change; every other byte is preserved.
public static class Tes4 {
    public static readonly Encoding Latin1=Encoding.GetEncoding(28591);
    static readonly Dictionary<string,string[]> TextFields=new Dictionary<string,string[]>{
        {"INFO",new[]{"NAM1","RNAM"}},{"QUST",new[]{"CNAM","NNAM"}},{"MESG",new[]{"ITXT"}},{"TERM",new[]{"ITXT","RNAM"}},{"FACT",new[]{"MNAM","FNAM"}},{"REGN",new[]{"RDMP"}}};
    public static uint U32(byte[] b,int o){return BitConverter.ToUInt32(b,o);}
    public static string Tag(byte[] b,int o){return Latin1.GetString(b,o,4);}
    public static List<Tes4Sub> Subrecords(byte[] d,int start,int end) {
        var list=new List<Tes4Sub>();int i=start;
        while(i<end) {
            int s=i;if(i+6>end)throw new InvalidDataException("Truncated subrecord");
            string tag=Tag(d,i);long size=BitConverter.ToUInt16(d,i+4);i+=6;
            if(tag=="XXXX") {
                if(size!=4||i+10>end)throw new InvalidDataException("Invalid XXXX subrecord");
                size=U32(d,i);i+=4;tag=Tag(d,i);i+=6;
            }
            if(i+size>end)throw new InvalidDataException("Subrecord exceeds its record");
            list.Add(new Tes4Sub{Tag=tag,Start=s,Data=i,Size=(int)size,End=i+(int)size});i+=(int)size;
        }
        return list;
    }
    // Body of a record, decompressed when flagged. Some stock records carry a bad Adler trailer, so only the deflate stream and declared length are trusted.
    public static byte[] Payload(byte[] file,int header,int body,int size) {
        if((U32(file,header+8)&0x40000)==0){var copy=new byte[size];Buffer.BlockCopy(file,body,copy,0,size);return copy;}
        if(size<7)throw new InvalidDataException("Compressed record is too short");
        int expected=(int)U32(file,body);var result=new byte[expected];
        using(var ms=new MemoryStream(file,body+6,size-6,false))using(var z=new DeflateStream(ms,CompressionMode.Decompress)) {
            int n=0,r;while(n<expected&&(r=z.Read(result,n,expected-n))>0)n+=r;
            if(n!=expected)throw new InvalidDataException("Compressed record has the wrong length");
        }
        return result;
    }
    public static byte[] Zlib(byte[] body) {
        using(var ms=new MemoryStream()) {
            ms.WriteByte(0x78);ms.WriteByte(0x9C);
            using(var z=new DeflateStream(ms,CompressionMode.Compress,true))z.Write(body,0,body.Length);
            uint a=1,b=0;foreach(byte x in body){a=(a+x)%65521;b=(b+a)%65521;}
            uint sum=(b<<16)|a;ms.WriteByte((byte)(sum>>24));ms.WriteByte((byte)(sum>>16));ms.WriteByte((byte)(sum>>8));ms.WriteByte((byte)sum);
            return ms.ToArray();
        }
    }
    public static void Walk(byte[] d,int start,int end,Action<int,int,int> visit) {
        int i=start;
        while(i<end) {
            if(i+24>end)throw new InvalidDataException("Truncated record");
            int size=(int)U32(d,i+4);
            if(Tag(d,i)=="GRUP") {
                if(size<24||i+size>end)throw new InvalidDataException("Invalid group");
                Walk(d,i+24,i+size,visit);i+=size;
            } else {
                if((long)i+24+size>end)throw new InvalidDataException("Invalid record length");
                visit(i,i+24,size);i+=24+size;
            }
        }
    }
    public static int CountRecords(byte[] d){int n=0;Walk(d,0,d.Length,(h,b,s)=>n++);return n;}
    public static List<string> Masters(byte[] d) {
        if(d.Length<24||Tag(d,0)!="TES4")throw new InvalidDataException("Not a plugin file");
        int size=(int)U32(d,4);var body=Payload(d,0,24,size);
        return Subrecords(body,0,body.Length).Where(s=>s.Tag=="MAST").Select(s=>Latin1.GetString(body,s.Data,s.Size).TrimEnd('\0').ToLowerInvariant()).ToList();
    }
    // Stock records outside the master list are never translated.
    public static string Key(byte[] d,int header,List<string> masters,string name) {
        uint fid=U32(d,header+12),index=fid>>24;
        if(index>masters.Count)return null;
        return (index<masters.Count?masters[(int)index]:name.ToLowerInvariant())+":"+(fid&0xffffff).ToString("x6");
    }
    public static IEnumerable<Tes4Sub> Fields(string record,byte[] body,List<Tes4Sub> subs) {
        if(record=="TES4")yield break;
        string[] extra;TextFields.TryGetValue(record,out extra);
        int edid=-1;var first=subs.FirstOrDefault(s=>s.Tag=="EDID");if(first!=null&&first.Size>0)edid=body[first.Data];
        foreach(var s in subs) {
            bool eligible=s.Tag=="FULL"||s.Tag=="DESC"||(extra!=null&&extra.Contains(s.Tag))||(record=="GMST"&&s.Tag=="DATA"&&edid=='s');
            if(!eligible||s.Size<1||body[s.End-1]!=0)continue;
            bool inner=false;for(int i=s.Data;i<s.End-1;i++)if(body[i]==0){inner=true;break;}
            if(!inner)yield return s;
        }
    }
    public static string Text(byte[] body,Tes4Sub s){return Latin1.GetString(body,s.Data,s.Size);}
}

// Any language: translations come from the game's own *_lang.esp files, matched to the English text in its masters.
public static class Localization {
    public const string ModName="Localized text compatibility";
    public static string FieldKey(string form,string tag,string english){return form+"|"+tag+"|"+english;}
    static Dictionary<string,List<byte[]>> Collect(byte[] d,int header,int body,int size,string record) {
        var payload=Tes4.Payload(d,header,body,size);var subs=Tes4.Subrecords(payload,0,payload.Length);var result=new Dictionary<string,List<byte[]>>();
        foreach(var s in Tes4.Fields(record,payload,subs)){List<byte[]> list;if(!result.TryGetValue(s.Tag,out list))result[s.Tag]=list=new List<byte[]>();var copy=new byte[s.Size];Buffer.BlockCopy(payload,s.Data,copy,0,s.Size);list.Add(copy);}
        return result;
    }
    static bool Same(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
    // English text of every record that has translatable fields, keyed like the dictionary. Later files replace earlier ones.
    public static Dictionary<string,EnglishRecord> ReadEnglish(IEnumerable<string> files,HashSet<string> only=null,Action<string> say=null,Func<bool> stop=null) {
        say=say??delegate{};stop=stop??(()=>false);var english=new Dictionary<string,EnglishRecord>();
        foreach(var file in files) {
            if(stop())throw new OperationCanceledException();
            say("Reading original text: "+Path.GetFileName(file));
            var data=File.ReadAllBytes(file);var ms=Tes4.Masters(data);string name=Path.GetFileName(file);
            Tes4.Walk(data,0,data.Length,(h,b,s)=>{
                string key=Tes4.Key(data,h,ms,name);if(key==null||(only!=null&&!only.Contains(key)))return;
                string record=Tes4.Tag(data,h);if(record=="TES4")return;var fields=Collect(data,h,b,s,record);
                if(fields.Count>0)english[key]=new EnglishRecord{Tag=record,Fields=fields};
            });
        }
        return english;
    }
    // The English DLC pack masters ship inside the installer, because a localized game's own pack masters no longer hold the English text.
    public static Dictionary<string,EnglishRecord> ReferenceEnglish() {
        try {
            using(var s=typeof(Engine).Assembly.GetManifestResourceStream("english-packs.json")) {
                if(s==null)return new Dictionary<string,EnglishRecord>();
                string json;using(var r=new StreamReader(s,Engine.Utf8))json=r.ReadToEnd();
                return ReferenceFromJson(json);
            }
        }catch{return new Dictionary<string,EnglishRecord>();}
    }
    public static string ReferenceToJson(Dictionary<string,EnglishRecord> records) {
        return Engine.Json().Serialize(records.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new Dictionary<string,object>{{"k",x.Key},{"t",x.Value.Tag},{"f",x.Value.Fields.ToDictionary(f=>f.Key,f=>f.Value.Select(v=>Convert.ToBase64String(v)).ToArray())}}).ToArray());
    }
    public static Dictionary<string,EnglishRecord> ReferenceFromJson(string json) {
        var result=new Dictionary<string,EnglishRecord>();
        foreach(var item in Engine.Json().Deserialize<Dictionary<string,object>[]>(json)) {
            var rec=new EnglishRecord{Tag=(string)item["t"]};
            foreach(var f in (Dictionary<string,object>)item["f"])rec.Fields[f.Key]=((System.Collections.ArrayList)f.Value).Cast<string>().Select(v=>Convert.FromBase64String(v)).ToList();
            result[(string)item["k"]]=rec;
        }
        return result;
    }
    // Maps (record, field, English text) to the translated text. Ambiguous English strings are skipped, never guessed.
    // translatedFiles: the *_lang.esp files plus any master that already ships in the target language.
    public static Dictionary<string,byte[]> BuildDictionary(IEnumerable<string> englishFiles,IEnumerable<string> translatedFiles,out int ambiguousSkipped,Action<string> say=null,Func<bool> stop=null,Dictionary<string,EnglishRecord> reference=null) {
        say=say??delegate{};stop=stop??(()=>false);
        var translated=new List<List<Tuple<string,string,Dictionary<string,List<byte[]>>>>>();var forms=new HashSet<string>();
        foreach(var file in translatedFiles) {
            if(stop())throw new OperationCanceledException();
            say("Reading translations: "+Path.GetFileName(file));
            var data=File.ReadAllBytes(file);var ms=Tes4.Masters(data);string name=Path.GetFileName(file);var list=new List<Tuple<string,string,Dictionary<string,List<byte[]>>>>();
            Tes4.Walk(data,0,data.Length,(h,b,s)=>{
                string key=Tes4.Key(data,h,ms,name);if(key==null)return;string record=Tes4.Tag(data,h);if(record=="TES4")return;
                var fields=Collect(data,h,b,s,record);if(fields.Count==0)return;
                list.Add(Tuple.Create(key,record,fields));forms.Add(key);
            });
            translated.Add(list);
        }
        var english=ReadEnglish(englishFiles,forms,say,stop);
        if(reference!=null)foreach(var r in reference)if(forms.Contains(r.Key))english[r.Key]=r.Value;
        var result=new Dictionary<string,byte[]>();var ambiguous=new HashSet<string>();
        foreach(var list in translated) {
            foreach(var rec in list) {
                EnglishRecord original;if(!english.TryGetValue(rec.Item1,out original)||original.Tag!=rec.Item2)continue;
                foreach(var f in rec.Item3) {
                    List<byte[]> old;if(!original.Fields.TryGetValue(f.Key,out old))old=new List<byte[]>();
                    if(old.Count!=f.Value.Count)continue;
                    for(int i=0;i<old.Count;i++) {
                        if(Same(old[i],f.Value[i]))continue;
                        string k=FieldKey(rec.Item1,f.Key,Tes4.Latin1.GetString(old[i]));byte[] existing;
                        if(result.TryGetValue(k,out existing)&&!Same(existing,f.Value[i]))ambiguous.Add(k);else result[k]=f.Value[i];
                    }
                }
            }
            foreach(var k in ambiguous)result.Remove(k);
        }
        ambiguousSkipped=ambiguous.Count;return result;
    }    public static HashSet<string> Forms(Dictionary<string,byte[]> dictionary){var set=new HashSet<string>();foreach(var k in dictionary.Keys)set.Add(k.Substring(0,k.IndexOf('|')));return set;}

    // Returns the translated plugin bytes, or null when no field matched.
    public static byte[] Translate(byte[] data,string name,Dictionary<string,byte[]> dictionary,HashSet<string> forms,LocalizationResult report) {
        var ms=Tes4.Masters(data);int total=0;var output=new MemoryStream(data.Length+(data.Length>>4));
        Action<int,int> block=null;
        block=delegate(int start,int end) {
            int i=start;
            while(i<end) {
                int size=(int)Tes4.U32(data,i+4);
                if(Tes4.Tag(data,i)=="GRUP") {
                    long at=output.Position;output.Write(data,i,24);block(i+24,i+size);
                    long length=output.Position-at;output.Position=at+4;output.Write(BitConverter.GetBytes((uint)length),0,4);output.Position=at+length;i+=size;continue;
                }
                int bodyAt=i+24;string key=Tes4.Key(data,i,ms,name);byte[] replaced=null;
                if(key!=null&&forms.Contains(key)) {
                    string record=Tes4.Tag(data,i);var body=Tes4.Payload(data,i,bodyAt,size);var subs=Tes4.Subrecords(body,0,body.Length);
                    var changes=new SortedDictionary<int,Tuple<int,byte[],byte[]>>();
                    foreach(var s in Tes4.Fields(record,body,subs)) {
                        byte[] now;
                        if(!dictionary.TryGetValue(FieldKey(key,s.Tag,Tes4.Text(body,s)),out now))continue;
                        byte[] old=new byte[s.Size];Buffer.BlockCopy(body,s.Data,old,0,s.Size);if(Same(old,now))continue;
                        byte[] encoded;
                        if(now.Length>65535) {
                            using(var e=new MemoryStream()){e.Write(new byte[]{(byte)'X',(byte)'X',(byte)'X',(byte)'X',4,0},0,6);e.Write(BitConverter.GetBytes((uint)now.Length),0,4);e.Write(Tes4.Latin1.GetBytes(s.Tag),0,4);e.Write(new byte[]{0,0},0,2);e.Write(now,0,now.Length);encoded=e.ToArray();}
                        } else {
                            encoded=new byte[6+now.Length];Buffer.BlockCopy(Tes4.Latin1.GetBytes(s.Tag),0,encoded,0,4);encoded[4]=(byte)(now.Length&255);encoded[5]=(byte)(now.Length>>8);Buffer.BlockCopy(now,0,encoded,6,now.Length);
                        }
                        changes[s.Start]=Tuple.Create(s.End,encoded,now);int c;report.Fields.TryGetValue(s.Tag,out c);report.Fields[s.Tag]=c+1;total++;report.Strings++;
                    }
                    if(changes.Count>0) {
                        using(var nb=new MemoryStream()) {
                            int cursor=0;foreach(var c in changes){nb.Write(body,cursor,c.Key-cursor);nb.Write(c.Value.Item2,0,c.Value.Item2.Length);cursor=c.Value.Item1;}
                            nb.Write(body,cursor,body.Length-cursor);var newBody=nb.ToArray();
                            Verify(body,subs,newBody,changes);
                            byte[] raw=newBody;if((Tes4.U32(data,i+8)&0x40000)!=0){var z=Tes4.Zlib(newBody);raw=new byte[4+z.Length];Buffer.BlockCopy(BitConverter.GetBytes((uint)newBody.Length),0,raw,0,4);Buffer.BlockCopy(z,0,raw,4,z.Length);}
                            var header=new byte[24];Buffer.BlockCopy(data,i,header,0,24);Buffer.BlockCopy(BitConverter.GetBytes((uint)raw.Length),0,header,4,4);
                            output.Write(header,0,24);output.Write(raw,0,raw.Length);replaced=raw;
                        }
                    }
                }
                if(replaced==null)output.Write(data,i,24+size);
                i+=24+size;
            }
        };
        block(0,data.Length);
        if(total==0)return null;
        var result=output.ToArray();
        if(Tes4.CountRecords(result)!=Tes4.CountRecords(data))throw new InvalidDataException("Translated plugin changed its record structure: "+name);
        return result;
    }
    // Same subrecord order and bytes everywhere except the approved string replacements.
    static void Verify(byte[] body,List<Tes4Sub> oldSubs,byte[] newBody,SortedDictionary<int,Tuple<int,byte[],byte[]>> changes) {
        var newSubs=Tes4.Subrecords(newBody,0,newBody.Length);
        if(newSubs.Count!=oldSubs.Count)throw new InvalidDataException("A translated record changed its subrecord count");
        for(int i=0;i<oldSubs.Count;i++) {
            var a=oldSubs[i];var z=newSubs[i];if(a.Tag!=z.Tag)throw new InvalidDataException("A translated record changed its subrecord order");
            Tuple<int,byte[],byte[]> change;
            if(changes.TryGetValue(a.Start,out change)) {
                var now=change.Item3;if(z.Size!=now.Length)throw new InvalidDataException("Translated text length mismatch");
                for(int k=0;k<now.Length;k++)if(newBody[z.Data+k]!=now[k])throw new InvalidDataException("Translated text mismatch");
            } else {
                if(a.Size!=z.Size)throw new InvalidDataException("A non-text subrecord changed size");
                for(int k=0;k<a.Size;k++)if(body[a.Data+k]!=newBody[z.Data+k])throw new InvalidDataException("A non-text subrecord changed");
            }
        }
    }

    // Masters identical to the English release are the originals. A master that differs already ships in the target language: its text is a translation source.
    public static void Sources(Package p,string game,List<string> langs,out List<string> english,out List<string> translated) {
        english=new List<string>();translated=new List<string>(langs);
        foreach(var m in p.masters) {
            string path=Engine.SafeJoin(game,m.name);if(!File.Exists(path))continue;
            if(Engine.Hash(path)==m.hash)english.Add(path);else translated.Add(path);
        }
    }
    public static bool HasTranslations(string game) {
        try{string p=Path.Combine(game,"Data","FalloutNV_lang.esp");return File.Exists(p)&&new FileInfo(p).Length>1024*1024;}catch{return false;}
    }
    public static List<string> LangFiles(string game){return Directory.GetFiles(Path.Combine(game,"Data"),"*_lang.esp").Where(f=>new FileInfo(f).Length>1024).OrderBy(f=>Path.GetFileName(f),StringComparer.OrdinalIgnoreCase).ToList();}

    // Translates every plugin of the installed profile in place of the English text, into one high-priority overlay mod.
    // only != null (an update): translate just those plugins into the existing overlay mod; fonts, notes and the mod entry are already there.
    public static void Apply(Engine engine,Options o,Action<string> log,HashSet<string> only=null) {
        string dest=o.Destination,game=o.Game,mod=Engine.SafeJoin(dest,"mods/"+ModName),support=Engine.SafeJoin(dest,"_setup");
        var langs=LangFiles(game);if(langs.Count==0)throw new Exception("This game has no translation files (*_lang.esp) in its Data folder.");
        List<string> english,translatedFiles;Sources(engine.P,game,langs,out english,out translatedFiles);
        int ambiguous;var dictionary=BuildDictionary(english,translatedFiles,out ambiguous,s=>engine.Progress(s,88),()=>engine.Cancel.IsCancellationRequested,ReferenceEnglish());
        if(dictionary.Count==0){log("No translated text was found in the game's language files; the setup keeps English text.");return;}
        var forms=Forms(dictionary);
        string profile=Engine.SafeJoin(dest,"profiles/Default");
        var lines=File.ReadAllLines(Path.Combine(profile,"modlist.txt"),Engine.Utf8).ToList();
        var mods=lines.Where(x=>x.StartsWith("+")).Select(x=>Engine.SafeJoin(dest,"mods/"+x.Substring(1))).ToList();
        var order=File.ReadAllLines(Path.Combine(profile,"loadorder.txt"),Engine.Utf8).Where(x=>x.Length>0&&!x.StartsWith("#")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var reports=new List<LocalizationResult>();int index=0,done=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in order) {
            engine.Cancel.ThrowIfCancellationRequested();
            if(Path.GetFileName(name)!=name||name.EndsWith("_lang.esp",StringComparison.OrdinalIgnoreCase)||!seen.Add(name))continue;
            if(only!=null&&!only.Contains(name))continue;
            // The overlay mod itself sits in the list and holds older translated copies; the plugin's own mod is the source.
            string file=mods.Where(m=>!m.EndsWith("\\"+ModName,StringComparison.OrdinalIgnoreCase)||only==null).Select(m=>Engine.SafeJoin(m,name)).FirstOrDefault(File.Exists);
            if(file==null){string g=Engine.SafeJoin(game,"Data/"+name);if(File.Exists(g))file=g;}
            if(file==null)continue;
            engine.Progress("Translating game text: "+name+" ("+(++index)+"/"+order.Count+")",89);
            var report=new LocalizationResult{Plugin=name};var result=Translate(File.ReadAllBytes(file),name,dictionary,forms,report);
            if(result==null)continue;
            string target=Engine.SafeJoin(mod,name);Engine.EnsureParent(target);File.WriteAllBytes(target,result);reports.Add(report);done++;
        }
        if(only!=null){log("Localization refreshed for "+done+" updated plugins.");return;}
        int fonts=0;string fontSource=Path.Combine(game,"Data","textures","fonts");
        if(Directory.Exists(fontSource))foreach(var f in Directory.GetFiles(fontSource,"*",SearchOption.AllDirectories)) {
            string rel=f.Substring(Path.Combine(game,"Data").Length+1),target=Engine.SafeJoin(mod,rel);Engine.NoLinks(f);Engine.EnsureParent(target);File.Copy(f,target,false);
            if(Engine.Hash(target)!=Engine.Hash(f))throw new Exception("Font copy verification failed: "+rel);fonts++;
        }
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod,"meta.ini"),"[General]\r\nnotes=Game text translated from this game's own language files. Text added or changed by mods without a stock translation stays English. Gameplay fields and plugin order are preserved.\r\nversion=1.0\r\n",Engine.Utf8);
        var modlist=Path.Combine(profile,"modlist.txt");lines.RemoveAll(x=>x=="+"+ModName);int at=lines.FindIndex(x=>!x.StartsWith("#"));if(at<0)at=lines.Count;lines.Insert(at,"+"+ModName);File.WriteAllLines(modlist,lines,Engine.Utf8);
        File.WriteAllText(Path.Combine(support,"localization-report.json"),Engine.Json().Serialize(new{translations=dictionary.Count,ambiguousSkipped=ambiguous,languageFiles=langs.Select(Path.GetFileName).ToArray(),plugins=reports,fontFiles=fonts}),Engine.Utf8);
        log("Localization applied: "+dictionary.Count+" translated strings in "+done+" plugins"+(fonts>0?" and "+fonts+" font files":"")+" from "+langs.Count+" language files ("+ambiguous+" ambiguous strings skipped).");
    }
}
}
