using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Reflection;
using FalloutModSetup;
class WizardChecks {
 static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(var x in All(child))yield return x;}
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
 static void Pass(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
 // The cat encounter: chance logic, window, close box, resource, and the Finish paths.
 static void CatChecks(){
  var oldDice=CatEncounter.Dice;var oldShow=CatEncounter.Show;int rolls=0,shown=0;
  try{
   CatEncounter.Dice=()=>{rolls++;return 0;};
   Pass(CatEncounter.Chance==12,"the cat has a 1 in 12 chance");
   Pass(CatEncounter.ShouldAppear(true,false),"a successful run can meet the cat when the dice say 0");
   rolls=0;Pass(!CatEncounter.ShouldAppear(false,false)&&!CatEncounter.ShouldAppear(true,true)&&rolls==0,"never after a failure or cancel and never in quiet/CLI mode (the dice are not even rolled)");
   CatEncounter.Dice=()=>5;Pass(!CatEncounter.ShouldAppear(true,false),"any other dice value means no cat");
   CatEncounter.Dice=oldDice;int hits=0;for(int i=0;i<12000;i++)if(CatEncounter.Dice()==0)hits++;
   Pass(hits>700&&hits<1300,"the real dice give about one cat in twelve ("+hits+" of 12000)");
   CatEncounter.Dice=()=>0;CatEncounter.Show=o=>shown++;
   CatEncounter.Maybe(null,false);CatEncounter.Maybe(null,true,true);Pass(shown==0,"Maybe shows nothing for failure or quiet runs");
   CatEncounter.Maybe(null,true);Pass(shown==1,"Maybe shows the cat after a successful run");
   CatEncounter.Show=o=>{throw new InvalidOperationException("cat broke");};CatEncounter.Maybe(null,true);Pass(true,"a cat that throws is ignored");
   // window
   using(var cat=new CatForm()) {
    Pass(cat.Text=="Warning"&&cat.MessageText=="A wild cowboy cat appeared.","the window is titled Warning and says A wild cowboy cat appeared.");
    Pass(cat.ButtonTexts.SequenceEqual(new[]{"ok","uhu...","noice"}),"the three buttons read exactly ok, uhu..., noice");
    Pass(cat.FormBorderStyle==FormBorderStyle.None&&!cat.ControlBox&&!cat.MinimizeBox&&!cat.MaximizeBox&&!cat.CloseBoxEnabled,"no minimize or maximize and the close box is drawn greyed out (no real close button)");
    Pass(cat.Picture!=null&&cat.Picture.PixelFormat==System.Drawing.Imaging.PixelFormat.Format32bppArgb&&cat.Picture.Width>=250&&cat.Picture.Width<=400,"the cat picture is embedded, about 300 px wide, with an alpha channel");
    Pass(cat.Picture.GetPixel(2,2).A==0&&cat.Picture.GetPixel(cat.Picture.Width/2,cat.Picture.Height/2).A==255,"the picture's background is transparent and the kitten opaque");
    cat.Opacity=0;cat.Show();Application.DoEvents();
    var hit=cat.CloseBoxBounds;Func<int,int,int> Lp=(x,y)=>{var s=cat.PointToScreen(new System.Drawing.Point(x,y));return (y2s(s.Y)<<16)|(s.X&0xFFFF);};
    int onClose=(int)SendMessage(cat.Handle,0x0084,IntPtr.Zero,(IntPtr)Lp(hit.X+10,hit.Y+10));
    Pass(onClose==1,"the greyed close box has no hit-test of its own (it is not a close button)");
    SendMessage(cat.Handle,0x0112,(IntPtr)0xF060,IntPtr.Zero);Application.DoEvents();
    Pass(!cat.IsDisposed&&cat.Visible,"the system close command (Alt+F4, taskbar, title menu) is ignored");
    cat.Close();Application.DoEvents();Pass(!cat.IsDisposed&&cat.Visible,"closing the window other than through a button does nothing");
    var keys=typeof(Form).GetMethod("ProcessCmdKey",BindingFlags.NonPublic|BindingFlags.Instance);var msg=new Message();
    Pass((bool)keys.Invoke(cat,new object[]{msg,Keys.Escape})&&(bool)keys.Invoke(cat,new object[]{msg,Keys.Alt|Keys.F4}),"Escape and Alt+F4 are swallowed");
    cat.Buttons[2].PerformClick();Application.DoEvents();Pass(cat.IsDisposed||!cat.Visible,"a button closes the window");
   }
   foreach(int i in new[]{0,1}){using(var cat=new CatForm()){cat.Opacity=0;cat.Show();cat.Buttons[i].PerformClick();Application.DoEvents();Pass(cat.IsDisposed||!cat.Visible,"button "+cat.ButtonTexts[i]+" closes the window");}}
   // Finish paths: a successful finish may show the cat; a throwing cat never keeps the window open; a stopped run never shows it.
   shown=0;CatEncounter.Dice=()=>0;CatEncounter.Show=o=>shown++;
   Func<int,SetupForm> finishAt=pageIndex=>{var f=new SetupForm(new Engine());typeof(SetupForm).GetMethod("ShowPage",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f,new object[]{pageIndex});((System.Threading.Tasks.Task)typeof(SetupForm).GetMethod("Continue",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f,null)).Wait();return f;};
   using(var f=finishAt(7))Pass(shown==1&&f.IsDisposed,"Finish after a completed installation can show the cat and then closes");
   shown=0;using(var f=finishAt(6))Pass(shown==0&&f.IsDisposed,"Finish after a failed or cancelled run never shows the cat");
   CatEncounter.Show=o=>{throw new InvalidOperationException("cat broke");};
   using(var f=finishAt(7))Pass(f.IsDisposed,"Finish still closes normally when the cat code throws");
   // uninstaller Finish
   CatEncounter.Show=o=>shown++;shown=0;
   string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"fnv-cat-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(dir);
   try{using(var u=new UninstallForm(dir)){typeof(UninstallForm).GetField("finished",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(u,true);typeof(UninstallForm).GetMethod("FinishNow",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(u,null);Pass(shown==1&&(u.IsDisposed||u.WasClosed),"the uninstaller's Finish can show the cat and then closes");}
    shown=0;using(var u=new UninstallForm(dir)){typeof(UninstallForm).GetMethod("FinishNow",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(u,null);Pass(shown==0&&(u.IsDisposed||u.WasClosed),"the uninstaller never shows the cat unless its removal finished");}
    CatEncounter.Show=o=>{throw new InvalidOperationException("cat broke");};
    using(var u=new UninstallForm(dir)){typeof(UninstallForm).GetField("finished",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(u,true);typeof(UninstallForm).GetMethod("FinishNow",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(u,null);Pass(u.IsDisposed||u.WasClosed,"the uninstaller still closes when the cat code throws");}
   }finally{try{System.IO.Directory.Delete(dir,true);}catch{}}
  }finally{CatEncounter.Dice=oldDice;CatEncounter.Show=oldShow;}
 }
 static int y2s(int y){return y&0xFFFF;} [STAThread] static void Main(){Application.EnableVisualStyles();using(var form=new SetupForm(new Engine())){
  { var fresh=(Options)typeof(SetupForm).GetMethod("CurrentOptions",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);var enRadio=All(form).OfType<RadioButton>().Single(x=>x.Text=="English");if(fresh.Language!="English"||!enRadio.Checked)throw new Exception("English must be preselected");Console.WriteLine("PASS English is the preselected game language"); }
  var radios=All(form).OfType<RadioButton>().ToList();radios.Single(x=>x.Text=="GOG").Checked=true;radios.Single(x=>x.Text=="Other languages").Checked=true;
  var options=(Options)typeof(SetupForm).GetMethod("CurrentOptions",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
  if(options.Store!="GOG"||options.Language!="Localized")throw new Exception("Store/language choices interfere");Console.WriteLine("PASS store and language choices stay independent");
  var langRadios=All(form).OfType<RadioButton>().Where(x=>new[]{"Auto detect","English","Other languages","Russian","Other language"}.Contains(x.Text)).ToList();
  if(!langRadios.Select(x=>x.Text).SequenceEqual(new[]{"English","Other languages"}))throw new Exception("The language chooser must offer exactly English and Other languages: "+String.Join(", ",langRadios.Select(x=>x.Text)));
  Console.WriteLine("PASS the language chooser offers exactly English and Other languages (no Auto detect, no Russian radio)");
  var langBy=new System.Collections.Generic.Dictionary<string,string>{{"English","English"},{"Other languages","Localized"}};
  foreach(var lr in langRadios){lr.Checked=true;var lo=(Options)typeof(SetupForm).GetMethod("CurrentOptions",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);if(lo.Language!=langBy[lr.Text])throw new Exception("Language option "+lr.Text+" maps to "+lo.Language);}
  Console.WriteLine("PASS the options map to English and Localized (generic localization)");
  langRadios.Single(x=>x.Text=="English").Checked=true;if(!langRadios.Single(x=>x.Text=="English").Checked||langRadios.Any(x=>x.Text!="English"&&x.Checked))throw new Exception("English must be selectable alone");
  langRadios.Single(x=>x.Text=="Other languages").Checked=true;
  var windowedBox=All(form).OfType<CheckBox>().Single(x=>x.Text=="Start in windowed mode");
  if(windowedBox.Checked||options.Windowed)throw new Exception("Windowed box must start unchecked");Console.WriteLine("PASS the windowed-mode box is unchecked by default (fullscreen)");
  windowedBox.Checked=true;
  options=(Options)typeof(SetupForm).GetMethod("CurrentOptions",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
  if(!options.Windowed)throw new Exception("Windowed selection lost");Console.WriteLine("PASS ticking the windowed box reaches installation options");
  windowedBox.Checked=false;
  options=(Options)typeof(SetupForm).GetMethod("CurrentOptions",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
  if(options.Windowed)throw new Exception("Fullscreen selection lost");Console.WriteLine("PASS fullscreen choice reaches installation options");
  if(!new Options().Windowed)throw new Exception("Engine/CLI windowed default changed");Console.WriteLine("PASS the engine and command-line default stays windowed (scripts unchanged)");
  if(form.Icon==null||!form.ShowIcon||!form.ShowInTaskbar)throw new Exception("Installer window icon missing");
  using(var s=typeof(Engine).Assembly.GetManifestResourceStream("Lucky38.ico"))using(var expected=new System.Drawing.Icon(s))if(form.Icon.Width!=expected.Width||form.Icon.Height!=expected.Height)throw new Exception("Installer window icon is not Lucky38.ico");
  Console.WriteLine("PASS the installer window shows the embedded Lucky38 icon in the title bar and taskbar");
  typeof(SetupForm).GetMethod("Review",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);var review=(Label)typeof(SetupForm).GetField("review",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
  if(!review.Text.Contains("Fullscreen")||!review.Text.Contains("Other language")||review.Text.Contains("English game")||review.Text.Contains("Russian"))throw new Exception("Review language incorrect");Console.WriteLine("PASS review reflects the chosen language");
  if(!review.Text.Contains("ONCE, right after you press Install")||!review.Text.Contains("you can leave"))throw new Exception("Review must say that Windows asks once");Console.WriteLine("PASS the review page says Windows approval is asked once, right after Install, and that the user can leave");
  var cancelBtn=(Button)typeof(SetupForm).GetField("cancel",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);var nextBtn=(Button)typeof(SetupForm).GetField("next",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);var backBtn=(Button)typeof(SetupForm).GetField("back",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
  var showPage=typeof(SetupForm).GetMethod("ShowPage",BindingFlags.NonPublic|BindingFlags.Instance);var busyField=typeof(SetupForm).GetField("busy",BindingFlags.NonPublic|BindingFlags.Instance);
  form.Opacity=0;form.Show();Application.DoEvents();
  showPage.Invoke(form,new object[]{5});if(!(cancelBtn.Visible&&cancelBtn.Text=="Close"&&nextBtn.Visible&&nextBtn.Text=="Install setup"&&backBtn.Visible))throw new Exception("Review page buttons");
  busyField.SetValue(form,true);showPage.Invoke(form,new object[]{6});
  if(!(cancelBtn.Visible&&cancelBtn.Text=="Cancel"&&!nextBtn.Visible&&!backBtn.Visible))throw new Exception("While installing only Cancel may show");Console.WriteLine("PASS while the installation runs only a Cancel button is shown");
  busyField.SetValue(form,false);showPage.Invoke(form,new object[]{6});
  if(!(!cancelBtn.Visible&&nextBtn.Visible&&nextBtn.Enabled&&nextBtn.Text=="Finish"&&!backBtn.Visible))throw new Exception("After a stop (cancelled or failed) only Finish may show");Console.WriteLine("PASS after a cancelled or failed run exactly one Finish button is shown");
  busyField.SetValue(form,true);showPage.Invoke(form,new object[]{7});busyField.SetValue(form,false);
  if(!(!cancelBtn.Visible&&nextBtn.Visible&&nextBtn.Text=="Finish"&&!backBtn.Visible))throw new Exception("Completed page buttons");Console.WriteLine("PASS the completed page shows exactly one Finish button");
  showPage.Invoke(form,new object[]{5});
  if(All(form).Any(x=>x.Text.Contains("Enable Steam Play")||x.Text.Contains("Personal Mod Setup")))throw new Exception("Stale UI");Console.WriteLine("PASS removed Steam automation and old branding absent");
  var report=typeof(SetupForm).GetMethod("ReportProgress",BindingFlags.NonPublic|BindingFlags.Instance);var percent=(Label)typeof(SetupForm).GetField("percent",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);var bar=(ProgressBar)typeof(SetupForm).GetField("progress",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
  var show=typeof(SetupForm).GetMethod("ShowPercent",BindingFlags.NonPublic|BindingFlags.Instance);var reset=typeof(SetupForm).GetMethod("ResetPercent",BindingFlags.NonPublic|BindingFlags.Instance);
  var statusLabel=(Label)typeof(SetupForm).GetField("status",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);var logBox=(TextBox)typeof(SetupForm).GetField("log",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
  if(!(bar.Bottom<=percent.Top&&percent.Bottom<=statusLabel.Top&&statusLabel.Bottom<=logBox.Top&&bar.Parent==percent.Parent&&percent.Parent==statusLabel.Parent))throw new Exception("Progress page order is wrong");Console.WriteLine("PASS the progress page reads top to bottom: bar, percentage, description, log (no overlap)");
  if(percent.Text!="00.00%"||!All(form).Contains(percent))throw new Exception("Percentage label missing");Console.WriteLine("PASS a percentage label sits under the progress bar and starts at 00.00%");
  report.Invoke(form,new object[]{"Installing files: 100 / 400",37});
  if(percent.Text!="37.00%"||bar.Value!=3700||percent.Top<=bar.Top+bar.Height-1)throw new Exception("Percentage does not follow the bar");Console.WriteLine("PASS the percentage follows the bar and is placed below it");
  show.Invoke(form,new object[]{37.256});if(percent.Text!="37.26%"||bar.Value!=3726)throw new Exception("Decimals missing");Console.WriteLine("PASS exact values show two decimals and move the bar in finer steps");
  show.Invoke(form,new object[]{5.5});report.Invoke(form,new object[]{"Verifying data part 2 of 17",12});if(percent.Text!="37.26%")throw new Exception("Percentage moved backwards");Console.WriteLine("PASS the percentage never moves backwards during an installation");
  show.Invoke(form,new object[]{4.07});reset.Invoke(form,null);show.Invoke(form,new object[]{4.07});if(percent.Text!="04.07%")throw new Exception("Zero padding wrong");Console.WriteLine("PASS values below 10 are zero-padded (04.07%) and a new installation starts from zero");
  report.Invoke(form,new object[]{"Done",150});if(percent.Text!="100.00%"||bar.Value!=10000)throw new Exception("Percentage above 100");
  reset.Invoke(form,null);report.Invoke(form,new object[]{"Start",-5});
  if(percent.Text!="00.00%"||bar.Value!=0)throw new Exception("Percentage not clamped");Console.WriteLine("PASS out-of-range values are clamped to 00.00-100.00%");
  CatChecks();
 }}
}
