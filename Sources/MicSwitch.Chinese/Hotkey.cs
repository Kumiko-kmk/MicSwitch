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
  internal volatile bool Block=false, ToggleMode;
  private RawMouseWindow rawWindow;
  internal int RawEvents;
  internal bool DisablePollingForTest;
  internal volatile Binding Current=new Binding(5,0),Captured;
  private long inputPulse;
  private int version,seenVersion;
  private Forms.Timer heartbeat;
  private bool physicalHeld;
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
    rawWindow=new RawMouseWindow(this);
    heartbeat=new Forms.Timer{Interval=10};heartbeat.Tick+=delegate{Interlocked.Exchange(ref inputPulse,Stopwatch.GetTimestamp());PollPhysical();};
    heartbeat.Start();Interlocked.Exchange(ref inputPulse,Stopwatch.GetTimestamp());ready.Set();Forms.Application.Run();
   }catch(Exception e){startupError=e;ready.Set();}
   finally{if(rawWindow!=null)rawWindow.Dispose();if(heartbeat!=null)heartbeat.Dispose();if(keyboard!=IntPtr.Zero)UnhookWindowsHookEx(keyboard);if(mouse!=IntPtr.Zero)UnhookWindowsHookEx(mouse);engine.Press(false);}
  }
  // Independent of cursor position and of whether a foreground app consumes hook events.
  private static bool Physical(int key){return (GetAsyncKeyState(key)&0x8000)!=0;}
  private void PollPhysical(){
   if(DisablePollingForTest)return;
   var b=Current;
   if(recording){physicalHeld=false;return;}
   if(suppressed[b.Key])return;
   bool held=Physical(b.Key);
   if(!ToggleMode&&(PhysicalModifiers()&b.Modifiers)!=b.Modifiers)engine.Press(false);
   if(held&&!physicalHeld)Handle(b.Key,true);
   if(!held&&physicalHeld)Handle(b.Key,false);
   physicalHeld=held;
  }

  [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
  private int Modifiers(){return(down[17]||down[162]||down[163]?1:0)|(down[18]||down[164]||down[165]?2:0)|(down[16]||down[160]||down[161]?4:0)|(down[91]||down[92]?8:0);}
  private bool Handle(int key,bool pressed){
   if(key<0||key>=256)return false;
   if(seenVersion!=version){seenVersion=version;Array.Clear(down,0,down.Length);}
   bool wasDown=down[key];down[key]=pressed;
   if(!pressed&&suppressed[key]){suppressed[key]=false;if(!ToggleMode)engine.Press(false);return true;}
   if(pressed&&suppressed[key])return true;
   if(recording){
    if(pressed&&key==27){recording=false;suppressed[key]=true;return true;}
    if(pressed&&!wasDown&&ValidKey(key)){Captured=new Binding(key,Modifiers());recording=false;suppressed[key]=true;Array.Clear(down,0,down.Length);return true;}
    return false;
   }
   var b=Current;
   if(!ToggleMode&&!pressed&&(key==b.Key||IsModifier(key)))engine.Press(false);
   if(key==b.Key&&pressed){
    if(!wasDown&&((Modifiers()|PhysicalModifiers())&b.Modifiers)==b.Modifiers&&!engine.Locked&&!engine.Suspended){engine.Press(ToggleMode?!engine.Held:true);if(Block)suppressed[key]=true;}
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
  private static int PhysicalModifiers(){return(Physical(17)?1:0)|(Physical(18)?2:0)|(Physical(16)?4:0)|(Physical(91)||Physical(92)?8:0);}
  // A hidden window on the dedicated input thread receives mouse input even when
  // another process has focus. It has no dependency on the overlay's HWND.
  private sealed class RawMouseWindow:Forms.NativeWindow,IDisposable
  {
   private readonly Hotkey owner;
   private readonly IntPtr buffer=Marshal.AllocHGlobal(256);
   private readonly uint headerSize=(uint)(8+2*IntPtr.Size);
   internal RawMouseWindow(Hotkey owner){
    this.owner=owner;
    try{
     CreateHandle(new Forms.CreateParams{Caption="MicSwitch Input",Parent=new IntPtr(-3)});
     var devices=new[]{new RawDevice{Page=1,Usage=2,Flags=0x100,Target=Handle}};
     if(!RegisterRawInputDevices(devices,1,(uint)Marshal.SizeOf(typeof(RawDevice))))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }catch{DestroyHandle();Marshal.FreeHGlobal(buffer);throw;}
   }
   protected override void WndProc(ref Forms.Message m){
    if(m.Msg==0xFF){
     uint size=256;
     uint read=GetRawInputData(m.LParam,0x10000003,buffer,ref size,headerSize);
     if(read!=UInt32.MaxValue&&read>=headerSize+24&&Marshal.ReadInt32(buffer)==0){
      int flags=(ushort)Marshal.ReadInt16(buffer,(int)headerSize+4);
      if(flags!=0){Interlocked.Increment(ref owner.RawEvents);Dispatch(flags,0x10,0x20,4);Dispatch(flags,0x40,0x80,5);Dispatch(flags,0x100,0x200,6);}
     }
    }
    base.WndProc(ref m);
   }
   private void Dispatch(int flags,int press,int release,int key){if((flags&press)!=0)owner.Handle(key,true);if((flags&release)!=0)owner.Handle(key,false);}
   public void Dispose(){RegisterRawInputDevices(new[]{new RawDevice{Page=1,Usage=2,Flags=1}},1,(uint)Marshal.SizeOf(typeof(RawDevice)));DestroyHandle();Marshal.FreeHGlobal(buffer);}
   [StructLayout(LayoutKind.Sequential)]private struct RawDevice{internal ushort Page,Usage;internal uint Flags;internal IntPtr Target;}
   [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterRawInputDevices(RawDevice[] devices,uint count,uint size);
   [DllImport("user32.dll")]private static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint headerSize);
  }
  internal void DisableHooksForTest(){UnhookWindowsHookEx(keyboard);UnhookWindowsHookEx(mouse);keyboard=IntPtr.Zero;mouse=IntPtr.Zero;}
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
