using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FalloutModSetup {
public class SetupForm:Form {
    static readonly Color Background=Color.FromArgb(26,19,13),Surface=Color.FromArgb(44,31,22),Ink=Color.FromArgb(235,229,215),Muted=Color.FromArgb(170,173,166),Amber=Color.FromArgb(243,167,112),Line=Color.FromArgb(79,43,27);
    readonly Engine engine;readonly Panel shell=new Panel(),body=new Panel(),nav=new Panel();
    readonly Label title=new Label(),subtitle=new Label(),stepCounter=new Label(),status=new Label(),review=new Label(),saveInfo=new Label();
    readonly TextBox game=new TextBox(),dest=new TextBox(),importPath=new TextBox(),log=new TextBox();
    readonly NumericUpDown width=new NumericUpDown(),height=new NumericUpDown();
    readonly CheckBox installRuntimes=new CheckBox(),defender=new CheckBox(),windowed=new CheckBox(),localSaves=new CheckBox(),importSaves=new CheckBox(),desktop=new CheckBox(),startMenu=new CheckBox();
    readonly Label runtimeInfo=new Label(),firstRunInfo=new Label(),defenderInfo=new Label();
    const int ReviewPage=5,ProgressPage=6,CompletePage=7;
    readonly Button back=new Button(),next=new Button(),cancel=new Button();readonly List<Panel> pages=new List<Panel>();readonly List<Label> steps=new List<Label>();
    readonly ProgressBar progress=new ProgressBar();readonly Label percent=new Label();readonly List<RadioButton> stores=new List<RadioButton>();
    readonly LinkLabel detected=new LinkLabel();List<GameCandidate> found=new List<GameCandidate>();int foundIndex;
    string store="Steam",languageChoice="English";int page;bool busy,complete,runtimeBusy;CancellationTokenSource cancellation;
    public SetupForm(Engine e) {
        foreach(var label in new[]{title,subtitle,stepCounter,status,review,saveInfo})label.UseMnemonic=false;
        engine=e;Text="Fallout New Vegas | Modification Framework";ClientSize=new Size(1040,740);MinimumSize=new Size(1000,650);StartPosition=FormStartPosition.CenterScreen;
        using(var icon=typeof(Engine).Assembly.GetManifestResourceStream("Lucky38.ico"))Icon=new Icon(icon);
        ShowIcon=true;ShowInTaskbar=true;
        AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Segoe UI",10);BackColor=Background;ForeColor=Ink;
        shell.Dock=DockStyle.Fill;shell.BackColor=Background;shell.Font=Font;shell.ForeColor=Ink;Controls.Add(shell);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,236));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));shell.Controls.Add(layout);
        nav.Dock=DockStyle.Fill;nav.BackColor=Surface;layout.Controls.Add(nav,0,0);
        var logo=new PictureBox{SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.Transparent};logo.SetBounds(8,29,220,80);
        using(var stream=typeof(Engine).Assembly.GetManifestResourceStream("Logo.png"))using(var original=Image.FromStream(stream))logo.Image=new Bitmap(original);
        nav.Controls.Add(logo);FormClosed+=delegate{logo.Image.Dispose();};
        AddLabel(nav,"MODIFICATION FRAMEWORK",18,123,210,34,8,Amber,true);
        string[] names={"Display & store","Locations","Required software","Windows Defender","Extras","Review & install"};
        for(int i=0;i<names.Length;i++){var l=AddLabel(nav,(i+1).ToString("00")+"   "+names[i],26,190+i*47,195,40,10,Muted,false);steps.Add(l);}
        var stats=AddLabel(nav,e.P.activeMods+" MODS\r\n"+e.P.plugins+" PLUGINS\r\n\r\nSnapshot "+e.P.version+"\r\nInstaller 2.0.0",30,540,188,150,9,Muted,false);stats.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        var right=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(36,28,36,22),ColumnCount=1,RowCount=4};right.RowStyles.Add(new RowStyle(SizeType.Absolute,94));right.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.Absolute,30));right.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.Controls.Add(right,1,0);
        var heading=new Panel{Dock=DockStyle.Fill};title.SetBounds(0,0,650,45);title.Font=new Font("Segoe UI",24,FontStyle.Bold);subtitle.SetBounds(0,50,690,42);subtitle.ForeColor=Muted;heading.Controls.Add(title);heading.Controls.Add(subtitle);right.Controls.Add(heading,0,0);
        body.Dock=DockStyle.Fill;right.Controls.Add(body,0,1);stepCounter.Dock=DockStyle.Fill;stepCounter.ForeColor=Muted;stepCounter.Font=new Font("Segoe UI",9);right.Controls.Add(stepCounter,0,2);
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,116));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));
        var restore=new LinkLabel{Text="Restore an installation",AutoSize=true,LinkColor=Muted,ActiveLinkColor=Amber,Margin=new Padding(0,14,0,0)};restore.LinkClicked+=async delegate{if(!busy&&!runtimeBusy)await RestoreInstallation();};footer.Controls.Add(restore,0,0);
        StyleButton(cancel,"Close",100,false);StyleButton(back,"Back",96,false);StyleButton(next,"Continue",144,true);footer.Controls.Add(cancel,1,0);footer.Controls.Add(back,2,0);footer.Controls.Add(next,3,0);right.Controls.Add(footer,0,3);
        BuildDisplayPage();BuildLocationsPage();BuildRuntimePage();BuildDefenderPage();BuildExtrasPage();BuildReviewPage();BuildProgressPage();BuildCompletePage();
        game.TextChanged+=delegate{AutoStore();UpdateDetected();};
        found=GameLocator.Find();if(found.Count>0)UseCandidate(0);else UpdateDetected();
        dest.Text=Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),"Games","FNV-Mods");
        string parent=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName;
        if(File.Exists(Path.Combine(parent,"_setup/state.json"))){dest.Text=parent;try{game.Text=Engine.Json().Deserialize<InstallState>(File.ReadAllText(Path.Combine(parent,"_setup/state.json"))).game;}catch{}}
        dest.TextChanged+=delegate{UpdateSaveInfo();};UpdateSaveInfo();
        back.Click+=delegate{if(page>0&&page<ProgressPage)ShowPage(page-1);};next.Click+=async delegate{await Continue();};cancel.Click+=delegate{if(busy){cancellation.Cancel();cancel.Enabled=false;status.Text="Finishing the current operation safely…";}else Close();};
        engine.Progress=delegate(string message,int value){if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)delegate{ReportProgress(message,value);});};
        engine.Fine=delegate(double value){if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)delegate{ShowPercent(value);});};
        FormClosing+=delegate(object sender,FormClosingEventArgs args){if(runtimeBusy){args.Cancel=true;return;}if(busy){args.Cancel=true;cancellation.Cancel();status.Text="Finishing the current operation safely…";}};
        ShowPage(0);
        var area=Screen.PrimaryScreen.WorkingArea;if(Height>area.Height)Height=area.Height;if(Width>area.Width)Width=area.Width;
    }
    // The bar and the percentage under it always show the same value.
    void ReportProgress(string message,int value) {
        status.Text=message;ShowPercent(value);
        if(!message.StartsWith("Installing files:"))log.AppendText(message+Environment.NewLine);
    }
    // Shown as 00.00%; never moves backwards during one installation (step values and exact values interleave).
    double shown;
    void ShowPercent(double value) {
        value=Math.Max(0,Math.Min(100,value));if(value<shown)value=shown;shown=value;
        progress.Value=(int)Math.Round(value*100);percent.Text=value.ToString("00.00",System.Globalization.CultureInfo.InvariantCulture)+"%";
    }
    void ResetPercent(){shown=0;progress.Value=0;percent.Text="00.00%";}
    static Label AddLabel(Control parent,string text,int x,int y,int w,int h,float size,Color color,bool bold) {
        var label=new Label{UseMnemonic=false,Text=text,ForeColor=color,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),BackColor=Color.Transparent};label.SetBounds(x,y,w,h);parent.Controls.Add(label);return label;
    }
    static void StyleButton(Button b,string text,int width,bool primary) {
        b.Text=text;b.Size=new Size(width,40);b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderSize=primary?0:1;b.FlatAppearance.BorderColor=Line;b.BackColor=primary?Amber:Surface;b.ForeColor=primary?Background:Ink;b.Cursor=Cursors.Hand;b.Margin=new Padding(4,4,0,0);b.UseVisualStyleBackColor=false;
    }
    Panel Page(){var p=new Panel{Dock=DockStyle.Fill,BackColor=Background,AutoScroll=true};body.Controls.Add(p);pages.Add(p);return p;}
    static void TextField(TextBox box){box.BackColor=Surface;box.ForeColor=Ink;box.BorderStyle=BorderStyle.FixedSingle;box.Font=new Font("Segoe UI",10);}
    void Folder(Control parent,string caption,TextBox box,int y) {
        AddLabel(parent,caption,0,y,650,24,11,Ink,true);TextField(box);box.SetBounds(0,y+35,555,30);box.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;parent.Controls.Add(box);
        var b=new Button();StyleButton(b,"Browse",100,false);b.SetBounds(568,y+28,100,38);b.Anchor=AnchorStyles.Top|AnchorStyles.Right;
        b.Click+=delegate{using(var dialog=new FolderBrowserDialog{Description=caption,SelectedPath=box.Text,ShowNewFolderButton=true})if(dialog.ShowDialog(this)==DialogResult.OK)box.Text=dialog.SelectedPath;};parent.Controls.Add(b);
    }
    static void Feature(Control parent,CheckBox c,string text,int y,bool selected) {
        c.Text=text;c.Checked=selected;c.SetBounds(0,y,690,30);c.Font=new Font("Segoe UI",11,FontStyle.Bold);c.ForeColor=Ink;c.BackColor=Background;c.UseVisualStyleBackColor=false;parent.Controls.Add(c);
    }
    void BuildDisplayPage() {
        var p=Page();AddLabel(p,"GAME STORE",0,10,600,22,9,Amber,true);
        for(int i=0;i<3;i++) {
            string name=new[]{"Steam","GOG","Epic"}[i];var r=new RadioButton{Text=name,Appearance=Appearance.Button,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleCenter,UseVisualStyleBackColor=false,BackColor=Surface,ForeColor=Ink};r.SetBounds(i*222,44,207,55);r.FlatAppearance.BorderColor=Line;r.FlatAppearance.CheckedBackColor=Amber;
            r.CheckedChanged+=delegate{r.ForeColor=r.Checked?Background:Ink;if(r.Checked)store=name;};p.Controls.Add(r);stores.Add(r);
        }
        stores[0].Checked=true;
        AddLabel(p,"GAME LANGUAGE",0,116,600,22,9,Amber,true);
        var languages=new Panel();languages.SetBounds(0,147,651,38);p.Controls.Add(languages);
        // English (default) or Other languages (generic localization from the game's own translation files). The engine still accepts Language=Auto/Russian from the command line.
        for(int i=0;i<2;i++) {
            string choice=new[]{"English","Localized"}[i];var r=new RadioButton{Text=new[]{"English","Other languages"}[i],Appearance=Appearance.Button,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleCenter,UseVisualStyleBackColor=false,BackColor=Surface,ForeColor=Ink};r.SetBounds(i*222,0,207,38);r.FlatAppearance.BorderColor=Line;r.FlatAppearance.CheckedBackColor=Amber;
            r.CheckedChanged+=delegate{r.ForeColor=r.Checked?Background:Ink;if(r.Checked)languageChoice=choice;};languages.Controls.Add(r);if(i==0)r.Checked=true;
        }
        AddLabel(p,"DISPLAY RESOLUTION",0,196,600,22,9,Amber,true);
        width.Minimum=640;width.Maximum=16384;width.Value=Math.Max(640,Screen.PrimaryScreen.Bounds.Width);height.Minimum=480;height.Maximum=16384;height.Value=Math.Max(480,Screen.PrimaryScreen.Bounds.Height);
        foreach(var n in new[]{width,height}){n.BackColor=Surface;n.ForeColor=Ink;n.Font=new Font("Segoe UI",13);p.Controls.Add(n);}width.SetBounds(0,230,165,36);height.SetBounds(210,230,165,36);AddLabel(p,"×",178,227,25,40,16,Muted,false);
        AddLabel(p,"Detected from your primary display. You can change these values.",0,274,660,34,10,Muted,false);
        Feature(p,windowed,"Start in windowed mode",307,false);
        var card=new Panel{BackColor=Surface};card.SetBounds(0,352,670,125);p.Controls.Add(card);
        AddLabel(card,"Before you begin",20,16,620,25,12,Amber,true);
        AddLabel(card,"Install the game with all DLC. Required software is checked next.\r\nEpic users: apply the official Epic Games Patcher first.\r\nAny language pack installed through your store works (mods stay English).",20,52,625,85,10,Ink,false);
    }
    void BuildLocationsPage() {
        var p=Page();Folder(p,"Existing game folder",game,8);AddLabel(p,"Choose the folder containing FalloutNV.exe, not its Data folder.",0,82,670,32,10,Muted,false);
        detected.SetBounds(0,112,670,28);detected.UseMnemonic=false;detected.BackColor=Color.Transparent;detected.ForeColor=Muted;detected.LinkColor=Amber;detected.ActiveLinkColor=Ink;detected.LinkClicked+=delegate{CycleDetected();};p.Controls.Add(detected);
        Folder(p,"New mod setup folder",dest,154);AddLabel(p,"A new empty folder for Mod Organizer, mods, profiles and downloads.\r\nKeep it outside the game and installer folders. Allow about 24 GiB free.",0,229,670,60,10,Muted,false);
        var card=new Panel{BackColor=Surface};card.SetBounds(0,337,670,99);p.Controls.Add(card);AddLabel(card,"Your setup stays together",20,15,620,27,12,Amber,true);AddLabel(card,"All paths are configured from these two locations.",20,51,625,35,10,Ink,false);
    }
    void BuildRuntimePage() {
        var p=Page();AddLabel(p,"Required software is checked on every installation.",0,5,670,35,12,Amber,true);
        runtimeInfo.SetBounds(0,49,670,136);runtimeInfo.Text="Checking Microsoft runtimes…";runtimeInfo.ForeColor=Ink;p.Controls.Add(runtimeInfo);
        var recheck=new Button();StyleButton(recheck,"Recheck",130,false);recheck.SetBounds(0,194,130,38);p.Controls.Add(recheck);recheck.Click+=async delegate{await RefreshRuntimes();};
        var install=new Button();StyleButton(install,"Install missing now",205,true);install.SetBounds(146,194,205,38);p.Controls.Add(install);install.Click+=async delegate{runtimeBusy=true;body.Enabled=back.Enabled=next.Enabled=cancel.Enabled=false;try{await Task.Run(()=>PrivilegedBatch.Perform(engine.PackageFolder,null,null,Prerequisites.Plan(engine.PackageFolder,true),false,s=>{},s=>{}));}catch(Exception ex){MessageBox.Show(this,ex.Message,"Required software");}finally{runtimeBusy=false;body.Enabled=back.Enabled=next.Enabled=cancel.Enabled=true;}await RefreshRuntimes();};
        Feature(p,installRuntimes,"Install missing required software",253,true);
        AddLabel(p,"Already-installed runtimes are kept. Missing ones use the supplied Microsoft installers and may require Windows approval or a restart. xNVSE and the 4GB patch remain part of the framework installation.",27,291,640,77,10,Muted,false);
        firstRunInfo.SetBounds(0,373,670,66);firstRunInfo.ForeColor=Muted;p.Controls.Add(firstRunInfo);
        var launcher=new LinkLabel{Text="Open the original game launcher (optional first-run setup)",Left=0,Top=447,Width=670,Height=29,LinkColor=Amber};launcher.LinkClicked+=delegate{try{Process.Start(new ProcessStartInfo(Path.Combine(Engine.Full(game.Text),"FalloutNVLauncher.exe")){WorkingDirectory=game.Text,UseShellExecute=true});}catch(Exception ex){MessageBox.Show(this,ex.Message,"Game launcher");}};p.Controls.Add(launcher);
    }
    async Task RefreshRuntimes() {
        try{var values=await Task.Run(()=>Prerequisites.Inspect());if(IsDisposed)return;runtimeInfo.Text=String.Join("\r\n",values.Select(v=>(v.Ready?"Ready   ":"Needed   ")+v.Title));firstRunInfo.Text=LaunchSupport.FirstRunStatus();}catch(Exception ex){if(!IsDisposed)runtimeInfo.Text=ex.Message;}
    }
    void BuildDefenderPage() {
        var p=Page();AddLabel(p,"Recommended compatibility step",0,8,670,30,12,Amber,true);
        AddLabel(p,"MO2 and xNVSE load code into the game to supply mods. Antivirus software can sometimes block those files. An exclusion may be required after a confirmed false positive; it is not a normal prerequisite.",0,54,670,88,11,Ink,false);
        Feature(p,defender,"Add limited Microsoft Defender file exclusions (recommended)",150,true);
        AddLabel(p,"Recommended: xNVSE changes how the game process loads code, and Defender can block or quarantine these files. Excluded files are no longer scanned by Defender. Only these eight files are covered; entire folders, other antivirus products, SmartScreen and Controlled folder access are not changed.",27,191,643,101,10,Muted,false);
        AddLabel(p,"Game: FalloutNV.exe, nvse_loader.exe, nvse_1_4.dll, nvse_steam_loader.dll\r\nMO2: ModOrganizer.exe, helper.exe, usvfs_x86.dll, usvfs_x64.dll",0,309,670,66,10,Ink,false);
        defenderInfo.SetBounds(0,379,670,39);defenderInfo.ForeColor=Muted;defenderInfo.Text="Windows asks for approval once, right after you press Install. If Windows or another antivirus refuses, this step is skipped and the installation continues.";p.Controls.Add(defenderInfo);
        // Look at Defender once the window exists. If it is not the active antivirus the exclusions are switched off (they can't work), but never forced.
        bool checkedDefender=false;
        HandleCreated+=async delegate{
            if(checkedDefender)return;checkedDefender=true;
            try {
                string status=await Task.Run(()=>DefenderSupport.Status());if(IsDisposed)return;
                if(status.StartsWith("Microsoft Defender is active")){defenderInfo.Text=status+" Windows asks for approval once, right after you press Install. If Windows refuses, this step is skipped and the installation continues.";}
                else{defender.Checked=false;defenderInfo.Text=status+" The exclusions are switched off for you. You can skip this step; the guide explains what to do if another antivirus blocks the game.";}
            }catch{}
        };
        AddLabel(p,"Uninstall removes only exclusions added by this installer. Existing exclusions are retained. Review Protection history before trusting a blocked file.",0,425,670,46,10,Muted,false);
    }
    void BuildExtrasPage() {
        var p=Page();Feature(p,localSaves,"Keep saves inside the MO2 profile",0,true);saveInfo.SetBounds(27,34,645,45);saveInfo.ForeColor=Muted;p.Controls.Add(saveInfo);
        importSaves.Text="Copy existing saves into the new profile (keep originals)";importSaves.SetBounds(27,82,630,26);importSaves.ForeColor=Ink;p.Controls.Add(importSaves);TextField(importPath);importPath.SetBounds(27,116,536,28);importPath.Enabled=false;p.Controls.Add(importPath);
        var browse=new Button();StyleButton(browse,"Browse",90,false);browse.SetBounds(577,109,90,36);browse.Enabled=false;p.Controls.Add(browse);browse.Click+=delegate{using(var f=new FolderBrowserDialog{Description="Existing Fallout New Vegas saves"})if(f.ShowDialog(this)==DialogResult.OK)importPath.Text=f.SelectedPath;};
        localSaves.CheckedChanged+=delegate{importSaves.Enabled=localSaves.Checked;if(!localSaves.Checked)importSaves.Checked=false;UpdateSaveInfo();};importSaves.CheckedChanged+=delegate{importPath.Enabled=browse.Enabled=importSaves.Checked;};
        Feature(p,desktop,"Create three desktop shortcuts",170,true);Feature(p,startMenu,"Create a Start menu folder",214,true);
        AddLabel(p,"Play the modded game, open MO2, and uninstall the framework.",27,246,645,28,9,Muted,false);
        var card=new Panel{BackColor=Surface};card.SetBounds(0,302,670,144);p.Controls.Add(card);
        AddLabel(card,"Launch your way",20,16,620,27,12,Amber,true);
        AddLabel(card,"Working shortcuts are added to both the game and mod folders.\r\nGECK and FNVEdit are available in Mod Organizer.\r\nManual Steam launch instructions are included in the mod folder.",20,54,630,78,10,Ink,false);

    }
    void BuildReviewPage() {
        var p=Page();review.Dock=DockStyle.Fill;review.Font=new Font("Segoe UI",10);review.ForeColor=Ink;p.Controls.Add(review);
    }
    void BuildProgressPage() {
        // Top to bottom: progress bar, its percentage, the description of the current step, then the log.
        var p=Page();progress.SetBounds(0,15,670,15);progress.Style=ProgressBarStyle.Continuous;progress.Maximum=10000;p.Controls.Add(progress);
        percent.SetBounds(0,34,670,24);percent.UseMnemonic=false;percent.BackColor=Color.Transparent;percent.ForeColor=Amber;percent.Font=new Font("Segoe UI",11,FontStyle.Bold);percent.Text="00.00%";p.Controls.Add(percent);
        status.SetBounds(0,64,670,58);status.ForeColor=Ink;p.Controls.Add(status);
        log.SetBounds(0,127,670,345);log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;TextField(log);log.Font=new Font("Consolas",9);p.Controls.Add(log);
    }
    void BuildCompletePage() {
        var p=Page();AddLabel(p,"Your modded Mojave is ready.",0,16,680,45,20,Amber,true);AddLabel(p,"Start with \""+Integrations.PlayName+"\" in the mod folder or use your selected shortcuts.\r\nThe game shortcut starts this installation’s Default profile.",0,79,670,65,11,Ink,false);
        var play=new Button();StyleButton(play,"Play New Vegas",210,true);play.SetBounds(0,180,210,44);play.Click+=delegate{Launch(true);};p.Controls.Add(play);
        var manage=new Button();StyleButton(manage,"Manage mods",190,false);manage.SetBounds(227,180,190,44);manage.Click+=delegate{Launch(false);};p.Controls.Add(manage);
        var folder=new Button();StyleButton(folder,"Open setup folder",210,false);folder.SetBounds(0,251,210,44);folder.Click+=delegate{Process.Start(new ProcessStartInfo(dest.Text){UseShellExecute=true});};p.Controls.Add(folder);
        AddLabel(p,"Keep the _setup folder: it contains verified backups and the uninstall tool.\r\nWhat changed in this version: see V1.9.2-NOTES.txt beside the installer.",0,340,680,68,10,Muted,false);
    }
    void Launch(bool play) {
        try{LaunchSupport.Launch(dest.Text,play);}catch(Exception ex){MessageBox.Show(this,ex.Message,"Launch",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
    void UpdateSaveInfo(){saveInfo.Text=localSaves.Checked?"Save location: "+Path.Combine(String.IsNullOrWhiteSpace(dest.Text)?"[mod setup folder]":dest.Text,"profiles","Default","saves"):"Use the game’s standard Documents saves folder.";}
    Options CurrentOptions(){return new Options{Game=game.Text,Destination=dest.Text,Store=store,Language=languageChoice,Width=(int)width.Value,Height=(int)height.Value,Windowed=windowed.Checked,InstallRuntimes=installRuntimes.Checked,DefenderExclusions=defender.Checked,LocalSaves=localSaves.Checked,DesktopShortcut=desktop.Checked,StartMenuShortcut=startMenu.Checked,ImportSaves=importSaves.Checked?importPath.Text:""};}
    // Review wording for the three language choices.
    static string LanguageText(string choice){return choice=="English"?"English game language (no translation)":choice=="Localized"?"Other language (the game's own translation)":"Language detected automatically";}
    void Review() {
        var o=CurrentOptions();review.Text="GAME & DISPLAY\r\n"+o.Store+"  ·  "+o.Width+" × "+o.Height+"  ·  "+(o.Windowed?"Windowed":"Fullscreen")+"  ·  "+LanguageText(o.Language)+" + all DLC\r\n\r\nLOCATIONS\r\nGame: "+o.Game+"\r\nMod setup: "+o.Destination+"\r\n\r\nEXTRAS\r\n"+(o.LocalSaves?"Profile-local saves":"Standard Documents saves")+(String.IsNullOrWhiteSpace(o.ImportSaves)?"":"; copy existing saves")+"\r\nThree desktop shortcuts: "+(o.DesktopShortcut?"Yes":"No")+"   ·   Start menu folder: "+(o.StartMenuShortcut?"Yes":"No")+"\r\nMissing runtimes: "+(o.InstallRuntimes?"Install automatically":"Check only; stop if missing")+"\r\nDefender file exclusions: "+(o.DefenderExclusions?"Add eight scoped exclusions":"Off")+"\r\nWindows approval: "+(o.InstallRuntimes||o.DefenderExclusions?"asked ONCE, right after you press Install (for missing required software and the Defender exclusions). Approve it and you can leave: nothing else needs you.":"not needed")+"\r\nGECK and FNVEdit included"+"\r\n\r\nInstallation automatically verifies the game and package, installs the saved\r\nmod setup, and backs up changed game files. No separate verification step\r\nis needed. Close the selected game and Mod Organizer before continuing.";
    }
    void ShowPage(int index) {
        page=index;for(int i=0;i<pages.Count;i++)pages[i].Visible=i==index;pages[index].BringToFront();
        string[] titles={"Make it yours.","Choose your folders.","Ready the essentials.","Check your protection.","The finishing touches.","Ready to install?","Installing your setup.","Welcome to the Mojave."};
        string[] subs={"Choose your store and the resolution you want to play at.","Connect the existing game to a fresh, portable mod setup.","First launch or returning player: required runtimes are checked.","Read why exclusions may help, then choose whether to add them.","Choose shortcuts and where your saves live.","Review your choices. Verification runs automatically.","Preparing software, verifying files, and keeping a backup.","The saved setup is installed and configured."};
        title.Text=titles[index];subtitle.Text=subs[index];stepCounter.Text=index<ProgressPage?"STEP "+(index+1)+" OF 6":"";
        for(int i=0;i<steps.Count;i++)steps[i].ForeColor=i==Math.Min(index,ReviewPage)?Amber:Muted;
        UpdateButtons();
        if(index==ReviewPage)Review();if(index==2){var pending=RefreshRuntimes();}
    }
    // Questions pages: Close, Back, Continue. While a run is going: only Cancel. Once it has stopped (done, cancelled or failed): only Finish.
    void UpdateButtons() {
        bool running=busy&&page==ProgressPage,ended=page==CompletePage||page==ProgressPage&&!busy;
        cancel.Visible=!ended;cancel.Text=running?"Cancel":"Close";
        back.Visible=page>0&&page<ProgressPage;
        next.Visible=!running;next.Enabled=true;next.Text=page==ReviewPage?"Install setup":ended?"Finish":"Continue";
    }
    async Task Continue() {
        try {
            if(page==CompletePage){CatEncounter.Maybe(this,true);Close();return;}
            if(page==ProgressPage){Close();return;}
            if(page==1) {
                game.Text=Engine.Full(game.Text);dest.Text=Engine.Full(dest.Text);
                if(!File.Exists(Path.Combine(game.Text,"FalloutNV.exe")))throw new Exception("Choose the game folder containing FalloutNV.exe.");
                if(Engine.Within(dest.Text,game.Text)||Engine.Within(game.Text,dest.Text)||Engine.Within(dest.Text,engine.PackageFolder)||Engine.Within(engine.PackageFolder,dest.Text))throw new Exception("Choose separate game, mod setup and installer folders.");
                if(File.Exists(dest.Text)||(Directory.Exists(dest.Text)&&Directory.EnumerateFileSystemEntries(dest.Text).Any()))throw new Exception("Choose a new empty mod setup folder.");
            }
            if(page==4){if(importSaves.Checked&&String.IsNullOrWhiteSpace(importPath.Text))throw new Exception("Choose the saves folder to copy, or uncheck that option.");Integrations.Preflight(CurrentOptions());}
            if(page<ReviewPage){ShowPage(page+1);return;}
            var o=CurrentOptions();await Work(delegate{engine.Install(o);},true);
        }catch(Exception ex){MessageBox.Show(this,ex.Message,"Fallout Mod Setup",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
    async Task Work(Action action,bool installing) {
        busy=true;cancellation=new CancellationTokenSource();engine.Cancel=cancellation.Token;ShowPage(ProgressPage);cancel.Enabled=true;nav.Enabled=false;log.Clear();ResetPercent();
        try{await Task.Run(action);complete=installing;if(installing)ShowPage(CompletePage);else{MessageBox.Show(this,"The installation’s game changes and optional integrations have been restored. Mods and saves were kept.","Restore complete");ShowPage(0);}}
        catch(Exception ex){log.AppendText(ex.Message+Environment.NewLine);status.Text="Stopped. See the details below.";MessageBox.Show(this,ex.Message,"Fallout Mod Setup",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        finally{busy=false;cancellation.Dispose();cancellation=null;engine.Cancel=CancellationToken.None;cancel.Enabled=true;nav.Enabled=true;UpdateButtons();}
    }
    async Task RestoreInstallation() {
        using(var f=new FolderBrowserDialog{Description="Select the installed Mod Organizer folder containing _setup",SelectedPath=dest.Text})if(f.ShowDialog(this)==DialogResult.OK) {
            string p=f.SelectedPath;if(MessageBox.Show(this,"Restore the game-file changes and installer-created shortcuts/Steam launch options for this installation? Mods and saves will stay in place. Exit Steam first if its launch options were configured.","Restore installation",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)await Work(delegate{engine.Restore(p);},false);
        }
    }
    public void RenderPreview(int index,string output) {
        if(index<0||index>=pages.Count)throw new Exception("Invalid preview page.");ShowPage(index);if(index==2){runtimeInfo.Text=String.Join("\r\n",Prerequisites.Inspect().Select(v=>(v.Ready?"Ready   ":"Needed   ")+v.Title));firstRunInfo.Text=LaunchSupport.FirstRunStatus();}PerformLayout();shell.Font=Font;shell.ForeColor=Ink;Controls.Remove(shell);shell.Dock=DockStyle.None;shell.Size=ClientSize;shell.CreateControl();LayoutAll(shell);
        using(var b=new Bitmap(shell.Width,shell.Height)){shell.DrawToBitmap(b,new Rectangle(Point.Empty,shell.Size));b.Save(output);}shell.Dispose();
    }
    static void LayoutAll(Control c){c.PerformLayout();foreach(Control child in c.Controls){child.CreateControl();LayoutAll(child);}}
    void SelectStore(string name) {
        var radio=stores.FirstOrDefault(r=>r.Text==name);if(radio!=null)radio.Checked=true;
    }
    // The store files in the chosen folder decide the store, so a GOG folder is never installed as Steam.
    void AutoStore() {
        string detectedStore=GameLocator.StoreFromFolder(game.Text);if(detectedStore!=null)SelectStore(detectedStore);
    }
    void UseCandidate(int index) {
        foundIndex=index;game.Text=found[index].Path;if(found[index].Store!=null)SelectStore(found[index].Store);
    }
    void UpdateDetected() {
        detected.Links.Clear();
        if(found.Count==0){detected.Text="No installed copy was found automatically. Use Browse to choose your game folder.";return;}
        bool current=String.Equals(game.Text,found[foundIndex].Path,StringComparison.OrdinalIgnoreCase);
        detected.Text=current?"Detected "+(found[foundIndex].Store??"game")+" installation"+(found.Count>1?" ("+found.Count+" found, click to try the next one)":""):"Click to use the detected installation: "+found[foundIndex].Path;
        if(!current||found.Count>1)detected.Links.Add(0,detected.Text.Length);
    }
    void CycleDetected() {
        if(found.Count==0)return;
        if(String.Equals(game.Text,found[foundIndex].Path,StringComparison.OrdinalIgnoreCase)&&found.Count>1)foundIndex=(foundIndex+1)%found.Count;
        UseCandidate(foundIndex);
    }
}
}
