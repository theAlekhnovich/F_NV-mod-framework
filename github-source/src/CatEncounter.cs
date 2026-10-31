using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
namespace FalloutModSetup {
// A rare, purely cosmetic joke: a classic Windows XP style Warning box with a cowboy cat. It writes nothing, logs nothing and changes no result;
// every failure is swallowed so that closing the program is never blocked. Hidden command line: --cat [--output file.png].
public static class CatEncounter {
    public const int Chance=12;
    static readonly Random Rng=new Random();
    // The dice: 0..Chance-1. The cat appears on 0 (1 in 12). Checks replace it.
    public static Func<int> Dice=()=>Rng.Next(Chance);
    // Shows the window. Checks replace it to count or to throw.
    public static Action<IWin32Window> Show=ShowWindow;
    // Only a finished, successful installation or uninstall may roll the dice. Failures, cancelled runs and quiet (command-line) runs never do.
    public static bool ShouldAppear(bool success,bool quiet) {
        if(!success||quiet)return false;
        return Dice()==0;
    }
    // Called when the user presses Finish. Never throws.
    public static void Maybe(IWin32Window owner,bool success,bool quiet=false) {
        try{if(ShouldAppear(success,quiet))Show(owner);}catch{}
    }
    // The real window. Not shown by the automated checks (they set FNVMF_NO_CAT) and not on a session without a desktop.
    public static void ShowWindow(IWin32Window owner) {
        if(Environment.GetEnvironmentVariable("FNVMF_NO_CAT")!=null||UninstallForm.SkipDisposeInChecks||!Environment.UserInteractive)return;
        using(var form=new CatForm())form.ShowDialog(owner);
    }
    // The cut-out kitten (transparent background), embedded in the EXE.
    public static Bitmap LoadCat() {
        using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("cat-cowboy-cutout.png"))
        using(var raw=new Bitmap(s)) {
            var copy=new Bitmap(raw.Width,raw.Height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(copy)){g.Clear(Color.Transparent);g.DrawImage(raw,0,0,raw.Width,raw.Height);}
            return copy;
        }
    }
    // --cat: shows the window (or, with --output, renders it to a PNG).
    public static int Run(string output) {
        Application.EnableVisualStyles();
        using(var form=new CatForm()) {
            if(String.IsNullOrEmpty(output))form.ShowDialog();else form.Render(output);
        }
        return 0;
    }
}

// Flat XP-looking button (light gradient, dark blue border, rounded corners).
class XpButton:Button {
    bool hot,down;public bool Emphasis;
    public XpButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);Font=new Font("Tahoma",9f);}
    protected override void OnMouseEnter(EventArgs e){hot=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hot=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){down=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){down=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Parent==null?SystemColors.Control:Parent.BackColor);
        var r=new Rectangle(0,0,Width-1,Height-1);
        using(var path=Round(r,3)) {
            using(var b=new LinearGradientBrush(r,down?Color.FromArgb(206,201,186):Color.White,down?Color.White:Color.FromArgb(214,208,197),LinearGradientMode.Vertical))g.FillPath(b,path);
            using(var p=new Pen(Emphasis||Focused?Color.Black:Color.FromArgb(0,60,116),Emphasis||Focused?2f:1f))g.DrawPath(p,path);
            if(hot&&!down)using(var p=new Pen(Color.FromArgb(255,199,60),2f)){var inner=Rectangle.Inflate(r,-2,-2);using(var ip=Round(inner,2))g.DrawPath(p,ip);}
        }
        TextRenderer.DrawText(g,Text,Font,new Rectangle(0,0,Width,Height),Color.Black,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
    }
    static GraphicsPath Round(Rectangle r,int rad) {
        var p=new GraphicsPath();int d=rad*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
}

