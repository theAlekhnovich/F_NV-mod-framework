using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
namespace FalloutModSetup {
public static class LaunchSupport {
    public static string Arguments(string dest,bool play) {return (play?"--play":"--manage")+" --destination "+Engine.Q(dest);}
    public static string Helper(string dest) {return Engine.SafeJoin(dest,"_setup/Setup and Restore.exe");}
    public static InstallState ReadState(string dest) {
        dest=Engine.Full(dest);var state=Engine.Json().Deserialize<InstallState>(File.ReadAllText(Engine.SafeJoin(dest,"_setup/state.json"),Engine.Utf8));
        if(!String.Equals(Engine.Full(state.destination),dest,StringComparison.OrdinalIgnoreCase))throw new Exception("This installation has moved. Run \""+PortableSupport.RepairCommand+"\" in its mod folder to update the paths and shortcuts.");
        state.game=Engine.Full(state.game);if(state.status!="installed")throw new Exception("This installation is not active. Finish installing before launching it.");return state;
    }
    public static void CheckInstances(string dest) {
        foreach(var p in Process.GetProcessesByName("ModOrganizer"))using(p) {
            string path;try{path=p.MainModule.FileName;}catch{throw new Exception("Close Mod Organizer before launching this installation.");}
            if(!path.Equals(Path.Combine(dest,"ModOrganizer.exe"),StringComparison.OrdinalIgnoreCase))throw new Exception("Another Mod Organizer instance is open:\r\n"+path+"\r\n\r\nClose it and try again. Otherwise MO2 can forward this shortcut to the wrong game or launcher.");
            throw new Exception("Close this Mod Organizer window before using the game shortcut, or select New Vegas and click Run inside it.");
        }
    }
    public static string DirectArguments(string game,bool epic=false) {return "-p Default "+Engine.Q(Path.Combine(game,"FalloutNV.exe"))+(epic?" -EpicPortal":"");}
    public static string FirstRunStatus(string preferences=null) {
        string p=preferences??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"My Games","FalloutNV","FalloutPrefs.ini");
        try{if(File.Exists(p)&&Regex.IsMatch(File.ReadAllText(p),@"(?im)^\s*sD3DDevice\s*=\s*\S"))return "Existing launcher settings found. Runtime checks still run.";}catch{}
        return "No usable launcher settings found. Setup supplies profile INIs and the local GPU name. Your store may still request its one-time first-run setup.";
    }
    public static void Prepare(string dest,string game) {
        string profile=Engine.SafeJoin(dest,"profiles/Default"),settings=Path.Combine(profile,"settings.ini");
        File.WriteAllText(settings,Engine.Ini(File.ReadAllText(settings,Engine.Utf8),"General","LocalSettings","true"),Engine.Utf8);
        string prefs=Path.Combine(profile,"falloutprefs.ini");string text=File.ReadAllText(prefs,Engine.Utf8);
        text=Engine.Ini(text,"Display","sD3DDevice",Engine.GraphicsAdapterName());text=Engine.Ini(text,"Display","iAdapter","0");File.WriteAllText(prefs,text,Engine.Utf8);
        string main=Path.Combine(profile,"fallout.ini");File.WriteAllText(main,Engine.Ini(File.ReadAllText(main,Engine.Utf8),"General","bUseMyGamesDirectory","1"),Engine.Utf8);
        string mo=Path.Combine(dest,"ModOrganizer.ini");text=File.ReadAllText(mo,Engine.Utf8);
        // Make the visible MO2 entry agree with the explicit shortcut target. Never launch the selected list index.
        var title=Regex.Match(text,@"(?im)^(\d+)\\title=New Vegas\s*$");
        if(title.Success){string key=title.Groups[1].Value+"\\";text=Engine.Ini(text,"customExecutables",key+"binary",Engine.Q(Path.Combine(game,"FalloutNV.exe")));text=Engine.Ini(text,"customExecutables",key+"workingDirectory",Engine.Q(game));}
        File.WriteAllText(mo,text,Engine.Utf8);
    }
    public static void Launch(string destination,bool play) {
        string dest=Engine.Full(destination);var state=ReadState(dest);CheckInstances(dest);
        if(play){if(!Engine.HasLoaderPatch(Path.Combine(state.game,"FalloutNV.exe")))throw new Exception("The game executable no longer has the verified 4GB/xNVSE patch. Store verification may have replaced it. Restore and reinstall this framework.");Prepare(dest,state.game);}
        bool epic=Regex.IsMatch(File.ReadAllText(Path.Combine(dest,"ModOrganizer.ini"),Engine.Utf8),@"(?im)^\s*game_edition\s*=\s*Epic Games\s*$");
        string args=play?DirectArguments(state.game,epic):"";
        File.AppendAllText(Engine.SafeJoin(dest,"_setup/launch.log"),DateTime.Now.ToString("s")+" "+(play?"Game":"MO2")+": "+Path.Combine(dest,"ModOrganizer.exe")+" "+args+Environment.NewLine,Engine.Utf8);
        Process.Start(new ProcessStartInfo(Path.Combine(dest,"ModOrganizer.exe"),args){WorkingDirectory=dest,UseShellExecute=true});
    }
}
}
