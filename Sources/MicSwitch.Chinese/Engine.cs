using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Serialization;
namespace MicSwitch.Chinese
{
 public sealed class Settings
 {
  public int SideButton = 1, Key = 0, Modifiers = 0;
  public string Device = "all";
  public bool BlockButton = true, KeepMuted = true;
  public double Left = -10000, Top = -10000;
  public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicSwitch.Chinese");
  private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Settings));
  public static Settings Load()
  {
   try { using (var f = File.OpenRead(Path.Combine(Folder,"settings.xml"))) {
    var s=(Settings)Serializer.Deserialize(f);
    if(s.Key==0) s.Key=s.SideButton==2?6:5;
    if(!Hotkey.ValidKey(s.Key)) s.Key=5;
    s.Modifiers &= 15;
    if(String.IsNullOrEmpty(s.Device)) s.Device="all";
    return s;
   }} catch { return new Settings { Key=5 }; }
  }
  public void Save()
  {
   Directory.CreateDirectory(Folder); string path=Path.Combine(Folder,"settings.xml"), temp=path+".tmp";
   using(var f=File.Create(temp)) Serializer.Serialize(f,this);
   if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
  }
 }
 internal sealed class DeviceChoice
 {
  internal readonly string Id,Name;
  internal DeviceChoice(string id,string name){Id=id;Name=name;}
  public override string ToString(){return Name;}
 }
 internal sealed class Snapshot
 {
  internal bool Talking;
  internal string Error;
  internal DeviceChoice[] Devices=new DeviceChoice[0];
 }
 internal sealed class Engine:IDisposable
 {
  private readonly Thread control,metering;
  private readonly AutoResetEvent wake=new AutoResetEvent(false);
  private readonly ManualResetEvent meterStop=new ManualResetEvent(false);
  private readonly Dictionary<string,Endpoint> devices=new Dictionary<string,Endpoint>();
  internal volatile string Selection="all";
  internal volatile bool Held,Locked,Suspended,KeepMuted=true;
  internal long UiPulse;
  private volatile bool stopping,refresh=true,refreshMeter=true;
  internal volatile Snapshot State=new Snapshot();
  internal volatile float Peak;
  internal volatile bool MeterAvailable;
  private string defaultId="";
  private DeviceChoice[] choices=new DeviceChoice[0];
  internal Engine()
  {
   UiPulse=Stopwatch.GetTimestamp();
   control=new Thread(Run){IsBackground=true,Name="麦克风控制",Priority=ThreadPriority.AboveNormal};
   metering=new Thread(MeterLoop){IsBackground=true,Name="音量采集",Priority=ThreadPriority.BelowNormal};
   control.SetApartmentState(ApartmentState.MTA);metering.SetApartmentState(ApartmentState.MTA);
   control.Start();metering.Start();
  }
  internal void Press(bool value){Held=value;wake.Set();}
  internal void Refresh(){refresh=true;refreshMeter=true;wake.Set();}
  private void Discover()
  {
   var found=Audio.Enumerate();var ids=new HashSet<string>();
   foreach(var d in found){ids.Add(d.Id);if(devices.ContainsKey(d.Id))d.Dispose();else devices.Add(d.Id,d);}
   var removed=new List<string>();foreach(var id in devices.Keys)if(!ids.Contains(id))removed.Add(id);
   foreach(var id in removed){devices[id].Dispose();devices.Remove(id);}
   defaultId=Audio.DefaultId();var list=new List<DeviceChoice>();
   foreach(var d in devices.Values)list.Add(new DeviceChoice(d.Id,d.Name));choices=list.ToArray();
  }
  private void Publish(bool talking,string error)
  {
   var old=State;
   if(old.Talking!=talking||old.Error!=error||old.Devices!=choices)State=new Snapshot{Talking=talking,Error=error,Devices=choices};
  }
  private void Run()
  {
   DeviceNotifications notifications=null;
   try {
    notifications=new DeviceNotifications(Refresh);
    while(!stopping){
     string error=null;
     if(refresh){refresh=false;try{Discover();}catch(Exception e){error="无法读取麦克风";Program.Log(e);}}
     bool allow=Held&&!Locked&&!Suspended&&Stopwatch.GetTimestamp()-Interlocked.Read(ref UiPulse)<Stopwatch.Frequency*2;
     if(Locked||Suspended||Stopwatch.GetTimestamp()-Interlocked.Read(ref UiPulse)>=Stopwatch.Frequency*2)Held=false;
     string selected=Selection=="default"?defaultId:Selection;
     bool exists=Selection=="all"?devices.Count>0:devices.ContainsKey(selected);
     foreach(var d in devices.Values){
      bool active=allow&&exists&&(Selection=="all"||selected==d.Id);
      try{d.SetMute(!active);}catch(Exception e){error="麦克风控制失败";Program.Log(e);}
     }
     if(!exists)error=devices.Count==0?"未找到麦克风":"麦克风已离线";
     if(error!=null){Held=false;allow=false;foreach(var d in devices.Values)try{d.SetMute(true);}catch{}}
     Publish(allow&&exists,error);
     // Key events wake this wait immediately. This is only an external-state safety audit.
     wake.WaitOne(200);
    }
   }catch(Exception e){Publish(false,"麦克风控制已停止");Program.Log(e);}
   finally {
    if(notifications!=null)notifications.Dispose();
    foreach(var d in devices.Values){try{d.SetMute(KeepMuted||d.OriginalMute);}catch(Exception e){Program.Log(e);}d.Dispose();}
    devices.Clear();
   }
  }
  private void MeterLoop()
  {
   var meters=new List<Endpoint>();string selected="",preferred="";
   try{while(!stopping){
    if(refreshMeter||selected!=Selection){
     refreshMeter=false;selected=Selection;preferred=selected=="default"?Audio.DefaultId():selected;
     foreach(var d in meters)d.Dispose();meters.Clear();
     try{foreach(var d in Audio.Enumerate()){
      if(selected=="all"||d.Id==preferred){meters.Add(d);d.StartMeter();}else d.Dispose();
     }}catch(Exception e){Program.Log(e);}
    }
    float peak=0;bool available=meters.Count>0;
    foreach(var d in meters){try{peak=Math.Max(peak,d.Peak());available &= d.MeterAvailable;}catch{available=false;}}
    Peak=peak;MeterAvailable=available;if(meterStop.WaitOne(40))break;
   }}finally{foreach(var d in meters)d.Dispose();}
  }
  public void Dispose()
  {
   Held=false;stopping=true;wake.Set();meterStop.Set();
   if(!control.Join(3000))throw new TimeoutException("麦克风控制未能退出");
   if(!metering.Join(3000))throw new TimeoutException("音量采集未能退出");
   wake.Dispose();meterStop.Dispose();
  }
 }
 [ComVisible(true),Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 public interface IMMNotificationClient
 {
  [PreserveSig]int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)]string id,uint state);
  [PreserveSig]int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)]string id);
  [PreserveSig]int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)]string id);
  [PreserveSig]int OnDefaultDeviceChanged(int flow,int role,[MarshalAs(UnmanagedType.LPWStr)]string id);
  [PreserveSig]int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)]string id,PropertyKey key);
 }
 [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
 public sealed class DeviceNotifications:IMMNotificationClient,IDisposable
 {
  private IMMDeviceEnumerator enumerator;
  private readonly Action changed;
  private IntPtr pointer;
  internal DeviceNotifications(Action action){
   changed=action;enumerator=(IMMDeviceEnumerator)new DeviceEnumerator();
   pointer=Marshal.GetComInterfaceForObject(this,typeof(IMMNotificationClient));
   Audio.Check(enumerator.RegisterEndpointNotificationCallback(pointer));
  }
  public int OnDeviceStateChanged(string id,uint state){changed();return 0;}
  public int OnDeviceAdded(string id){changed();return 0;}
  public int OnDeviceRemoved(string id){changed();return 0;}
  public int OnDefaultDeviceChanged(int flow,int role,string id){if(flow==1)changed();return 0;}
  public int OnPropertyValueChanged(string id,PropertyKey key){return 0;}
  public void Dispose(){
   if(enumerator!=null){enumerator.UnregisterEndpointNotificationCallback(pointer);Audio.Release(enumerator);enumerator=null;}
   if(pointer!=IntPtr.Zero){Marshal.Release(pointer);pointer=IntPtr.Zero;}
  }
 }
}
