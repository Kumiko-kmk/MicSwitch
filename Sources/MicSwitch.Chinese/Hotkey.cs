using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Forms=System.Windows.Forms;
namespace MicSwitch.Chinese
{
 internal sealed class Binding
 {
  internal readonly int Key,Modifiers;
  internal Binding(int key,int modifiers){Key=key;Modifiers=modifiers;}
  public override string ToString(){
   string text="";
   if((Modifiers&1)!=0)text+="Ctrl + ";if((Modifiers&2)!=0)text+="Alt + ";
   if((Modifiers&4)!=0)text+="Shift + ";if((Modifiers&8)!=0)text+="Win + ";
   string key=Key==4?"鼠标中键":Key==5?"鼠标侧键 1":Key==6?"鼠标侧键 2":Key==32?"空格":((Forms.Keys)Key).ToString();
   return text+key;
  }
 }
 internal sealed class Hotkey:IDisposable
 {
  private readonly Engine engine;
  private readonly Thread thread;
  private readonly ManualResetEvent ready=new ManualResetEvent(false);
  private readonly HookProc keyboardProc,mouseProc;
  private IntPtr keyboard,mouse;
  private uint threadId;
  private Exception startupError;
  private readonly bool[] down=new bool[256],suppressed=new bool[256];
  private volatile bool recording;
  internal volatile bool Block=true;
  internal volatile Binding Current=new Binding(5,0),Captured;
  private long inputPulse;
  private int version,seenVersion;
  private Forms.Timer heartbeat;
  internal bool IsRecording{get{return recording;}}
  internal long Pulse{get{return Interlocked.Read(ref inputPulse);}}
  internal Hotkey(Engine engine){
   this.engine=engine;keyboardProc=Keyboard;mouseProc=Mouse;
   thread=new Thread(Run){IsBackground=true,Name="全局热键",Priority=ThreadPriority.AboveNormal};
   thread.SetApartmentState(ApartmentState.STA);thread.Start();
   if(!ready.WaitOne(5000))throw new TimeoutException("热键初始化超时");
   if(startupError!=null)throw startupError;
  }
  internal static bool ValidKey(int key){return key>2&&key<256&&!IsModifier(key)&&key!=27;}
  private static bool IsModifier(int key){return key==16||key==17||key==18||(key>=160&&key<=165)||key==91||key==92;}
  internal void Configure(Binding value){engine.Press(false);Current=value;Interlocked.Increment(ref version);}
  internal void Record(){engine.Press(false);Captured=null;recording=true;}
  internal void CancelRecord(){recording=false;engine.Press(false);}
  private void Run(){
   try{
    threadId=GetCurrentThreadId();keyboard=SetWindowsHookEx(13,keyboardProc,GetModuleHandle(null),0);mouse=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);
    if(keyboard==IntPtr.Zero||mouse==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    heartbeat=new Forms.Timer{Interval=100};heartbeat.Tick+=delegate{Interlocked.Exchange(ref inputPulse,Stopwatch.GetTimestamp());};
    heartbeat.Start();Interlocked.Exchange(ref inputPulse,Stopwatch.GetTimestamp());ready.Set();Forms.Application.Run();
   }catch(Exception e){startupError=e;ready.Set();}
   finally{if(heartbeat!=null)heartbeat.Dispose();if(keyboard!=IntPtr.Zero)UnhookWindowsHookEx(keyboard);if(mouse!=IntPtr.Zero)UnhookWindowsHookEx(mouse);engine.Press(false);}
  }
  private int Modifiers(){return(down[17]||down[162]||down[163]?1:0)|(down[18]||down[164]||down[165]?2:0)|(down[16]||down[160]||down[161]?4:0)|(down[91]||down[92]?8:0);}
  private bool Handle(int key,bool pressed){
   if(key<0||key>=256)return false;
   if(seenVersion!=version){seenVersion=version;Array.Clear(down,0,down.Length);}
   bool wasDown=down[key];down[key]=pressed;
   if(!pressed&&suppressed[key]){suppressed[key]=false;engine.Press(false);return true;}
   if(pressed&&suppressed[key])return true;
   if(recording){
    if(pressed&&key==27){recording=false;suppressed[key]=true;return true;}
    if(pressed&&!wasDown&&ValidKey(key)){Captured=new Binding(key,Modifiers());recording=false;suppressed[key]=true;Array.Clear(down,0,down.Length);return true;}
    return false;
   }
   var b=Current;
   if(!pressed&&(key==b.Key||IsModifier(key)))engine.Press(false);
   if(key==b.Key&&pressed){
    if(!wasDown&&(Modifiers()&b.Modifiers)==b.Modifiers&&!engine.Locked&&!engine.Suspended){engine.Press(true);if(Block)suppressed[key]=true;}
    return suppressed[key];
   }
   return false;
  }
  private IntPtr Keyboard(int code,IntPtr wp,IntPtr lp){
   if(code>=0){int msg=wp.ToInt32(),key=Marshal.ReadInt32(lp);if(msg==0x100||msg==0x104||msg==0x101||msg==0x105)if(Handle(key,msg==0x100||msg==0x104))return new IntPtr(1);}
   return CallNextHookEx(keyboard,code,wp,lp);
  }
  private IntPtr Mouse(int code,IntPtr wp,IntPtr lp){
   if(code>=0){int msg=wp.ToInt32(),key=0;bool pressed=false;
    if(msg==0x20B||msg==0x20C){key=((Marshal.ReadInt32(lp,8)>>16)&0xffff)==1?5:6;pressed=msg==0x20B;}
    if(msg==0x207||msg==0x208){key=4;pressed=msg==0x207;}
    if(key!=0&&Handle(key,pressed))return new IntPtr(1);
   }return CallNextHookEx(mouse,code,wp,lp);
  }
  public void Dispose(){PostThreadMessage(threadId,0x12,IntPtr.Zero,IntPtr.Zero);thread.Join(2000);ready.Dispose();}
  private delegate IntPtr HookProc(int code,IntPtr wp,IntPtr lp);
  [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint thread);
  [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
  [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wp,IntPtr lp);
  [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint thread,uint message,IntPtr wp,IntPtr lp);
  [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string module);
 }
}
