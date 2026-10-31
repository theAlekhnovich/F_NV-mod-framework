using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using FalloutModSetup;

class ComponentChecks {
    static void Assert(bool ok,string msg){if(!ok)throw new Exception(msg);Console.WriteLine("PASS "+msg);}
    [STAThread] static int Main(string[] args){
        string root=args[0],game=args[1];Directory.CreateDirectory(root);var engine=new Engine();
        foreach(string store in new[]{"Steam","GOG","Epic"}) {
            string dest=Path.Combine(root,store);Directory.CreateDirectory(Path.Combine(dest,"profiles","Default"));
            File.WriteAllText(Path.Combine(dest,"profiles","Default","falloutprefs.ini"),"[Display]\niSize W=2560\niSize H=1440\nsD3DDevice=NVIDIA\n");
            File.WriteAllText(Path.Combine(dest,"profiles","Default","falloutcustom.ini"),"[Display]\nbFull Screen=1\n");
            File.WriteAllText(Path.Combine(dest,"profiles","Default","settings.ini"),"[General]\nLocalSaves=true\n");
            File.WriteAllText(Path.Combine(dest,"profiles","Default","fallout.ini"),"[General]\nsLocalSavePath=Saves\\\n");
            var o=new Options{Game=game,Destination=dest,Store=store,Width=1600,Height=900};
            typeof(Engine).GetMethod("Configure",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine,new object[]{o});
            string ini=File.ReadAllText(Path.Combine(dest,"ModOrganizer.ini"));
            Assert(ini.Contains("game_edition="+(store=="Epic"?"Epic Games":store)),store+" edition");
            Assert(ini.Contains("1\\arguments="+(store=="Epic"?"-EpicPortal":"")+"\r\n"),store+" launch arguments");
            Assert(ini.Contains("1\\steamAppID="+(store=="Steam"?"22490":"")+"\r\n"),store+" Steam ID");
            Assert(File.ReadAllText(Path.Combine(dest,"profiles","Default","falloutprefs.ini")).Contains("iSize W=1600"),store+" resolution");
            Assert(File.ReadAllText(Path.Combine(dest,"profiles","Default","falloutprefs.ini")).Contains("sD3DDevice="+Engine.GraphicsAdapterName()),store+" local DirectX adapter instead of blank/source GPU");
            foreach(bool windowed in new[]{true,false}) {
                o.Windowed=windowed;typeof(Engine).GetMethod("Configure",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine,new object[]{o});
                string expected="bFull Screen="+(windowed?"0":"1");
                Assert(new[]{"falloutprefs.ini","falloutcustom.ini"}.All(n=>File.ReadAllText(Path.Combine(dest,"profiles","Default",n)).Contains(expected)),store+" "+(windowed?"windowed":"fullscreen")+" agrees in both profile files");
            }
        }
        using(var f=new SetupForm(engine)) {
            var field=typeof(SetupForm).GetField("store",BindingFlags.Instance|BindingFlags.NonPublic);
            var control=(string)field.GetValue(f);
            Assert(control=="Steam","GUI selects Steam by default");
        }
        bool rejected=false;try{Engine.SafeJoin(root,"../escape.txt");}catch{rejected=true;}Assert(rejected,"reject path traversal");
        rejected=false;try{Engine.SafeJoin(root,"C:/escape.txt");}catch{rejected=true;}Assert(rejected,"reject rooted archive paths");
        rejected=false;try{Engine.Full("C:\\");}catch{rejected=true;}Assert(rejected,"reject drive-root destination");
        string rollback=Path.Combine(root,"rollback"),rg=Path.Combine(rollback,"Game"),rd=Path.Combine(rollback,"Mods");
        Directory.CreateDirectory(rg);Directory.CreateDirectory(Path.Combine(rd,"_setup","backup"));
        File.WriteAllText(Path.Combine(rg,"nvse_1_4.dll"),"new");File.WriteAllText(Path.Combine(rd,"_setup","backup","nvse_1_4.dll"),"old");
        File.WriteAllText(Path.Combine(rg,"nvse_loader.exe"),"readonly old");File.WriteAllText(Path.Combine(rd,"_setup","backup","nvse_loader.exe"),"readonly old");
        File.WriteAllText(Path.Combine(rg,"nvse_steam_loader.dll"),"added");
        File.SetAttributes(Path.Combine(rg,"nvse_loader.exe"),FileAttributes.ReadOnly);
        var state=new InstallState{game=rg,destination=rd,status="installing",version="test"};
        state.changes.Add(new Change{path="nvse_1_4.dll",existed=true,touched=true,before=Engine.Hash(Path.Combine(rd,"_setup","backup","nvse_1_4.dll")),after=Engine.Hash(Path.Combine(rg,"nvse_1_4.dll"))});
        state.changes.Add(new Change{path="nvse_loader.exe",existed=true,touched=true,before=Engine.Hash(Path.Combine(rg,"nvse_loader.exe")),after="uncommitted"});
        state.changes.Add(new Change{path="nvse_steam_loader.dll",existed=false,touched=true,after=Engine.Hash(Path.Combine(rg,"nvse_steam_loader.dll"))});
        try {typeof(Engine).GetMethod("RestoreState",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine,new object[]{state,true});}
        finally {File.SetAttributes(Path.Combine(rg,"nvse_loader.exe"),FileAttributes.Normal);}
        Assert(File.ReadAllText(Path.Combine(rg,"nvse_1_4.dll"))=="old"&&!File.Exists(Path.Combine(rg,"nvse_steam_loader.dll"))&&state.status=="restored","rollback after a partially committed install, including unchanged read-only file");
        return 0;
    }
}
