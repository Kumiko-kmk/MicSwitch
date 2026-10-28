using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms=System.Windows.Forms;
namespace MicSwitch.Chinese
{
 internal sealed class Shell
 {
  internal readonly Window Window;
  internal readonly Engine engine;
  internal readonly Hotkey hook;
  internal readonly Settings config;
  private readonly Guardian guard;
  private readonly DispatcherTimer timer;
  private readonly Forms.NotifyIcon tray;
  private readonly System.Drawing.Icon onIcon,offIcon;
  private readonly Brush green=new SolidColorBrush(Color.FromRgb(88,223,189)),gray=new SolidColorBrush(Color.FromRgb(145,163,189)),red=new SolidColorBrush(Color.FromRgb(238,142,152));
  private bool loading=true,closing,trayTalking;
  private DeviceChoice[] shownDevices;
  private string previousLevel="",previousStatus="";
  private long beat;
  private readonly Diagnostics.UiTest test;
  internal T Find<T>(string name)where T:FrameworkElement{return(T)Window.FindName(name);}
  internal Shell(bool testing)
  {
   config=testing?new Settings{Key=5,KeepMuted=false}:Settings.Load();
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Window.xaml"))Window=(Window)XamlReader.Load(stream);
   var area=SystemParameters.WorkArea;
   Window.Left=config.Left > -9999?config.Left:area.Right-100;Window.Top=config.Top > -9999?config.Top:area.Top+80;
   Window.SourceInitialized+=delegate{Clamp();};Window.SizeChanged+=delegate{Clamp();};
   guard=new Guardian();engine=new Engine{Selection=config.Device,KeepMuted=config.KeepMuted};
   try{hook=new Hotkey(engine){Block=config.BlockButton};hook.Configure(new Binding(config.Key,config.Modifiers));}
   catch{engine.Dispose();guard.Dispose();throw;}
   Find<Button>("BindButton").Content=hook.Current.ToString();
   Find<Button>("BindButton").Click+=delegate{hook.Record();Find<Button>("BindButton").Content="请按键…";};
   Find<CheckBox>("BlockToggle").IsChecked=config.BlockButton;Find<CheckBox>("ExitToggle").IsChecked=config.KeepMuted;
   Find<CheckBox>("StartToggle").IsChecked=IsStartup();
   foreach(string name in new[]{"BlockToggle","ExitToggle","StartToggle"})Find<CheckBox>(name).Click+=delegate{Checks();};
   Find<ComboBox>("DeviceChoice").SelectionChanged+=delegate{
    if(loading)return;var d=Find<ComboBox>("DeviceChoice").SelectedItem as DeviceChoice;
    if(d!=null){engine.Press(false);config.Device=d.Id;engine.Selection=d.Id;Save();}
   };
   Find<Button>("CloseSettings").Click+=delegate{Expand(false);};Find<Button>("ExitButton").Click+=delegate{Window.Close();};
   Find<Button>("AdminButton").Visibility=IsAdmin()?Visibility.Collapsed:Visibility.Visible;
   Find<Button>("AdminButton").Click+=delegate{RestartElevated();};
   Find<StackPanel>("Indicator").MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){
    if(e.ClickCount==2){Expand(Find<StackPanel>("SettingsPanel").Visibility!=Visibility.Visible);return;}
    Window.DragMove();config.Left=Window.Left;config.Top=Window.Top;Save();
   };
   var context=new System.Windows.Controls.ContextMenu();
   var settings=new MenuItem{Header="设置"};settings.Click+=delegate{Expand(true);};context.Items.Add(settings);
   var exit=new MenuItem{Header="退出"};exit.Click+=delegate{Window.Close();};context.Items.Add(exit);Window.ContextMenu=context;
   using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("mic-on.ico"))onIcon=new System.Drawing.Icon(s);
   using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("mic-off.ico"))offIcon=new System.Drawing.Icon(s);
   tray=new Forms.NotifyIcon{Icon=offIcon,Text="麦克风 · 已静音",Visible=true};
   var menu=new Forms.ContextMenuStrip();menu.Items.Add("设置",null,delegate{Window.Show();Expand(true);Window.Activate();});
   menu.Items.Add("麦克风权限",null,delegate{Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone"){UseShellExecute=true});});
   menu.Items.Add("退出",null,delegate{Window.Close();});tray.ContextMenuStrip=menu;
   tray.DoubleClick+=delegate{Window.Show();Expand(true);Window.Activate();};
   Window.KeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Escape){hook.CancelRecord();Expand(false);}};
   SystemEvents.SessionSwitch+=SessionChanged;SystemEvents.PowerModeChanged+=PowerChanged;
   Window.Closing+=delegate{Close();};
   timer=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(40)};timer.Tick+=delegate{Tick();};timer.Start();loading=false;
   if(testing)test=new Diagnostics.UiTest(this,Settings.Folder);
  }
  internal void Expand(bool show){if(!show)hook.CancelRecord();Find<StackPanel>("SettingsPanel").Visibility=show?Visibility.Visible:Visibility.Collapsed;Window.Width=show?240:76;}
  private void Clamp(){var area=SystemParameters.WorkArea;Window.Left=Math.Max(area.Left,Math.Min(Window.Left,area.Right-Window.ActualWidth));Window.Top=Math.Max(area.Top,Math.Min(Window.Top,area.Bottom-Window.ActualHeight));}
  private bool IsStartup(){using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))return key!=null&&key.GetValue("MicSwitch.Chinese")!=null;}
  internal static bool IsAdmin(){return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}
  private void RestartElevated(){
   try{
    Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--wait-parent "+Process.GetCurrentProcess().Id){UseShellExecute=true,Verb="runas"});Window.Close();
   }catch(System.ComponentModel.Win32Exception){ }
  }
  private void Checks(){
   if(loading)return;engine.Press(false);config.BlockButton=Find<CheckBox>("BlockToggle").IsChecked==true;hook.Block=config.BlockButton;
   config.KeepMuted=Find<CheckBox>("ExitToggle").IsChecked==true;engine.KeepMuted=config.KeepMuted;
   try{bool enabled=Find<CheckBox>("StartToggle").IsChecked==true;if(enabled!=IsStartup())using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){
    if(enabled)key.SetValue("MicSwitch.Chinese","\""+Assembly.GetExecutingAssembly().Location+"\"");else key.DeleteValue("MicSwitch.Chinese",false);
   }}catch(Exception e){Program.Log(e);Find<CheckBox>("StartToggle").IsChecked=IsStartup();MessageBox.Show("无法修改开机启动设置。","麦克风");}Save();
  }
  internal void Save(){try{config.Save();}catch(Exception e){Program.Log(e);MessageBox.Show("设置未能保存。","麦克风");}}
  private void SessionChanged(object sender,SessionSwitchEventArgs e){engine.Press(false);if(e.Reason==SessionSwitchReason.SessionLock||e.Reason==SessionSwitchReason.RemoteDisconnect||e.Reason==SessionSwitchReason.ConsoleDisconnect)engine.Locked=true;if(e.Reason==SessionSwitchReason.SessionUnlock||e.Reason==SessionSwitchReason.RemoteConnect||e.Reason==SessionSwitchReason.ConsoleConnect)engine.Locked=false;}
  private void PowerChanged(object sender,PowerModeChangedEventArgs e){if(e.Mode==PowerModes.Suspend){engine.Press(false);engine.Suspended=true;}if(e.Mode==PowerModes.Resume){engine.Press(false);engine.Suspended=false;engine.Refresh();}}
  internal void EmergencyMute(){engine.Locked=true;engine.Press(false);}
  private void Tick(){
   if(closing)return;long now=Stopwatch.GetTimestamp();Interlocked.Exchange(ref engine.UiPulse,now);
   if(now-hook.Pulse>Stopwatch.Frequency){EmergencyMute();}
   if(now-beat>Stopwatch.Frequency/3){guard.Pulse();beat=now;}
   if(hook.Captured!=null){var b=hook.Captured;hook.Captured=null;hook.Configure(b);config.Key=b.Key;config.Modifiers=b.Modifiers;Save();}
   string bindText=hook.IsRecording?"请按键…":hook.Current.ToString();
   if(!Object.Equals(Find<Button>("BindButton").Content,bindText))Find<Button>("BindButton").Content=bindText;
   var state=engine.State;
   string status=state.Error??(state.Talking?"正在说话":"已静音");
   if(status!=previousStatus){previousStatus=status;tray.Text="麦克风 · "+status;Find<TextBlock>("MicIcon").Foreground=state.Error!=null?red:state.Talking?green:gray;Find<TextBlock>("MicIcon").Text=state.Talking?"\uE720":"\uF781";Find<StackPanel>("Indicator").ToolTip=state.Error??"右键设置";}
   string level=state.Error!=null?"!":!state.Talking?"−∞":!engine.MeterAvailable?"—":engine.Peak>0.000001?Math.Max(-120,20*Math.Log10(engine.Peak)).ToString("0"):"−∞";
   if(level!=previousLevel){previousLevel=level;Find<TextBlock>("LevelText").Text=level;}
   if(trayTalking!=state.Talking){trayTalking=state.Talking;tray.Icon=trayTalking?onIcon:offIcon;}
   if(shownDevices!=state.Devices){
    shownDevices=state.Devices;loading=true;var combo=Find<ComboBox>("DeviceChoice");combo.Items.Clear();combo.Items.Add(new DeviceChoice("all","所有麦克风"));combo.Items.Add(new DeviceChoice("default","默认通话麦克风"));
    foreach(var d in shownDevices)combo.Items.Add(d);foreach(DeviceChoice d in combo.Items)if(d.Id==config.Device)combo.SelectedItem=d;
    if(combo.SelectedItem==null){var d=new DeviceChoice(config.Device,"所选设备已离线");combo.Items.Add(d);combo.SelectedItem=d;}loading=false;
   }
   if(test!=null)test.Tick();
  }
  private void Close(){
   if(closing)return;closing=true;timer.Stop();SystemEvents.SessionSwitch-=SessionChanged;SystemEvents.PowerModeChanged-=PowerChanged;
   hook.Dispose();try{engine.Dispose();guard.Dispose();}catch(Exception e){Program.Log(e);}
   tray.Visible=false;tray.ContextMenuStrip.Dispose();tray.Dispose();onIcon.Dispose();offIcon.Dispose();config.Left=Window.Left;config.Top=Window.Top;Save();
  }
  internal void Capture(string path){Window.UpdateLayout();var bmp=new RenderTargetBitmap((int)Math.Ceiling(Window.ActualWidth),(int)Math.Ceiling(Window.ActualHeight),96,96,PixelFormats.Pbgra32);bmp.Render(Window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using(var f=File.Create(path))encoder.Save(f);}
 }
}
