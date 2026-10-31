using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FalloutModSetup;

class WMXChecks {
    static void Check(bool ok, string message) { if(!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static int Main(string[] args) {
        string root=args[0],companion="WMX - Hide Notes and Uninstall Menu";
        foreach(string language in new[]{"English","Russian"}) {
            string dest=Path.Combine(root,language),profile=Path.Combine(dest,"profiles","Default");
            Directory.CreateDirectory(profile);
            string wmx=Path.Combine(dest,"mods","WMX-DLC"),config=Path.Combine(dest,"mods",companion,"Config");
            Directory.CreateDirectory(wmx);Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config,"WMXPersonal.ini"),"[Inventory]\nHideReferenceNotes=0\n");
            File.WriteAllText(Path.Combine(wmx,"WMX-POPMerged.esp"),"original English plugin");
            string localized=Path.Combine(dest,"mods","Russian text compatibility");Directory.CreateDirectory(localized);
            File.WriteAllText(Path.Combine(localized,"WMX-POPMerged.esp"),"localized Russian plugin");
            File.WriteAllText(Path.Combine(profile,"modlist.txt"),"# fixture\n-"+companion+"\n+"+companion+"\n"+(language=="Russian"?"+Russian text compatibility\n":"")+"+WMX-DLC\n");
            File.WriteAllText(Path.Combine(profile,"plugins.txt"),"WMX-POPMerged.esp\n");
            File.WriteAllText(Path.Combine(profile,"loadorder.txt"),"WMX-POPMerged.esp\n");
            var options=new Options{Destination=dest,Game=Path.Combine(root,"UnusedGame")};var e=new Engine();
            var method=typeof(Engine).GetMethod("PreparePluginOrder",BindingFlags.Instance|BindingFlags.NonPublic);
            method.Invoke(e,new object[]{options});method.Invoke(e,new object[]{options});
            var lines=File.ReadAllLines(Path.Combine(profile,"modlist.txt"));
            Check(lines.Count(x=>x=="+"+companion)==1&&!lines.Contains("-"+companion),language+" companion enabled once, including repeated configuration");
            Check(Array.IndexOf(lines,"+"+companion)<Array.IndexOf(lines,"+WMX-DLC"),language+" companion priority");
            Check(File.ReadAllText(Path.Combine(config,"WMXPersonal.ini")).Contains("HideReferenceNotes=0"),language+" existing INI retained");
            Check(File.ReadAllText(Path.Combine(wmx,"WMX-POPMerged.esp"))=="original English plugin",language+" original plugin untouched");
            Check(File.ReadAllText(Path.Combine(localized,"WMX-POPMerged.esp"))=="localized Russian plugin",language+" translation untouched");
            Check(File.ReadAllText(Path.Combine(profile,"plugins.txt"))=="WMX-POPMerged.esp\n"&&File.ReadAllText(Path.Combine(profile,"loadorder.txt"))=="WMX-POPMerged.esp\n",language+" plugin activation and load order unchanged");
        }
        Check(new Engine().P.archives.Length==18,"compiled EXE contains the 16-part manifest plus update parts 17 and 18");
        Check(new Engine().P.files.Count(f=>f.path.StartsWith("app/mods/"+companion+"/"))==14,"compiled EXE verifies all fourteen companion files");
        return 0;
    }
}
