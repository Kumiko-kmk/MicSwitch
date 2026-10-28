using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
namespace MicSwitch.Chinese
{
 internal static class Program
 {
  internal static void Log(Exception e){}
  private delegate bool Callback(IntPtr window,IntPtr state);
  [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,IntPtr state);
  [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
  [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
  [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
  [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
  [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
  [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
[DllImport("user32.dll")]private static extern bool AttachThreadInput(uint from,uint to,bool attach);
private static void Focus(IntPtr target){uint pid;uint fg=GetWindowThreadProcessId(GetForegroundWindow(),out pid),self=GetCurrentThreadId();bool attached=fg!=self&&AttachThreadInput(self,fg,true);try{ShowWindow(target,9);SetForegroundWindow(target);}finally{if(attached)AttachThreadInput(self,fg,false);}}private static List<Endpoint> devices; private static int hotkey=5; [DllImport("user32.dll")]private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra); private static void SendKey(bool down){if(hotkey==5)mouse_event(down?0x80u:0x100u,0,0,1,UIntPtr.Zero);else keybd_event((byte)hotkey,0,down?0u:2u,UIntPtr.Zero);}
  private static double Change(bool open){var sw=Stopwatch.StartNew();SendKey(open);while(sw.ElapsedMilliseconds<2000){bool done=true;foreach(var d in devices)if(d.Muted==open)done=false;if(done)return sw.Elapsed.TotalMilliseconds;Thread.Sleep(1);}throw new Exception("mute timeout");}
  [STAThread]private static int Main(string[] args){
   var lines=new List<string>();Process app=null;string dir=args[1];if(args.Length>2)hotkey=Int32.Parse(args[2]);Directory.CreateDirectory(dir);
   try{
    devices=Audio.Enumerate();File.WriteAllText(Path.Combine(dir,"settings.xml"),"<Settings><Key>"+hotkey+"</Key><Device>all</Device><KeepMuted>false</KeepMuted></Settings>");
    app=Process.Start(new ProcessStartInfo(args[0],"--profile \""+dir+"\""){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});Thread.Sleep(3000);
    IntPtr target=IntPtr.Zero;EnumWindows(delegate(IntPtr h,IntPtr p){uint pid;GetWindowThreadProcessId(h,out pid);try{using(var proc=Process.GetProcessById((int)pid)){var text=new StringBuilder(256);GetWindowText(h,text,256);if(proc.ProcessName=="Weixin"&&text.ToString()=="微信")target=h;}}catch{}return true;},IntPtr.Zero);
    if(target==IntPtr.Zero)throw new Exception("Weixin window not found");Focus(target);Thread.Sleep(300);
    if(GetForegroundWindow()!=target)throw new Exception("Weixin not foreground");
    var on=new List<double>();var off=new List<double>();
    int attempts=0;for(int i=0;i<100;i++){if(++attempts>300)throw new Exception("Cannot establish foreground consistently");if(GetForegroundWindow()!=target){Focus(target);Thread.Sleep(50);if(GetForegroundWindow()!=target){i--;continue;}}on.Add(Change(true));Thread.Sleep(25);off.Add(Change(false));Thread.Sleep(25);}
    on.Sort();off.Sort();lines.Add("PASS Weixin foreground independently confirmed for all 100 cycles");lines.Add("press_ms median="+on[50]+" p95="+on[94]+" max="+on[99]);lines.Add("release_ms median="+off[50]+" p95="+off[94]+" max="+off[99]);
    lines.Add("Priority="+app.PriorityClass+"; hotkey="+hotkey);return 0;
   }catch(Exception e){lines.Add("FAIL "+e);return 1;}
   finally{
    SendKey(false);
    if(app!=null){try{PostMessage(app.MainWindowHandle,0x10,IntPtr.Zero,IntPtr.Zero);if(!app.WaitForExit(4000)){app.Kill();app.WaitForExit();Thread.Sleep(2200);}}catch{}app.Dispose();}
    if(devices!=null)foreach(var d in devices){try{d.SetMute(d.OriginalMute);}finally{d.Dispose();}}
    File.WriteAllLines(Path.Combine(dir,"focus-test.txt"),lines);Console.WriteLine(String.Join(Environment.NewLine,lines));
   }
  }
 }
}
