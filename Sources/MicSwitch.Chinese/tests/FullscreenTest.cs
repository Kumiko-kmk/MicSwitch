using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
namespace MicSwitch.Chinese
{
 internal static class Program
 {
  internal static void Log(Exception e){}
  [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
  [DllImport("user32.dll")]private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
  [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
  [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
  [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow(); private static bool toggle; private static IntPtr expected; private static List<Endpoint> devices;
  private static double Change(bool open)
  {
   if(expected!=IntPtr.Zero && GetForegroundWindow()!=expected){keybd_event(18,0,0,UIntPtr.Zero);keybd_event(18,0,2,UIntPtr.Zero);SetForegroundWindow(expected);System.Windows.Forms.Application.DoEvents();Thread.Sleep(100);if(GetForegroundWindow()!=expected)throw new Exception("Fullscreen focus lost");}var sw=Stopwatch.StartNew();if(toggle){mouse_event(0x80,0,0,1,UIntPtr.Zero);Thread.Sleep(20);mouse_event(0x100,0,0,1,UIntPtr.Zero);}else mouse_event(open?0x80u:0x100u,0,0,1,UIntPtr.Zero);
   while(sw.ElapsedMilliseconds<2500){System.Windows.Forms.Application.DoEvents();bool matched=true;foreach(var d in devices)if(d.Muted==open)matched=false;if(matched)return sw.Elapsed.TotalMilliseconds;Thread.Sleep(1);}
   throw new Exception("Timeout changing mute="+!open);
  }
  private static string Stats(List<double> values)
  {
   values.Sort();double sum=0;foreach(var v in values)sum+=v;
   return "n="+values.Count+" min="+values[0].ToString("F2")+" median="+values[values.Count/2].ToString("F2")+" p95="+values[(int)Math.Ceiling(values.Count*.95)-1].ToString("F2")+" max="+values[values.Count-1].ToString("F2")+" mean="+(sum/values.Count).ToString("F2");
  }
  [STAThread]private static int Main(string[] args)
  {
   Thread.CurrentThread.CurrentCulture=CultureInfo.InvariantCulture;
   string exe=args[0],dir=args[1];int duration=args.Length>2?Int32.Parse(args[2]):0;
   toggle=args.Length>3&&args[3]=="toggle";bool rawOnly=args.Length>4&&args[4]=="raw";
   Directory.CreateDirectory(dir);var report=new List<string>();Process app=null;
   File.WriteAllText(Path.Combine(dir,"settings.xml"),"<Settings><SideButton>1</SideButton><Key>5</Key><Modifiers>0</Modifiers><Device>all</Device><InputVersion>2</InputVersion><ToggleMode>"+(toggle?"true":"false")+"</ToggleMode><BlockButton>false</BlockButton><KeepMuted>false</KeepMuted></Settings>");
   try
   {
    devices=Audio.Enumerate();if(devices.Count==0)throw new Exception("No devices");
    app=Process.Start(new ProcessStartInfo(exe,(rawOnly?"--raw-only-profile ":"--profile ")+"\""+dir+"\""){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});
    Thread.Sleep(3000);if(app.HasExited)throw new Exception("App exited");
    foreach(var d in devices)if(!d.Muted)throw new Exception("Startup not muted");
    var fullscreen=new System.Windows.Forms.Form{FormBorderStyle=System.Windows.Forms.FormBorderStyle.None,Bounds=System.Windows.Forms.Screen.PrimaryScreen.Bounds,TopMost=true,Text="Fullscreen input regression"};fullscreen.Show();System.Windows.Forms.Application.DoEvents();SetForegroundWindow(fullscreen.Handle);expected=fullscreen.Handle;System.Windows.Forms.Cursor.Position=new System.Drawing.Point(400,400); report.Add("Fullscreen foreground, cursor away from overlay; toggle="+toggle+" rawOnly="+rawOnly); var press=new List<double>();var release=new List<double>();
    for(int i=0;i<100;i++){press.Add(Change(true));Thread.Sleep(35);release.Add(Change(false));Thread.Sleep(35);}
    report.Add("Press_ms "+Stats(press));report.Add("Release_ms "+Stats(release));expected=IntPtr.Zero;fullscreen.Close();fullscreen.Dispose();
    File.WriteAllLines(Path.Combine(dir,"latency.txt"),report);
    var timer=Stopwatch.StartNew();long next=0;int cycle=0;
    using(var csv=new StreamWriter(Path.Combine(dir,"memory.csv"))){
     csv.WriteLine("seconds,private_bytes,working_set,handles,threads,cpu_ms");csv.AutoFlush=true;
     while(timer.Elapsed.TotalSeconds<duration){
      if(timer.ElapsedMilliseconds>=next){app.Refresh();csv.WriteLine(timer.Elapsed.TotalSeconds.ToString("F1")+","+app.PrivateMemorySize64+","+app.WorkingSet64+","+app.HandleCount+","+app.Threads.Count+","+app.TotalProcessorTime.TotalMilliseconds);next+=5000;}
      Change(true);Thread.Sleep(400);Change(false);Thread.Sleep(600);cycle++;
     }
    }
    report.Add("soak_seconds="+duration+" cycles="+cycle);
    // Crash while unmuted; independent readback verifies the guardian, not the UI state.
    Change(true);app.Kill();app.WaitForExit();mouse_event(0x100,0,0,1,UIntPtr.Zero);Thread.Sleep(2500);
    foreach(var d in devices)if(!d.Muted)throw new Exception("Guardian failed");
    report.Add("PASS guardian after forced termination");report.Add("PASS all checks");return 0;
   }
   catch(Exception e){report.Add("FAIL "+e);return 1;}
   finally
   {
    mouse_event(0x100,0,0,1,UIntPtr.Zero);
    if(app!=null){try{if(!app.HasExited){PostMessage(app.MainWindowHandle,0x10,IntPtr.Zero,IntPtr.Zero);if(!app.WaitForExit(4000)){app.Kill();app.WaitForExit();Thread.Sleep(2200);}}}catch{}app.Dispose();}
    if(devices!=null)foreach(var d in devices){try{d.SetMute(d.OriginalMute);}finally{d.Dispose();}}
    File.WriteAllLines(Path.Combine(dir,"report.txt"),report);Console.WriteLine(String.Join(Environment.NewLine,report));
   }
  }
 }
}
