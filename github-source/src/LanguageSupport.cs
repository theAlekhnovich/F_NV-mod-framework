using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace FalloutModSetup {
public class LanguagePatch {public string sourceKind,source,sourceHash,target,targetHash,patch;public long size;public int strings;}
public class LanguageItem {public string id;public MasterItem[] masters;public FileItem[] fontFiles,sourceTranslations;public LanguagePatch[] patches;public LanguagePatch[] retiredPatches;}
public static class LanguageSupport {
 static LanguageItem Selected(Package p,Options o){return (p.languages??new LanguageItem[0]).FirstOrDefault(x=>x.id==o.Language);}
 public static void Resolve(Package p,Options o){
  bool auto=o.Language=="Auto";
  if(o.Language=="Auto"){
   o.Language="English";
   foreach(var lang in p.languages??new LanguageItem[0]) {
    foreach(var m in lang.masters) {
     var baseline=p.masters.FirstOrDefault(x=>x.name==m.name);
     string path=Engine.SafeJoin(o.Game,m.name);
     if(baseline!=null&&baseline.hash!=m.hash&&File.Exists(path)&&Engine.Hash(path)==m.hash){o.Language=lang.id;break;}
    }
    if(o.Language!="English")break;
   }
  }
  // Any other release that ships its translations in *_lang.esp files (Steam/GOG/Epic German, French, Polish, ...).
  if(auto&&o.Language=="English"&&Localization.HasTranslations(o.Game))o.Language="Localized";
  if(o.Language!="English"&&o.Language!="Localized"&&Selected(p,o)==null)throw new Exception("Choose English, Other languages or the supported Russian language package.");
 }
 // null = this master may differ from the English release (a localized DLC pack); only the masters rebuilt into Fixed ESMs must match exactly.
 public static string MasterHash(Package p,Options o,MasterItem m){
  if(o.Language=="Localized")return p.patches.Any(x=>x.source==m.name)?m.hash:null;
  var lang=Selected(p,o);return lang==null?m.hash:lang.masters.Single(x=>x.name==m.name).hash;
 }
 public static void Preflight(Package p,Options o){
  if(o.Language=="Localized"&&!Localization.HasTranslations(o.Game))throw new Exception("Other language needs the game's translation file Data\\FalloutNV_lang.esp (the installed language pack). Install the language in your store, or choose English. No game files have been changed.");
  var lang=Selected(p,o);if(lang==null)return;
  foreach(var f in lang.fontFiles.Concat(lang.sourceTranslations)){
   string source=Engine.SafeJoin(o.Game,f.path);
   if(!File.Exists(source)||new FileInfo(source).Length!=f.size||Engine.Hash(source)!=f.hash)throw new Exception("The supported Russian installation needs its matching translation/font file: "+f.path+". Restore the original Russian language files before installing. No game files have been changed.");
  }
 }
 public static void Apply(Engine engine,Options o,Action<string> log){
  if(o.Language=="Localized"){Localization.Apply(engine,o,log);return;}
  var lang=Selected(engine.P,o);if(lang==null)return;
  string support=Engine.SafeJoin(o.Destination,"_setup"),scratch=Engine.SafeJoin(support,"language-work");Directory.CreateDirectory(scratch);
  int index=0;
  foreach(var p in lang.patches){
   engine.Cancel.ThrowIfCancellationRequested();engine.Progress("Applying Russian text: "+Path.GetFileName(p.target)+" ("+(++index)+"/"+lang.patches.Length+")",89);
   string source=Engine.SafeJoin(p.sourceKind=="game"?o.Game:o.Destination,p.source);
   if(p.sourceKind!="game"&&p.sourceKind!="app")throw new Exception("Invalid language source.");
   string localSource=Path.Combine(scratch,"source.esm"),delta=Path.Combine(scratch,"text.vcdiff"),result=Path.Combine(scratch,"result.esm");
   File.Copy(source,localSource,true);if(Engine.Hash(localSource)!=p.sourceHash)throw new Exception("The localization source changed: "+p.source);
   File.Copy(Engine.SafeJoin(support,p.patch),delta,true);
   Engine.Run(Engine.SafeJoin(support,"tools/xdelta3decode.exe"),"-f -d -s source.esm text.vcdiff result.esm",scratch,600,false);
   if(new FileInfo(result).Length!=p.size||Engine.Hash(result)!=p.targetHash)throw new Exception("Russian text verification failed: "+p.target);
   string target=Engine.SafeJoin(o.Destination,p.target);Engine.EnsureParent(target);File.Move(result,target);
  }
  string mod=Engine.SafeJoin(o.Destination,"mods/Russian text compatibility");Directory.CreateDirectory(mod);
  foreach(var f in lang.fontFiles){
   engine.Cancel.ThrowIfCancellationRequested();string source=Engine.SafeJoin(o.Game,f.path),target=Engine.SafeJoin(mod,f.path.Substring("Data/".Length));
   Engine.EnsureParent(target);File.Copy(source,target,false);if(Engine.Hash(target)!=f.hash)throw new Exception("Russian font verification failed: "+f.path);
  }
  File.WriteAllText(Path.Combine(mod,"meta.ini"),"[General]\r\nnotes=Verified Russian text overlay. Gameplay fields and plugin order are preserved. Mod-added or changed text without a stock translation remains English.\r\nversion=1.2\r\n",Engine.Utf8);
  string list=Engine.SafeJoin(o.Destination,"profiles/Default/modlist.txt");var lines=File.ReadAllLines(list,Engine.Utf8).ToList();int at=lines.FindIndex(x=>!x.StartsWith("#"));if(at<0)at=lines.Count;lines.Insert(at,"+Russian text compatibility");File.WriteAllLines(list,lines,Engine.Utf8);
  log("Russian compatibility applied: "+lang.patches.Length+" text-only plugin overlays and "+lang.fontFiles.Length+" font files. Plugin order retained.");
 }
}
}
