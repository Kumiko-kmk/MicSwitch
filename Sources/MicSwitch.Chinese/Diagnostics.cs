using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
namespace MicSwitch.Chinese
{
 internal static class Diagnostics
 {
  internal static int Probe(string path,bool test){
   var lines=new List<string>();List<Endpoint> devices=null;
   try{devices=Audio.Enumerate();lines.Add("Devices="+devices.Count);
    foreach(var d in devices){lines.Add(d.Name+" | "+d.Id+" | Muted="+d.Muted);
     if(test)try{d.SetMute(true);if(!d.Muted)throw new Exception("静音失败");d.SetMute(false);if(d.Muted)throw new Exception("开麦失败");d.StartMeter();Thread.Sleep(150);lines.Add("Meter="+d.MeterAvailable+" Peak="+d.Peak());}finally{d.StopMeter();d.SetMute(d.OriginalMute);}
    }lines.Add("PASS");return 0;
   }catch(Exception e){lines.Add("FAIL "+e);return 1;}
   finally{if(devices!=null)foreach(var d in devices)d.Dispose();File.WriteAllLines(path,lines);}
  }
  internal static void Key(int key,bool pressed){
   if(key==4)mouse_event(pressed?0x20u:0x40u,0,0,0,UIntPtr.Zero);
   else if(key==5||key==6)mouse_event(pressed?0x80u:0x100u,0,0,(uint)(key-4),UIntPtr.Zero);
   else keybd_event((byte)key,0,pressed?0u:2u,UIntPtr.Zero);
  }
  [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
  [DllImport("user32.dll")]private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
  internal sealed class UiTest
  {
   private readonly Shell shell;
   private readonly string folder;
   private readonly List<string> lines=new List<string>();
   private readonly List<Action> steps=new List<Action>();
   private readonly Stopwatch clock=Stopwatch.StartNew();
   private int step;
   private bool done;
   internal UiTest(Shell s,string folder){
    shell=s;this.folder=folder;Directory.CreateDirectory(folder);
    steps.Add(delegate{Assert(!s.engine.State.Talking&&s.engine.State.Error==null,"startup mute");s.Capture(Path.Combine(folder,"collapsed.png"));Key(5,true);});
    steps.Add(delegate{Assert(s.engine.State.Talking,"XButton1 down");s.Capture(Path.Combine(folder,"talking.png"));Key(5,false);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"XButton1 up");s.hook.Configure(new Binding(6,0));Key(6,true);});
    steps.Add(delegate{Assert(s.engine.State.Talking,"XButton2 down");Key(6,false);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"XButton2 up");s.hook.Configure(new Binding(4,0));Key(4,true);});
    steps.Add(delegate{Assert(s.engine.State.Talking,"middle mouse down");Key(4,false);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"middle mouse up");s.hook.Configure(new Binding(119,0));Key(119,true);});
    steps.Add(delegate{Assert(s.engine.State.Talking,"F8 down");Key(119,true);Key(119,false);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"F8 repeat and release");s.hook.Configure(new Binding(120,3));Key(120,true);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"wrong modifiers stay muted");Key(120,false);Key(162,true);Key(164,true);Key(120,true);});
    steps.Add(delegate{Assert(s.engine.State.Talking,"Ctrl+Alt+F9 down");Key(164,false);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"modifier first release mutes");Key(120,true);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"repeat cannot reopen after modifier release");Key(120,false);Key(162,false);s.hook.Record();Key(162,true);Key(119,true);});
    steps.Add(delegate{Key(119,false);Key(162,false);Assert(s.hook.Current.Key==119&&s.hook.Current.Modifiers==1,"interactive key binding Ctrl+F8");});
    steps.Add(delegate{Key(162,true);Key(119,true);});
    steps.Add(delegate{Assert(s.engine.State.Talking,"recorded hotkey works");Key(119,false);Key(162,false);s.engine.Locked=true;Key(162,true);Key(119,true);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"lock blocks unmute");Key(119,false);Key(162,false);s.engine.Locked=false;s.engine.Suspended=true;Key(162,true);Key(119,true);});
    steps.Add(delegate{Assert(!s.engine.State.Talking,"suspend blocks unmute");Key(119,false);Key(162,false);s.engine.Suspended=false;s.hook.Configure(new Binding(5,0));s.config.Key=5;s.config.Modifiers=0;s.Save();Assert(Settings.Load().Key==5,"settings persist");s.Expand(true);});
    steps.Add(delegate{s.Capture(Path.Combine(folder,"settings.png"));s.engine.Selection="missing-test-device";s.engine.Refresh();});
    steps.Add(delegate{Assert(!s.engine.State.Talking&&s.engine.State.Error!=null,"missing device fails closed");s.engine.Selection="all";s.engine.Refresh();});
    steps.Add(delegate{Assert(s.engine.State.Error==null,"device selection recovers");});
   }
   private void Assert(bool condition,string text){if(!condition)throw new Exception(text+": "+shell.engine.State.Error);lines.Add("PASS "+text);}
   internal void Tick(){
    if(done||clock.ElapsedMilliseconds<2000+step*450)return;
    try{if(step<steps.Count){steps[step++]();return;}done=true;File.WriteAllLines(Path.Combine(folder,"ui-test.txt"),lines);shell.Window.Close();}
    catch(Exception e){done=true;lines.Add("FAIL "+e);foreach(int key in new[]{4,5,6,119,120,162,164})Key(key,false);File.WriteAllLines(Path.Combine(folder,"ui-test.txt"),lines);shell.Window.Close();}
   }
  }
 }
}
