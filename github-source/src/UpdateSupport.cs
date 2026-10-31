using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace FalloutModSetup {
// One edit of a profile list (modlist.txt, plugins.txt, loadorder.txt): drop lines, then put lines after an anchor. Anchors are tried in order,
// so a line the user already removed or moved does not block the others. result is the digest of the list the edit produces from the packaged one.
// addOnly (revision 8, optional mods): insert line only when no entry of that mod exists in the list (enabled or not), directly above the entry of the mod named by "above"
// (found by name, whatever its flag), else after the first anchor of "after"; when neither exists nothing is added. An existing entry is never moved, enabled or disabled.
public class PlaceItem { public string line; public string[] after; public bool addOnly; public string above; }
// One key of a profile INI (profiles\Default\<file>): was is the packaged value before this revision (null = the key was not packaged), value the new one.
// A fresh install sets it; an update sets it only while the installed value still equals was, so a value the user (or the wizard) changed is kept.
public class IniEdit { public string file, section, key, was, value; public int revision; }
public class ProfileEdit { public string file, result; public string[] remove; public PlaceItem[] place; public int revision; }   // revision 0 means 1

// One mod's place in the mod list, in Mod Organizer's own terms. MO2's pane lists the LOWEST priority first, modlist.txt stores the HIGHEST first, so
// "below in the pane" is "above in the file". after: directly below the first of these mods that exists; before: directly above it; sectionEnd: at the end of the
// section that this separator starts (directly above the next separator in the pane). revision: the package revision that introduced the move (0 means 1).
public class ModMove { public string mod, sectionEnd; public string[] after, before; public int revision; }

public static class ModlistMoves {
    static bool Same(string a,string b){return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
    static string Name(string line){return line.Length>0&&"+-*".IndexOf(line[0])>=0?line.Substring(1):line;}
    static bool Separator(string line){return !line.StartsWith("#")&&Name(line).EndsWith("_separator",StringComparison.OrdinalIgnoreCase);}
    static int Find(List<string> lines,string mod){return lines.FindIndex(l=>!l.StartsWith("#")&&Same(Name(l),mod));}
    // Moves only the named entries (enabled or not) and leaves every other line where it is. A move is skipped when its mod is not in the list, when none of its
    // anchors exists, or when the mod already sits there. Returns how many entries moved; haveRevision is what the setup already has (0 for a fresh install).
    public static int Apply(List<string> lines,IEnumerable<ModMove> moves,int haveRevision,Action<string> log=null) {
        int moved=0;
        foreach(var m in moves??new ModMove[0]) {
            if(haveRevision>=(m.revision<=0?1:m.revision))continue;
            int old=Find(lines,m.mod);
            if(old<0){if(log!=null)log("Mod list: \""+m.mod+"\" is not in the list, left alone.");continue;}
            string line=lines[old];lines.RemoveAt(old);
            int at=-1;
            foreach(var a in m.after??new string[0]){int i=Find(lines,a);if(i>=0){at=i;break;}}
            if(at<0)foreach(var b in m.before??new string[0]){int i=Find(lines,b);if(i>=0){at=i+1;break;}}
            if(at<0&&m.sectionEnd!=null) {
                int s=Find(lines,m.sectionEnd);
                if(s>=0){int j=s-1;while(j>=0&&!lines[j].StartsWith("#")&&!Separator(lines[j]))j--;at=j+1;}
            }
            if(at<0){lines.Insert(old,line);if(log!=null)log("Mod list: no place found for \""+m.mod+"\", left alone.");continue;}
            lines.Insert(at,line);
            if(at!=old){moved++;if(log!=null)log("Mod list: moved \""+m.mod+"\".");}
        }
        return moved;
    }
}

public static class ListPatch {
    static bool Same(string a,string b){return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
    static string EntryName(string line){return line.Length>0&&"+-*".IndexOf(line[0])>=0?line.Substring(1):line;}
    public static List<string> Apply(IEnumerable<string> lines,ProfileEdit e) {
        var r=new List<string>(lines);
        foreach(var x in e.remove??new string[0])r.RemoveAll(l=>Same(l,x));
        foreach(var p in e.place??new PlaceItem[0]) {
            if(p.addOnly) {
                string name=EntryName(p.line);
                if(r.Any(l=>!l.StartsWith("#")&&Same(EntryName(l),name)))continue;
                int spot=-1;
                if(p.above!=null){int i=r.FindIndex(l=>!l.StartsWith("#")&&Same(EntryName(l),p.above));if(i>=0)spot=i;}
                if(spot<0)foreach(var a in p.after??new string[0]){int i=r.FindIndex(l=>Same(l,a));if(i>=0){spot=i+1;break;}}
                if(spot>=0)r.Insert(spot,p.line);
                continue;
            }
            r.RemoveAll(l=>Same(l,p.line));
            int at=-1;
            foreach(var a in p.after??new string[0]){int i=r.FindIndex(l=>Same(l,a));if(i>=0){at=i+1;break;}}
            r.Insert(at<0?r.Count:at,p.line);
        }
        return r;
    }
    public static string Digest(IEnumerable<string> lines) {
        using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n",lines)))).Replace("-","").ToLowerInvariant();
    }
}

// The 17 October 2026 revision of the package: newer mods, replaced and retired mod files, and the matching profile order.
// A fresh installation gets it through the normal package; an existing setup gets it from here, without reinstalling.
public static class UpdateSupport {
    public const string RevisionName="revision.txt",RevisionFile="_setup/revision.txt";
    static readonly string[] ProfileFiles={"modlist.txt","plugins.txt","loadorder.txt"};
    public static int Revision(string dest) {
        try{string p=Engine.SafeJoin(dest,RevisionFile);int n;return File.Exists(p)&&Int32.TryParse(File.ReadAllText(p).Trim(),out n)?n:0;}catch{return 0;}
    }
    public static bool Needs(Engine engine,string dest){return engine.P.revision>Revision(dest);}
    // Shortcuts made by older installers get the current names; failures are logged and never stop an update.
    static void RenameShortcuts(string dest,Action<string> log) {
        try{var state=LaunchSupport.ReadState(dest);Integrations.MigrateNames(dest,state.game,log);}
        catch(Exception ex){log("Shortcut names were not updated: "+ex.Message);}
        // Every update run also makes sure the Windows 'Installed apps' entry exists and names this mod folder.
        UninstallSupport.WriteRegistry(dest,log);
    }

    // Fresh install: the packaged lists come out of the data parts; this brings them to the revision's lists and proves the result.
    // An update passes the revision the setup already has: edits of that revision are skipped, so the user's own arrangement is not undone.
    public static void ApplyProfileEdits(Engine engine,string dest,bool strict,int haveRevision=0) {
        string profile=Engine.SafeJoin(dest,"profiles/Default");
        foreach(var e in engine.P.profileEdits??new ProfileEdit[0]) {
            if(haveRevision>=(e.revision<=0?1:e.revision))continue;
            if(Array.IndexOf(ProfileFiles,e.file)<0)throw new Exception("Unexpected profile edit target: "+e.file);
            string path=Engine.SafeJoin(profile,e.file);
            var lines=File.ReadAllLines(path,Engine.Utf8).ToList();
            var edited=ListPatch.Apply(lines,e);
            if(strict&&!String.IsNullOrEmpty(e.result)&&ListPatch.Digest(edited)!=e.result)throw new Exception("The packaged "+e.file+" does not match the expected revision. The data parts may be from another build.");
            File.WriteAllLines(path,edited,Engine.Utf8);
        }
    }
    static string IniValue(string text,string section,string key) {
        string current=null;bool inside=false;
        foreach(var raw in text.Replace("\r","").Split('\n')) {
            string l=raw.Trim();
            if(l.StartsWith("[")&&l.EndsWith("]")){inside=String.Equals(l.Substring(1,l.Length-2),section,StringComparison.OrdinalIgnoreCase);continue;}
            if(!inside||l.StartsWith(";")||l.StartsWith("#"))continue;
            int eq=l.IndexOf('=');if(eq>0&&String.Equals(l.Substring(0,eq).Trim(),key,StringComparison.OrdinalIgnoreCase))current=l.Substring(eq+1).Trim();
        }
        return current;
    }
    // Profile INI key edits newer than haveRevision. fresh: a new installation, the packaged file is ours and gets every value. Returns the files that changed.
    public static List<string> ApplyIniEdits(Engine engine,string dest,int haveRevision,bool fresh,Action<string> log) {
        log=log??delegate{};var changedFiles=new List<string>();
        string profile=Engine.SafeJoin(dest,"profiles/Default");
        foreach(var group in (engine.P.iniEdits??new IniEdit[0]).Where(e=>haveRevision<(e.revision<=0?1:e.revision)).GroupBy(e=>e.file,StringComparer.OrdinalIgnoreCase)) {
            if(!group.Key.EndsWith(".ini",StringComparison.OrdinalIgnoreCase)||group.Key.IndexOfAny(new[]{'/','\\'})>=0)throw new Exception("Unexpected INI edit target: "+group.Key);
            string path=Engine.SafeJoin(profile,group.Key);if(!File.Exists(path)){log("INI edits skipped, "+group.Key+" does not exist.");continue;}
            string text=File.ReadAllText(path,Engine.Utf8),before=text;int set=0,kept=0;
            foreach(var e in group) {
                string cur=IniValue(text,e.section,e.key);
                if(cur==e.value)continue;
                if(!fresh&&!(cur==null?e.was==null:cur==e.was)){kept++;log("Kept your value: "+group.Key+" ["+e.section+"] "+e.key+"="+cur);continue;}
                text=Engine.Ini(text,e.section,e.key,e.value);set++;
            }
            if(text!=before){File.WriteAllText(path,text,Engine.Utf8);changedFiles.Add(path);}
            if(set>0||kept>0)log("INI "+group.Key+": "+set+" value(s) updated, "+kept+" of your own kept.");
        }
        return changedFiles;
    }    public static void EnsureGeckProfileInis(string profile) {
        foreach(var n in new[]{"GECKCustom.ini","GECKPrefs.ini"}) {
            string p=Path.Combine(profile,n);
            if(!File.Exists(p))File.WriteAllBytes(p,new byte[0]);
        }
    }
    // Adds the editor entry to an existing ModOrganizer.ini. Idempotent; a user's own entries keep their numbers.
    public static string AddGeckNvse(string text,string game) {
        string g=game.Replace('\\','/');
        var titles=System.Text.RegularExpressions.Regex.Matches(text,@"(?im)^\d+\\title=(.*)$").Cast<System.Text.RegularExpressions.Match>().Select(m=>m.Groups[1].Value.Trim());
        if(titles.Any(t=>t==Engine.GeckNvseTitle))return text;
        int start=text.IndexOf("[customExecutables]",StringComparison.OrdinalIgnoreCase);
        if(start<0)return text;
        int count=0;
        foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text.Substring(start),@"(?im)^(\d+)\\title="))count=Math.Max(count,Int32.Parse(m.Groups[1].Value));
        if(count==0)return text;
        int index=count+1;
        foreach(var line in Engine.GeckNvseEntry(index,g).Split(new[]{"\r\n"},StringSplitOptions.None)){int eq=line.IndexOf('=');text=Engine.Ini(text,"customExecutables",line.Substring(0,eq),line.Substring(eq+1));}
        return Engine.Ini(text,"customExecutables","size",index.ToString());
    }

    // Interactive or scripted entry point. Returns true when the setup is now current.
    public static bool Run(Engine engine,string destination,bool quiet,Action<string> log) {
        log=log??delegate{};
        try {
            string dest=destination;
            if(String.IsNullOrWhiteSpace(dest)) {
                dest=PortableSupport.InstalledFolderOf(Assembly.GetExecutingAssembly().Location);
                if(dest==null) {
                    if(quiet)throw new Exception("Use --update --destination <mod folder>.");
                    using(var f=new FolderBrowserDialog{Description="Select the mod folder of the setup to update (the folder that contains _setup and ModOrganizer.ini)."}) {
                        if(f.ShowDialog()!=DialogResult.OK)return false;
                        dest=f.SelectedPath;
                    }
                }
            }
            dest=Engine.Full(dest);
            if(!Needs(engine,dest)&&Revision(dest)>0)RenameShortcuts(dest,log);
            if(!quiet&&!Needs(engine,dest)&&Revision(dest)>0){MessageBox.Show("This setup is already up to date.","Update setup",MessageBoxButtons.OK,MessageBoxIcon.Information);return true;}
            if(!quiet&&MessageBox.Show("Update this setup with the newer mods?\r\n\r\n"+dest+"\r\n\r\nClose Mod Organizer and the game first. Files that the update replaces are backed up inside _setup\\update-backup, your saves, downloads and own edits are kept, and the mod list order is adjusted instead of replaced.","Update setup",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return false;
            string summary=Apply(engine,dest,log);
            if(!quiet)MessageBox.Show(summary,"Update setup",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return true;
        } catch(Exception ex) {
            log("UPDATE FAILED: "+ex.Message);
            if(quiet)throw;
            MessageBox.Show(ex.Message,"Update setup",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return false;
        }
    }

    sealed class Journal {
        public readonly string Backup;
        public readonly List<string[]> Saved=new List<string[]>();   // target, backup copy
        public readonly List<string> Created=new List<string>();
        readonly string root;
        public Journal(string root,string backup){this.root=root;Backup=backup;}
        // Copies a file that is about to change, once.
        public void Save(string dest,string target) {
            if(!File.Exists(target)||Saved.Any(s=>s[0].Equals(target,StringComparison.OrdinalIgnoreCase)))return;
            string copy=Engine.SafeJoin(Backup,"files/"+target.Substring(dest.Length+1).Replace('\\','/'));
            Engine.EnsureParent(copy);File.Copy(target,copy,true);Saved.Add(new[]{target,copy});
        }
        public void Undo() {
            foreach(var c in Created.AsEnumerable().Reverse())try{if(File.Exists(c)&&!Saved.Any(s=>s[0].Equals(c,StringComparison.OrdinalIgnoreCase)))File.Delete(c);for(string d=Path.GetDirectoryName(c);d!=null&&Engine.Within(d,root)&&!d.Equals(root,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(d)&&!Directory.EnumerateFileSystemEntries(d).Any();d=Path.GetDirectoryName(d))Directory.Delete(d,false);}catch{}
            foreach(var s in Saved){Engine.EnsureParent(s[0]);File.Copy(s[1],s[0],true);}
        }
    }

    // Brings an existing setup to the package's revision. Everything it changes is backed up first and restored if a step fails.
    public static string Apply(Engine engine,string destination,Action<string> log) {
        log=log??delegate{};var P=engine.P;
        string dest=Engine.Full(destination);
        var plan=PortableSupport.Inspect(dest);
        if(plan.DestinationMoved)throw new Exception("This setup was moved. Run \""+PortableSupport.RepairCommand+"\" in the mod folder first, then update.");
        var state=plan.State;
        if(state.version!=P.version)throw new Exception("This setup was installed by package version "+state.version+", but this installer contains "+P.version+". Update it with the installer that matches it.");
        int have=Revision(dest);
        if(have>=P.revision){log("The setup is already at package revision "+have+".");RenameShortcuts(dest,log);return "This setup is already up to date.";}
        Engine.NotRunning(state.game,dest);
        string setup=Engine.SafeJoin(dest,"_setup");
        var changed=P.files.Where(f=>f.part!=null).ToList();
        foreach(var name in changed.Select(f=>f.part).Distinct(StringComparer.OrdinalIgnoreCase)) {
            var a=P.archives.FirstOrDefault(x=>String.Equals(x.name,name,StringComparison.OrdinalIgnoreCase));
            if(a==null)throw new Exception("The package inventory does not list the update part "+name+".");
            string p=Engine.SafeJoin(engine.PackageFolder,a.name);
            if(!File.Exists(p)||new FileInfo(p).Length!=a.size||Engine.Hash(p)!=a.hash)throw new Exception("Missing or damaged data part: "+a.name+". Keep every .bin file beside the EXE.");
        }
        foreach(var f in changed)if(Engine.SafeJoin(dest,f.path.Substring(4)).Length>=248)throw new Exception("The Mod Organizer path is too long for a file in this update: "+f.path);
        string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss"),backup=Engine.SafeJoin(setup,"update-backup/"+stamp);
        Directory.CreateDirectory(backup);
        var journal=new Journal(dest,backup);var touched=new List<string>();var removed=new List<string>();
        int added=0,replaced=0,kept=0,retiredCount=0,movedEntries=0;
        try {
            // 1. Changed and new files, each verified while it is written.
            foreach(var group in changed.GroupBy(f=>f.part,StringComparer.OrdinalIgnoreCase))
                using(var zip=ZipFile.OpenRead(Engine.SafeJoin(engine.PackageFolder,group.Key))) {
                    foreach(var f in group) {
                        string target=Engine.SafeJoin(dest,f.path.Substring(4));
                        bool exists=File.Exists(target);
                        if(exists) {
                            string current=Engine.Hash(target);
                            if(current==f.hash)continue;
                            // A file the user edited since installation is not overwritten.
                            if(f.was!=null&&current!=f.was&&!(f.wasAlso!=null&&f.wasAlso.Contains(current))){kept++;log("Kept your edited file: "+f.path.Substring(4));continue;}
                        }
                        var entry=zip.GetEntry(f.path);
                        if(entry==null||entry.Length!=f.size)throw new Exception("Update part "+group.Key+" does not contain "+f.path+" as expected.");
                        Engine.EnsureParent(target);
                        string temp=target+".update-tmp";
                        using(var src=entry.Open())using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write))using(var h=SHA256.Create()) {
                            byte[] b=new byte[1024*1024];int n;
                            while((n=src.Read(b,0,b.Length))>0){output.Write(b,0,n);h.TransformBlock(b,0,n,null,0);}
                            h.TransformFinalBlock(new byte[0],0,0);
                            if(BitConverter.ToString(h.Hash).Replace("-","").ToLowerInvariant()!=f.hash){output.Close();File.Delete(temp);throw new Exception("Damaged update file: "+f.path);}
                        }
                        if(exists){journal.Save(dest,target);File.Delete(target);replaced++;}else{journal.Created.Add(target);added++;}
                        File.Move(temp,target);touched.Add(target);
                    }
                }
            log("Update files: "+added+" added, "+replaced+" replaced, "+kept+" kept because you edited them.");
            // 2. Files that are no longer part of the package (only if they are still the packaged bytes; MO2's own meta.ini is always theirs).
            foreach(var r in P.retired??new FileItem[0]) {
                string target=Engine.SafeJoin(dest,r.path.Substring(4));
                if(!File.Exists(target))continue;
                bool meta=Path.GetFileName(target).Equals("meta.ini",StringComparison.OrdinalIgnoreCase);
                if(!meta&&Engine.Hash(target)!=r.hash){log("Kept a changed file of a retired mod: "+r.path.Substring(4));continue;}
                journal.Save(dest,target);File.Delete(target);removed.Add(target);retiredCount++;
                string mods=Engine.SafeJoin(dest,"mods");
                for(string d=Path.GetDirectoryName(target);d!=null&&Engine.Within(d,mods)&&!d.Equals(mods,StringComparison.OrdinalIgnoreCase)&&!Directory.EnumerateFileSystemEntries(d).Any();d=Path.GetDirectoryName(d))Directory.Delete(d,false);
            }
            if(retiredCount>0)log("Removed "+retiredCount+" files of mods that are no longer part of the setup.");
            // 3. Language overlays built from a changed or retired plugin would shadow the new text with old, mismatched copies.
            var lang=(P.languages??new LanguageItem[0]).FirstOrDefault(x=>x.id==PortableSupport.InstalledLanguage(P,dest));
            if(lang!=null)foreach(var rp in lang.retiredPatches??new LanguagePatch[0]) {
                string target=Engine.SafeJoin(dest,rp.target);
                if(File.Exists(target)&&Engine.Hash(target)==rp.targetHash){journal.Save(dest,target);File.Delete(target);removed.Add(target);log("Removed an outdated Russian overlay: "+Path.GetFileName(target));}
            }
            // 4. Profile lists: edits applied to what the user has, never replaced wholesale.
            string profile=Engine.SafeJoin(dest,"profiles/Default");
            foreach(var n in ProfileFiles)journal.Save(dest,Path.Combine(profile,n));
            foreach(var n in new[]{"ModOrganizer.ini"})journal.Save(dest,Engine.SafeJoin(dest,n));
            ApplyProfileEdits(engine,dest,false,have);
            foreach(var n in ProfileFiles)touched.Add(Path.Combine(profile,n));
            foreach(var f in (P.iniEdits??new IniEdit[0]).Where(e=>have<(e.revision<=0?1:e.revision)).Select(e=>e.file).Distinct(StringComparer.OrdinalIgnoreCase)){string ip=Path.Combine(profile,f);if(File.Exists(ip))journal.Save(dest,ip);}
            foreach(var ip in ApplyIniEdits(engine,dest,have,false,log))touched.Add(ip);
            var modlist=File.ReadAllLines(Path.Combine(profile,"modlist.txt"),Engine.Utf8).ToList();
            // Single mods that changed places in a newer revision: only those entries move, the user's own order stays.
            movedEntries=ModlistMoves.Apply(modlist,P.modMoves,have,log);
            if(movedEntries>0)File.WriteAllLines(Path.Combine(profile,"modlist.txt"),modlist,Engine.Utf8);
            // 5. Localized (non-Russian) setups: the overlay copies of changed plugins are stale; translate the changed and new plugins again.
            string localized=Engine.SafeJoin(dest,"mods/"+Localization.ModName);
            if(lang==null&&Directory.Exists(localized)) {
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var f in changed){string n=f.path.Substring(f.path.LastIndexOf('/')+1);if(f.path.Count(c=>c=='/')==3&&(n.EndsWith(".esm",StringComparison.OrdinalIgnoreCase)||n.EndsWith(".esp",StringComparison.OrdinalIgnoreCase)))names.Add(n);}
                foreach(var f in P.retired??new FileItem[0]){string n=f.path.Substring(f.path.LastIndexOf('/')+1);if(f.path.Count(c=>c=='/')==3&&(n.EndsWith(".esm",StringComparison.OrdinalIgnoreCase)||n.EndsWith(".esp",StringComparison.OrdinalIgnoreCase)))names.Add(n);}
                foreach(var n in names){string stale=Engine.SafeJoin(localized,n);if(File.Exists(stale)){journal.Save(dest,stale);File.Delete(stale);removed.Add(stale);}}
                try{Localization.Apply(engine,new Options{Destination=dest,Game=state.game,Language="Localized"},log,names);}
                catch(Exception lx){log("Translated text for the updated plugins could not be refreshed ("+lx.Message+"); they stay in English.");}
            }
            // 6. GECK: the profile INIs MO2 expects, and the editor entry that loads xNVSE plugins.
            EnsureGeckProfileInis(profile);
            string ini=Engine.SafeJoin(dest,"ModOrganizer.ini");
            if(File.Exists(ini)){string text=File.ReadAllText(ini,Engine.Utf8),now=AddGeckNvse(text,state.game);if(now!=text){File.WriteAllText(ini,now,Engine.Utf8);touched.Add(ini);log("Added the \""+Engine.GeckNvseTitle+"\" entry to Mod Organizer.");}}
            // 7. Plugin timestamps follow the new order.
            engine.StampPluginOrder(dest,state.game,modlist,false);
            File.WriteAllText(Engine.SafeJoin(setup,RevisionName),P.revision.ToString(),Engine.Utf8);touched.Add(Engine.SafeJoin(setup,RevisionName));
            // 8. A newer helper replaces the old one so Play, Move and Uninstall use the package that matches the files now.
            string self=Assembly.GetExecutingAssembly().Location,helper=LaunchSupport.Helper(dest);
            if(!Engine.Within(self,dest)&&File.Exists(self)&&(!File.Exists(helper)||Engine.Hash(helper)!=Engine.Hash(self))){File.Copy(self,helper,true);touched.Add(helper);log("Updated the setup helper.");}
            PortableSupport.UpdateReceipt(dest,touched.Concat(Directory.GetFiles(profile,"G*.ini")),removed.Select(x=>x.Substring(dest.Length+1)));
        } catch(Exception ex) {
            log("UPDATE FAILED: "+ex.Message);
            try {
                journal.Undo();
                string mods=Engine.SafeJoin(dest,"mods");
                foreach(var t in changed) {
                    string target=Engine.SafeJoin(dest,t.path.Substring(4)),tmp=target+".update-tmp";if(File.Exists(tmp))File.Delete(tmp);
                    for(string d=Path.GetDirectoryName(target);d!=null&&Engine.Within(d,mods)&&!d.Equals(mods,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(d)&&!Directory.EnumerateFileSystemEntries(d).Any();d=Path.GetDirectoryName(d))Directory.Delete(d,false);
                }
                log("Every file the update touched was put back.");
            }
            catch(Exception re){log("RESTORE REQUIRED: "+re.Message+" The backups are in "+backup);}
            throw new Exception(ex.Message+"\r\nThe setup was left as it was before the update.",ex);
        }
        RenameShortcuts(dest,log);
        log("Setup updated to package revision "+P.revision+". Backup of replaced files: "+backup);
        return "The setup is up to date (package revision "+P.revision+").\r\n\r\n"+added+" files added, "+replaced+" replaced, "+retiredCount+" retired"+(kept>0?", "+kept+" of your edited files kept":"")+".\r\nThe mod list and plugin order follow the new package"+(movedEntries>0?" ("+movedEntries+" mod list "+(movedEntries==1?"entry":"entries")+" moved, nothing else reordered)":"")+". Replaced files are backed up in _setup\\update-backup\\"+stamp+".";
    }
}
}