public class CatForm:Form {
    const int WM_SYSCOMMAND=0x0112,WM_NCHITTEST=0x0084,SC_CLOSE=0xF060,SC_KEYMENU=0xF100,HTCLIENT=1,HTCAPTION=2;
    const int TitleHeight=30,Frame=4;
    readonly Label message=new Label();readonly List<Button> buttons=new List<Button>();readonly PictureBox picture=new PictureBox();
    bool allowClose;Bitmap cat;
    public string MessageText{get{return message.Text;}}
    public string[] ButtonTexts{get{return buttons.Select(b=>b.Text).ToArray();}}
    public IList<Button> Buttons{get{return buttons;}}
    // The close box of this window is drawn greyed out and is not part of the window's real chrome: it has no handler and no hit-test, so it cannot be clicked.
    public bool CloseBoxEnabled{get{return false;}}
    public Rectangle CloseBoxBounds{get{return new Rectangle(ClientSize.Width-Frame-27,5,21,21);}}
    public Bitmap Picture{get{return cat;}}
    public CatForm() {
        Text="Warning";FormBorderStyle=FormBorderStyle.None;ControlBox=false;MinimizeBox=false;MaximizeBox=false;ShowIcon=false;ShowInTaskbar=false;
        StartPosition=FormStartPosition.CenterParent;ClientSize=new Size(440,300);BackColor=Color.FromArgb(236,233,216);DoubleBuffered=true;KeyPreview=true;AutoScaleMode=AutoScaleMode.None;
        message.Text="A wild cowboy cat appeared.";message.Font=new Font("Tahoma",10.5f);message.AutoSize=false;message.UseMnemonic=false;message.BackColor=Color.Transparent;message.ForeColor=Color.Black;message.SetBounds(94,52,330,26);Controls.Add(message);
        cat=CatEncounter.LoadCat();picture.Image=cat;picture.SizeMode=PictureBoxSizeMode.Zoom;picture.BackColor=Color.Transparent;picture.SetBounds(120,82,200,160);picture.TabStop=false;Controls.Add(picture);
        string[] texts={"ok","uhu...","noice"};
        for(int i=0;i<3;i++) {
            var b=new XpButton{Text=texts[i],Emphasis=i==0};b.SetBounds(49+i*114,252,100,28);b.Click+=delegate{Dismiss();};Controls.Add(b);buttons.Add(b);
        }
        using(var path=TopRounded(new Rectangle(0,0,ClientSize.Width,ClientSize.Height),9))Region=new Region(path);
    }
    // Any of the three buttons closes the window; nothing else does.
    void Dismiss(){allowClose=true;Close();}
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if(!allowClose&&e.CloseReason==CloseReason.UserClosing)e.Cancel=true;
        base.OnFormClosing(e);
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
        if(keyData==Keys.Escape||keyData==(Keys.Alt|Keys.F4))return true;
        return base.ProcessCmdKey(ref msg,keyData);
    }
    protected override void WndProc(ref Message m) {
        if(m.Msg==WM_SYSCOMMAND&&((m.WParam.ToInt64()&0xFFF0)==SC_CLOSE)){return;}
        if(m.Msg==WM_NCHITTEST) {
            // The title bar drags the window; the greyed close box does nothing.
            var p=PointToClient(new Point((short)(m.LParam.ToInt64()&0xFFFF),(short)((m.LParam.ToInt64()>>16)&0xFFFF)));
            m.Result=(IntPtr)(p.Y<TitleHeight&&!CloseBoxBounds.Contains(p)?HTCAPTION:HTCLIENT);return;
        }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing){if(disposing&&cat!=null){picture.Image=null;cat.Dispose();cat=null;}base.Dispose(disposing);}
    static GraphicsPath TopRounded(Rectangle r,int rad) {
        var p=new GraphicsPath();int d=rad*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddLine(r.Right,r.Y+rad,r.Right,r.Bottom);p.AddLine(r.Right,r.Bottom,r.X,r.Bottom);p.CloseFigure();return p;
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int w=ClientSize.Width,h=ClientSize.Height;
        // Frame and title bar (Luna blue).
        using(var b=new SolidBrush(Color.FromArgb(0,60,212)))g.FillRectangle(b,0,0,w,h);
        var bar=new Rectangle(0,0,w,TitleHeight);
        using(var b=new LinearGradientBrush(bar,Color.FromArgb(61,149,255),Color.FromArgb(0,65,200),LinearGradientMode.Vertical)){
            var blend=new ColorBlend{Positions=new[]{0f,0.12f,0.45f,1f},Colors=new[]{Color.FromArgb(94,170,255),Color.FromArgb(0,90,240),Color.FromArgb(0,84,230),Color.FromArgb(0,62,200)}};b.InterpolationColors=blend;
            using(var path=TopRounded(bar,9))g.FillPath(b,path);
        }
        using(var f=new Font("Trebuchet MS",12f,FontStyle.Bold)){
            g.DrawString(Text,f,Brushes.Black,new PointF(12,5),StringFormat.GenericDefault);
            g.DrawString(Text,f,Brushes.White,new PointF(11,4),StringFormat.GenericDefault);
        }
        // The greyed-out close box.
        var cb=CloseBoxBounds;
        using(var path=RoundedRect(cb,4)) {
            using(var b=new LinearGradientBrush(cb,Color.FromArgb(172,182,204),Color.FromArgb(132,146,176),LinearGradientMode.Vertical))g.FillPath(b,path);
            using(var p=new Pen(Color.FromArgb(222,228,240),1.5f))g.DrawPath(p,path);
        }
        using(var p=new Pen(Color.FromArgb(215,222,236),2.2f)){p.StartCap=p.EndCap=LineCap.Round;g.DrawLine(p,cb.X+6,cb.Y+6,cb.Right-7,cb.Bottom-7);g.DrawLine(p,cb.Right-7,cb.Y+6,cb.X+6,cb.Bottom-7);}
        // Body.
        using(var b=new SolidBrush(BackColor))g.FillRectangle(b,Frame,TitleHeight,w-2*Frame,h-TitleHeight-Frame);
        // Red error icon with a white cross.
        var ic=new Rectangle(30,48,44,44);
        using(var b=new SolidBrush(Color.FromArgb(60,0,0,0)))g.FillEllipse(b,ic.X+1,ic.Y+3,ic.Width,ic.Height);
        using(var b=new LinearGradientBrush(ic,Color.FromArgb(244,96,74),Color.FromArgb(168,8,8),LinearGradientMode.ForwardDiagonal))g.FillEllipse(b,ic);
        using(var p=new Pen(Color.White,2.5f))g.DrawEllipse(p,ic.X+1,ic.Y+1,ic.Width-2,ic.Height-2);
        using(var p=new Pen(Color.White,6f)){p.StartCap=p.EndCap=LineCap.Round;g.DrawLine(p,ic.X+13,ic.Y+13,ic.Right-13,ic.Bottom-13);g.DrawLine(p,ic.Right-13,ic.Y+13,ic.X+13,ic.Bottom-13);}
        using(var b=new LinearGradientBrush(new Rectangle(ic.X+6,ic.Y+3,ic.Width-12,ic.Height/2),Color.FromArgb(120,255,255,255),Color.FromArgb(0,255,255,255),LinearGradientMode.Vertical))g.FillEllipse(b,ic.X+6,ic.Y+3,ic.Width-12,ic.Height/2);
        base.OnPaint(e);
    }
    static GraphicsPath RoundedRect(Rectangle r,int rad) {
        var p=new GraphicsPath();int d=rad*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    // Preview: the window as a PNG (on a plain sky-blue desktop, like the reference picture).
    public void Render(string output) {
        StartPosition=FormStartPosition.Manual;Location=new Point(-20000,-20000);Show();Application.DoEvents();
        using(var window=new Bitmap(ClientSize.Width,ClientSize.Height))using(var shot=new Bitmap(ClientSize.Width+80,ClientSize.Height+80))using(var g=Graphics.FromImage(shot)) {
            DrawToBitmap(window,new Rectangle(Point.Empty,ClientSize));
            using(var b=new LinearGradientBrush(new Rectangle(0,0,shot.Width,shot.Height),Color.FromArgb(70,140,240),Color.FromArgb(150,200,255),LinearGradientMode.Vertical))g.FillRectangle(b,0,0,shot.Width,shot.Height);
            using(var clip=TopRounded(new Rectangle(40,40,ClientSize.Width,ClientSize.Height),9)){g.SetClip(clip);g.DrawImage(window,40,40);g.ResetClip();}
            shot.Save(output,ImageFormat.Png);
            Hide();
        }
    }
}
}
