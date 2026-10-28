using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
namespace MicSwitch.Chinese
{
 internal static class Program
 {
  internal static void Log(Exception e){}
  [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
  [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
  private static double ReadNonZero(Endpoint d)
  {
   var flags=BindingFlags.Instance|BindingFlags.NonPublic;
   var client=(IAudioClient)typeof(Endpoint).GetField("client",flags).GetValue(d);
   var capture=(IAudioCaptureClient)typeof(Endpoint).GetField("capture",flags).GetValue(d);
   IntPtr format;Audio.Check(client.GetMixFormat(out format));int align=Marshal.ReadInt16(format,12), bitsPerSample=Marshal.ReadInt16(format,14), tag=(ushort)Marshal.ReadInt16(format,0); if(tag==65534)tag=Marshal.ReadInt32(format,24); Marshal.FreeCoTaskMem(format);
   double nonzero=0;uint next;Audio.Check(capture.GetNextPacketSize(out next));
   while(next>0){IntPtr data;uint frames,bits;ulong pos,clock;Audio.Check(capture.GetBuffer(out data,out frames,out bits,out pos,out clock));
    try{if((bits&2)==0){byte[] bytes=new byte[frames*align];Marshal.Copy(data,bytes,0,bytes.Length);int step=bitsPerSample/8;for(int i=0;i+step<=bytes.Length;i+=step){double sample=0;if(tag==3 && bitsPerSample==32)sample=BitConverter.ToSingle(bytes,i);else if(bitsPerSample==16)sample=BitConverter.ToInt16(bytes,i)/32768.0;else if(bitsPerSample==32)sample=BitConverter.ToInt32(bytes,i)/2147483648.0;else if(bitsPerSample==24)sample=((bytes[i]<<8)|(bytes[i+1]<<16)|(bytes[i+2]<<24))/2147483648.0;nonzero=Math.Max(nonzero,Math.Abs(sample));}}}
    finally{Audio.Check(capture.ReleaseBuffer(frames));}Audio.Check(capture.GetNextPacketSize(out next));
   }return nonzero;
  }
  [STAThread]private static int Main(string[] args)
  {
   var report=new List<string>();List<Endpoint> devices=null;Process app=null;string dir=args[1];Directory.CreateDirectory(dir);
   try{
    devices=Audio.Enumerate();File.WriteAllText(Path.Combine(dir,"settings.xml"),"<Settings><Key>5</Key><Device>all</Device><KeepMuted>false</KeepMuted></Settings>");
    app=Process.Start(new ProcessStartInfo(args[0],"--profile \""+dir+"\""){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});Thread.Sleep(3000);
    int nonzero=0,index=0;foreach(var d in devices){
     d.StartMeter();if(!d.MeterAvailable)throw new Exception("Capture unavailable");
     ReadNonZero(d);Thread.Sleep(150);double mutedPeak=ReadNonZero(d);report.Add("endpoint "+index+" initial_mute="+d.Muted+" muted_peak="+mutedPeak);
     mouse_event(0x80,0,0,1,UIntPtr.Zero);Thread.Sleep(250);double received=ReadNonZero(d);if(received>0.0001)nonzero++;
     mouse_event(0x100,0,0,1,UIntPtr.Zero);Thread.Sleep(150);ReadNonZero(d);Thread.Sleep(150);
     double releasePeak=ReadNonZero(d);report.Add("released_mute="+d.Muted+" released_peak="+releasePeak); if(Double.IsNaN(mutedPeak)||Double.IsNaN(releasePeak)||mutedPeak>0.0001||releasePeak>0.0001)throw new Exception("Muted signal exceeds -80 dBFS");report.Add("PASS endpoint "+(++index)+" muted/released capture silence; open_nonzero="+received);
    }
    if(nonzero==0)throw new Exception("No live audio observed while open");report.Add("PASS actual PCM gate, no audio saved");return 0;
   }catch(Exception e){report.Add("FAIL "+e);return 1;}
   finally{
    mouse_event(0x100,0,0,1,UIntPtr.Zero);
    if(app!=null){try{PostMessage(app.MainWindowHandle,0x10,IntPtr.Zero,IntPtr.Zero);if(!app.WaitForExit(4000)){app.Kill();app.WaitForExit();Thread.Sleep(2200);}}catch{}app.Dispose();}
    if(devices!=null)foreach(var d in devices){try{d.SetMute(d.OriginalMute);}finally{d.Dispose();}}
    File.WriteAllLines(Path.Combine(dir,"capture-test.txt"),report);Console.WriteLine(String.Join(Environment.NewLine,report));
   }
  }
 }
}
