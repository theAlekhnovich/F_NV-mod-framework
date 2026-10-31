using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
namespace FalloutModSetup {
public class DefenderRecord {public string destination,game,status="pending";public List<string> added=new List<string>();}
// What an add did: Added exclusions are new, Present ones were already in Defender (and stay out of the uninstall record).
public class DefenderResult {public int Added,Present;}
public static class DefenderSupport {
    public static string[] Targets(string game,string dest) {
        game=Engine.Full(game);dest=Engine.Full(dest);
        return new[]{"FalloutNV.exe","nvse_loader.exe","nvse_1_4.dll","nvse_steam_loader.dll"}.Select(n=>Engine.SafeJoin(game,n)).Concat(new[]{"ModOrganizer.exe","helper.exe","usvfs_x86.dll","usvfs_x64.dll"}.Select(n=>Engine.SafeJoin(dest,n))).ToArray();
    }
    static string Journal(string dest){return Engine.SafeJoin(dest,"_setup/defender.json");}
    static void Save(DefenderRecord r){string p=Journal(r.destination);File.WriteAllText(p+".tmp",Engine.Json().Serialize(r),Engine.Utf8);if(File.Exists(p))File.Replace(p+".tmp",p,null);else File.Move(p+".tmp",p);}
    static string PowerShell(string script) {
        string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
        // The script is passed as readable -Command text, never as a base64 -EncodedCommand blob (that look is shared with malware droppers). It has no double quotes: paths are single-quoted by Literal and Windows paths cannot contain a double quote.
        var info=new ProcessStartInfo(path,"-NoProfile -NonInteractive -Command \""+CommandText(script)+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        using(var p=Process.Start(info)){var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(120000)){p.Kill();throw new Exception("Windows Defender did not answer. No security policy will be bypassed.");}if(p.ExitCode!=0)throw new Exception(Explain(error.Result));return output.Result.Trim();}
    }
    public static string CommandText(string script) {
        string text="$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); "+script;
        if(text.IndexOf('"')>=0)throw new ArgumentException("A Defender script must not contain a double quote.");
        return text;
    }
    // PowerShell reports errors as CLIXML. Keep the first real error line and say what it usually means.
    public static string CleanError(string raw) {
        if(String.IsNullOrWhiteSpace(raw))return "";
        if(raw.IndexOf("#< CLIXML",StringComparison.Ordinal)<0)return raw.Trim();
        var text=new StringBuilder();
        foreach(Match m in Regex.Matches(raw,"<S S=\"Error\">(.*?)</S>",RegexOptions.Singleline))text.Append(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Replace("_x000D_","").Replace("_x000A_","\n")));
        foreach(var line in text.ToString().Split('\n')){string t=line.Trim();if(t.Length>0)return t;}
        return "";
    }
    public static string Explain(string raw) {
        string detail=CleanError(raw);
        string why=Regex.IsMatch(detail,"800106ba",RegexOptions.IgnoreCase)?"Microsoft Defender is not running on this PC. Usually another antivirus is the active one.":"Check that Defender is active and that device policy allows exclusions.";
        return "Microsoft Defender could not add the file exclusions. "+why+(detail.Length>0?" ("+detail+")":"");
    }
    // Defender trouble must never stop an installation: the exclusions are optional. A record the failed attempt left behind is closed so uninstall is not blocked by it.
    // Returns true when nothing was added; false when some exclusions were already added and need a removal request.
    public static bool Abandon(string dest,string game) {
        if(!File.Exists(Journal(dest)))return true;
        DefenderRecord r;try{r=Engine.Json().Deserialize<DefenderRecord>(File.ReadAllText(Journal(dest),Engine.Utf8));}catch{r=null;}
        if(r!=null&&r.status=="removed")return true;
        if(r!=null&&r.added!=null&&r.added.Count>0)return false;
        Save(new DefenderRecord{destination=dest,game=game,status="removed"});return true;
    }
    static string Literal(string value){return "'"+value.Replace("'","''")+"'";}
    public static string Status() {try{return PowerShell("$s=Get-MpComputerStatus; if($s.AntivirusEnabled -and $s.RealTimeProtectionEnabled){'Microsoft Defender is active.'}else{'Defender is not fully active. Check your security provider before choosing exclusions.'}");}catch{return "Defender status is unavailable. A managed device or another antivirus may control protection.";}}
    static HashSet<string> Current() {var s=PowerShell("ConvertTo-Json -InputObject @((Get-MpPreference).ExclusionPath) -Compress");return new HashSet<string>(String.IsNullOrWhiteSpace(s)||s=="null"?new string[0]:Engine.Json().Deserialize<string[]>(s),StringComparer.OrdinalIgnoreCase);}
    public static DefenderResult ApplyCore(string game,string dest,Func<HashSet<string>> current,Action<string,bool> change) {
        var targets=Targets(game,dest);if(!File.Exists(Engine.SafeJoin(dest,"_setup/owned-marker")))throw new Exception("Installer ownership marker is missing.");
        DefenderRecord r;if(File.Exists(Journal(dest))){r=Read(dest,game);if(r.status!="removed")throw new Exception("Existing Defender changes must be removed before retrying.");}
        r=new DefenderRecord{game=game,destination=dest};Save(r);var before=current();
        foreach(string target in targets)if(!before.Contains(target)){r.added.Add(target);Save(r);change(target,true);if(!current().Contains(target))throw new Exception("Defender did not retain the exclusion: "+target);}
        r.status="installed";Save(r);
        return new DefenderResult{Added=r.added.Count,Present=targets.Length-r.added.Count};
    }
    // The Defender part of setup's single elevated step. A failed attempt takes back whatever it added, so nothing needs a second Windows prompt.
    public static DefenderResult Batch(string dest,string game) {
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new Exception("The Windows administrator prompt is required for Defender changes.");
        Action<string,bool> change=(p,a)=>PowerShell((a?"Add":"Remove")+"-MpPreference -ExclusionPath "+Literal(p));
        try{return ApplyCore(game,dest,Current,change);}
        catch{try{if(!Abandon(dest,game))RemoveCore(dest,Current,change);}catch{}throw;}
    }
    static DefenderRecord Read(string dest,string game=null) {
        var r=Engine.Json().Deserialize<DefenderRecord>(File.ReadAllText(Journal(dest),Engine.Utf8));
        if(!String.Equals(Engine.Full(r.destination),Engine.Full(dest),StringComparison.OrdinalIgnoreCase)||(game!=null&&!String.Equals(Engine.Full(r.game),Engine.Full(game),StringComparison.OrdinalIgnoreCase)))throw new Exception("Defender record belongs to another installation.");
        var allowed=new HashSet<string>(Targets(r.game,dest),StringComparer.OrdinalIgnoreCase);
        if(r.added==null||r.added.Any(p=>!allowed.Contains(p)))throw new Exception("Unexpected Defender exclusion path.");return r;
    }
    public static void RemoveCore(string dest,Func<HashSet<string>> current,Action<string,bool> change) {
        if(!File.Exists(Journal(dest)))return;var r=Read(dest);if(r.status=="removed")return;
        foreach(string p in r.added)if(current().Contains(p)){change(p,false);if(current().Contains(p))throw new Exception("Defender could not remove an installer-added exclusion: "+p);}
        r.status="removed";Save(r);
    }
    public static void Elevated(string dest,string game,bool add) {
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new Exception("The Windows administrator prompt is required for Defender changes.");
        Action<string,bool> change=(p,a)=>PowerShell((a?"Add":"Remove")+"-MpPreference -ExclusionPath "+Literal(p));
        if(add)ApplyCore(game,dest,Current,change);else RemoveCore(dest,Current,change);
    }
    // After a move: drop the exclusions this installer added for the old paths and add the same eight files at the new ones.
    public static void RehomeCore(string dest,string game,string oldDest,string oldGame,Func<HashSet<string>> current,Action<string,bool> change) {
        if(!File.Exists(Journal(dest)))return;
        var r=Engine.Json().Deserialize<DefenderRecord>(File.ReadAllText(Journal(dest),Engine.Utf8));if(r.status=="removed")return;
        var allowed=new HashSet<string>(Targets(oldGame,oldDest),StringComparer.OrdinalIgnoreCase);
        if(r.added==null||r.added.Any(p=>!allowed.Contains(p)))throw new Exception("Unexpected Defender exclusion path.");
        foreach(string p in r.added)if(current().Contains(p)){change(p,false);if(current().Contains(p))throw new Exception("Defender could not remove an installer-added exclusion: "+p);}
        Save(new DefenderRecord{destination=dest,game=game,status="removed"});
        ApplyCore(game,dest,current,change);
    }
    public static void ElevatedRehome(string dest,string game,string oldDest,string oldGame) {
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new Exception("The Windows administrator prompt is required for Defender changes.");
        RehomeCore(dest,game,oldDest,oldGame,Current,(p,a)=>PowerShell((a?"Add":"Remove")+"-MpPreference -ExclusionPath "+Literal(p)));
    }
    // A record copied from another PC names exclusions that are not in this PC's Defender. Closes it, for this destination and game, so uninstall never trips over it.
    public static bool Detach(string dest,string game) {
        if(!File.Exists(Journal(dest)))return false;
        var r=Engine.Json().Deserialize<DefenderRecord>(File.ReadAllText(Journal(dest),Engine.Utf8));
        if(r.status!="installed")return false;
        Func<string,string> same=x=>(x??"").Replace('/','\\').TrimEnd('\\').ToLowerInvariant();
        if(same(r.destination)==same(dest)&&same(r.game)==same(game))return false;
        Save(new DefenderRecord{destination=dest,game=game,status="removed"});return true;
    }
    public static bool HasExclusions(string dest) {
        try{return File.Exists(Journal(dest))&&Engine.Json().Deserialize<DefenderRecord>(File.ReadAllText(Journal(dest),Engine.Utf8)).status=="installed";}catch{return false;}
    }
    public static void RequestRehome(string dest,string game,string oldDest,string oldGame) {
        string result=Engine.SafeJoin(dest,"_setup/defender-result.txt");if(File.Exists(result))File.Delete(result);
        string args="--defender-helper --destination "+Engine.Q(dest)+" --game "+Engine.Q(game)+" --action rehome --old-destination "+Engine.Q(oldDest)+" --old-game "+Engine.Q(oldGame);
        var info=new ProcessStartInfo(typeof(Engine).Assembly.Location,args){UseShellExecute=true,Verb="runas",WorkingDirectory=Path.GetDirectoryName(typeof(Engine).Assembly.Location)};
        using(var p=Process.Start(info)){p.WaitForExit();if(p.ExitCode!=0)throw new Exception(File.Exists(result)?File.ReadAllText(result):"Defender exclusions were not moved.");}
    }
    public static void Request(string dest,string game,bool add) {
        if(!add&&(!File.Exists(Journal(dest))||Read(dest).status=="removed"))return;
        string result=Engine.SafeJoin(dest,"_setup/defender-result.txt");if(File.Exists(result))File.Delete(result);
        string args="--defender-helper --destination "+Engine.Q(dest)+" --game "+Engine.Q(game)+" --action "+(add?"add":"remove");
        var info=new ProcessStartInfo(typeof(Engine).Assembly.Location,args){UseShellExecute=true,Verb="runas",WorkingDirectory=Path.GetDirectoryName(typeof(Engine).Assembly.Location)};
        using(var p=Process.Start(info)){p.WaitForExit();if(p.ExitCode!=0)throw new Exception(File.Exists(result)?File.ReadAllText(result):"Defender changes were not applied. You can leave the optional exclusion setting off.");}
    }
}
}
