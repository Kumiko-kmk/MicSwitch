using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("輕聲 — MicSwitch 中文輕量版")]
[assembly: AssemblyDescription("按住側鍵說話，鬆開即靜音")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace MicSwitch.Chinese
{
    internal static class Program
    {
        private static long lastLog;
        internal static void Log(Exception e)
        {
            long now = Stopwatch.GetTimestamp(); if (now - Interlocked.Read(ref lastLog) < Stopwatch.Frequency * 5) return;
            Interlocked.Exchange(ref lastLog, now);
            try { Directory.CreateDirectory(Settings.Folder); File.AppendAllText(System.IO.Path.Combine(Settings.Folder, "error.log"), DateTime.Now.ToString("s") + " " + e + Environment.NewLine); } catch { }
        }
        [STAThread] private static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("zh-TW");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-TW");
            if (args.Length > 0 && args[0] == "--guard") return Guardian.Run(Int32.Parse(args[1]), args[2]);
            if (args.Length > 1 && args[0] == "--probe") return Diagnostics.Probe(args[1], false);
            if (args.Length > 1 && args[0] == "--audio-test") return Diagnostics.Probe(args[1], true);
            if (args.Length > 1 && args[0] == "--resilience-test") return Diagnostics.Resilience(args[1]);
            if (args.Length > 1 && args[0] == "--profile") Settings.Folder = System.IO.Path.GetFullPath(args[1]);
            bool test = args.Length > 1 && args[0] == "--ui-test";
            if (test) Settings.Folder = System.IO.Path.GetFullPath(args[1]);
            bool created;
            using (var mutex = new Mutex(true, "Local\\MicSwitch.Chinese.SingleInstance", out created))
            {
                if (!created) { MessageBox.Show("輕聲已在執行，請從系統匣開啟。", "輕聲"); return 0; }
                if (Process.GetProcessesByName("MicSwitch").Length > 0)
                {
                    MessageBox.Show("請先退出原版 MicSwitch，再啟動輕聲。", "輕聲"); return 1;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var window = new Shell(test);
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log(e.Exception); window.EmergencyMute();
                        MessageBox.Show("程式發生錯誤，已要求麥克風靜音。請重新開啟輕聲。", "輕聲");
                        e.Handled = true; window.Window.Close();
                    };
                    return app.Run(window.Window);
                }
                catch (Exception e) { Log(e); MessageBox.Show("無法啟動輕聲，請確認麥克風可用。", "輕聲"); return 1; }
            }
        }
    }
    internal sealed class Guardian : IDisposable
    {
        private readonly EventWaitHandle pulse, done, ready;
        private readonly Process child;
        internal Guardian()
        {
            string name = "Local\\MicSwitch.Chinese." + Guid.NewGuid().ToString("N");
            pulse = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".pulse");
            done = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".done");
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready");
            child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--guard " + Process.GetCurrentProcess().Id + " " + name) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (!ready.WaitOne(5000)) throw new InvalidOperationException("靜音保護無法啟動");
        }
        internal void Pulse() { if (child.HasExited) throw new InvalidOperationException("靜音保護已停止"); pulse.Set(); }
        public void Dispose() { done.Set(); child.WaitForExit(1500); child.Dispose(); pulse.Dispose(); done.Dispose(); ready.Dispose(); }
        internal static int Run(int pid, string name)
        {
            try
            {
                using (var parent = Process.GetProcessById(pid))
                using (var p = EventWaitHandle.OpenExisting(name + ".pulse"))
                using (var d = EventWaitHandle.OpenExisting(name + ".done"))
                using (var r = EventWaitHandle.OpenExisting(name + ".ready"))
                {
                    r.Set();
                    while (true)
                    {
                        int result = WaitHandle.WaitAny(new WaitHandle[] { d, p }, 1800);
                        if (result == 0) return 0;
                        if (result == WaitHandle.WaitTimeout || parent.HasExited)
                        {
                            try
                            {
                                var endpoints = Audio.Enumerate();
                                foreach (var endpoint in endpoints) { try { endpoint.SetMute(true); } finally { endpoint.Dispose(); } }
                            }
                            catch (Exception e) { Program.Log(e); }
                            if (parent.HasExited) return 0;
                        }
                    }
                }
            }
            catch (Exception e) { Program.Log(e); return 1; }
        }
    }
    internal sealed class Shell
    {
        internal readonly Window Window;
        private readonly Settings config;
        internal readonly Engine engine;
        private readonly MouseHook hook;
        private readonly Guardian guard;
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private readonly System.Drawing.Icon onIcon, offIcon;
        private bool trayTalking;
        private readonly List<Rectangle> bars = new List<Rectangle>();
        private readonly Brush teal = new SolidColorBrush(Color.FromRgb(88, 223, 189));
        private readonly Brush muted = new SolidColorBrush(Color.FromRgb(93, 115, 145));
        private readonly Brush red = new SolidColorBrush(Color.FromRgb(240, 162, 170));
        private readonly double[] history = new double[36];
        private bool loading = true, closing, test;
        private string deviceSignature = "";
        private long lastBeat;
        private readonly List<string> testLines = new List<string>();
        private Stopwatch testClock;
        private int testStep;
        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        internal Shell(bool test)
        {
            this.test = test; config = test ? new Settings { KeepMuted = false } : Settings.Load();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Window.xaml")) Window = (Window)XamlReader.Load(stream);
            Window.Topmost = config.Topmost; Window.Opacity = config.Opacity;
            Find<StackPanel>("Body").LayoutTransform = new ScaleTransform(config.Scale, config.Scale);
            Window.Width = 320 * config.Scale;
            var area = SystemParameters.WorkArea;
            Window.Left = config.Left > -9999 ? config.Left : area.Right - Window.Width - 24;
            Window.Top = config.Top > -9999 ? config.Top : area.Top + 64;
            Window.SourceInitialized += delegate { ClampPosition(); };
            Window.SizeChanged += delegate { ClampPosition(); };
            // The engine owns audio COM objects on its own thread. Hook callbacks never perform audio IO.
            guard = new Guardian();
            engine = new Engine(); engine.Selection = config.Device; engine.KeepMuted = config.KeepMuted;
            try { hook = new MouseHook(engine) { Button = config.SideButton, Block = config.BlockButton }; }
            catch { engine.Dispose(); guard.Dispose(); throw; }
            Find<ComboBox>("KeyChoice").SelectedIndex = config.SideButton - 1;
            Find<CheckBox>("BlockToggle").IsChecked = config.BlockButton;
            Find<CheckBox>("TopToggle").IsChecked = config.Topmost;
            Find<CheckBox>("ExitToggle").IsChecked = config.KeepMuted;
            Find<CheckBox>("StartToggle").IsChecked = IsStartup();
            Find<Slider>("OpacitySlider").Value = config.Opacity;
            Find<Slider>("ScaleSlider").Value = config.Scale;
            Find<Button>("SettingsButton").Click += delegate { ToggleSettings(); };
            Find<Button>("HideButton").Click += delegate { Window.Hide(); };
            Find<Button>("ExitButton").Click += delegate { Window.Close(); };
            Find<Button>("PrivacyButton").Click += delegate { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); };
            Find<ComboBox>("KeyChoice").SelectionChanged += delegate
            {
                if (loading) return; engine.Press(false); config.SideButton = Find<ComboBox>("KeyChoice").SelectedIndex + 1; hook.Button = config.SideButton; Save();
            };
            Find<ComboBox>("DeviceChoice").SelectionChanged += delegate
            {
                if (loading) return; var item = Find<ComboBox>("DeviceChoice").SelectedItem as DeviceChoice;
                if (item == null) return; engine.Press(false); config.Device = item.Id; engine.Selection = item.Id; Save();
            };
            foreach (string name in new[] { "BlockToggle", "TopToggle", "ExitToggle", "StartToggle" })
            {
                Find<CheckBox>(name).Click += delegate { ApplyChecks(); };
            }
            Find<Slider>("OpacitySlider").ValueChanged += delegate
            {
                if (loading) return; config.Opacity = Find<Slider>("OpacitySlider").Value; Window.Opacity = config.Opacity; Save();
            };
            Find<Slider>("ScaleSlider").ValueChanged += delegate
            {
                if (loading) return; config.Scale = Find<Slider>("ScaleSlider").Value;
                Find<StackPanel>("Body").LayoutTransform = new ScaleTransform(config.Scale, config.Scale);
                Window.Width = 320 * config.Scale; Save();
            };
            Find<Grid>("DragArea").MouseLeftButtonDown += Drag;
            Find<Grid>("StatusArea").MouseLeftButtonDown += Drag;
            Window.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Find<StackPanel>("SettingsPanel").Visibility = Visibility.Collapsed; e.Handled = true; } };
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mic-on.ico")) onIcon = new System.Drawing.Icon(s);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mic-off.ico")) offIcon = new System.Drawing.Icon(s);
            tray = new Forms.NotifyIcon { Icon = offIcon, Text = "輕聲 · 已靜音", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("顯示懸浮窗", null, delegate { Window.Show(); Window.Activate(); });
            menu.Items.Add("設定", null, delegate { Window.Show(); Find<StackPanel>("SettingsPanel").Visibility = Visibility.Visible; Window.Activate(); });
            menu.Items.Add("退出", null, delegate { Window.Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Window.Show(); Window.Activate(); };
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            Window.Closing += delegate { Close(); };
            var canvas = Find<Canvas>("Meter");
            for (int i = 0; i < history.Length; i++)
            {
                var bar = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, Width = 4, Height = 3, Fill = muted };
                canvas.Children.Add(bar); bars.Add(bar);
            }
            timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += delegate { Tick(); }; timer.Start(); loading = false;
            if (test) { testClock = Stopwatch.StartNew(); Directory.CreateDirectory(Settings.Folder); }
        }
        private void Drag(object sender, MouseButtonEventArgs e)
        {
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null && d != sender) { if (d is Button) return; d = VisualTreeHelper.GetParent(d); }
            if (e.ButtonState == MouseButtonState.Pressed) { Window.DragMove(); config.Left = Window.Left; config.Top = Window.Top; Save(); }
        }
        private void ClampPosition()
        {
            var area = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(Window).Handle).WorkingArea;
            var source = PresentationSource.FromVisual(Window);
            double scaleX = source == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            double scaleY = source == null ? 1 : source.CompositionTarget.TransformToDevice.M22;
            Find<ScrollViewer>("SettingsScroll").MaxHeight = Math.Max(100, area.Height / scaleY / config.Scale - 180);
            Window.Left = Math.Max(area.Left / scaleX, Math.Min(Window.Left, area.Right / scaleX - Window.ActualWidth));
            Window.Top = Math.Max(area.Top / scaleY, Math.Min(Window.Top, area.Bottom / scaleY - Window.ActualHeight));
        }
        private void ToggleSettings() { var panel = Find<StackPanel>("SettingsPanel"); panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
        private void ApplyChecks()
        {
            if (loading) return;
            engine.Press(false);
            config.BlockButton = Find<CheckBox>("BlockToggle").IsChecked == true; hook.Block = config.BlockButton;
            config.Topmost = Find<CheckBox>("TopToggle").IsChecked == true; Window.Topmost = config.Topmost;
            config.KeepMuted = Find<CheckBox>("ExitToggle").IsChecked == true; engine.KeepMuted = config.KeepMuted;
            try
            {
                bool enabled = Find<CheckBox>("StartToggle").IsChecked == true;
                if (enabled != IsStartup())
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (enabled) key.SetValue("MicSwitch.Chinese", "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else key.DeleteValue("MicSwitch.Chinese", false);
                }
            }
            catch (Exception e) { Program.Log(e); Find<CheckBox>("StartToggle").IsChecked = IsStartup(); MessageBox.Show("無法變更開機啟動設定。", "輕聲"); }
            Save();
        }
        private bool IsStartup()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key != null && key.GetValue("MicSwitch.Chinese") != null;
        }
        private void Save() { try { config.Save(); } catch (Exception e) { Program.Log(e); Find<TextBlock>("StateText").Text = "設定未能儲存"; } }
        private void SessionChanged(object sender, SessionSwitchEventArgs e)
        {
            engine.Press(false);
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.RemoteDisconnect || e.Reason == SessionSwitchReason.ConsoleDisconnect) engine.Locked = true;
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.RemoteConnect || e.Reason == SessionSwitchReason.ConsoleConnect) engine.Locked = false;
        }
        private void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) { engine.Press(false); engine.Suspended = true; }
            if (e.Mode == PowerModes.Resume) { engine.Press(false); engine.Suspended = false; engine.Refresh(); }
        }
        internal void EmergencyMute() { engine.Locked = true; engine.Press(false); }
        private void Tick()
        {
            if (closing) return;
            long now = Stopwatch.GetTimestamp(); Interlocked.Exchange(ref engine.UiPulse, now);
            if (now - lastBeat > Stopwatch.Frequency / 3) { guard.Pulse(); lastBeat = now; }
            var s = engine.State;
            timer.Interval = TimeSpan.FromMilliseconds(s.Talking ? 33 : 100);
            string status = s.Error ?? (s.Talking ? "正在說話" : "已靜音");
            Find<TextBlock>("StateText").Text = status;
            Find<TextBlock>("StateText").FontSize = s.Error == null ? 19 : 14;
            Find<TextBlock>("StateText").Foreground = s.Error != null ? red : (s.Talking ? teal : Brushes.White);
            Find<TextBlock>("MicIcon").Foreground = s.Talking ? teal : muted;
            Find<TextBlock>("MicIcon").Text = s.Talking ? "\uE720" : "\uF781";
            double db = s.Talking && s.Peak > 0.000001 ? Math.Max(-120, 20 * Math.Log10(s.Peak)) : Double.NegativeInfinity;
            Find<TextBlock>("LevelText").Text = s.Talking && !s.MeterAvailable ? "音量不可用" : (Double.IsNegativeInfinity(db) ? "−∞ dBFS" : db.ToString("0") + " dBFS");
            Array.Copy(history, 1, history, 0, history.Length - 1);
            history[history.Length - 1] = Double.IsNegativeInfinity(db) ? 0 : Math.Max(0, Math.Min(1, (db + 60) / 60));
            if (!s.Talking) Array.Clear(history, 0, history.Length);
            double width = Math.Max(1, Find<Canvas>("Meter").ActualWidth);
            for (int i = 0; i < bars.Count; i++)
            {
                bars[i].Height = 3 + history[i] * 25; bars[i].Fill = s.Talking ? teal : muted;
                bars[i].Opacity = s.Talking ? 0.4 + 0.6 * i / bars.Count : 0.4;
                Canvas.SetLeft(bars[i], i * width / bars.Count); Canvas.SetTop(bars[i], (30 - bars[i].Height) / 2);
            }
            tray.Text = "輕聲 · " + status;
            if (trayTalking != s.Talking) { trayTalking = s.Talking; tray.Icon = trayTalking ? onIcon : offIcon; }
            string signature = String.Join("|", Array.ConvertAll(s.Devices, d => d.Id + d.Name));
            if (signature != deviceSignature || Find<ComboBox>("DeviceChoice").Items.Count == 0)
            {
                deviceSignature = signature; loading = true;
                var combo = Find<ComboBox>("DeviceChoice"); combo.Items.Clear();
                combo.Items.Add(new DeviceChoice("all", "所有麥克風")); combo.Items.Add(new DeviceChoice("default", "預設通訊麥克風"));
                foreach (var d in s.Devices) combo.Items.Add(d);
                foreach (DeviceChoice d in combo.Items) if (d.Id == config.Device) combo.SelectedItem = d;
                if (combo.SelectedItem == null) { var missing = new DeviceChoice(config.Device, "所選麥克風已離線"); combo.Items.Add(missing); combo.SelectedItem = missing; }
                loading = false;
            }
            if (test) TestTick();
        }
        private void Close()
        {
            if (closing) return; closing = true; timer.Stop();
            SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
            hook.Dispose();
            try { engine.Dispose(); guard.Dispose(); } catch (Exception e) { Program.Log(e); }
            tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose();
            onIcon.Dispose(); offIcon.Dispose();
            config.Left = Window.Left; config.Top = Window.Top; Save();
        }
        private void TestTick()
        {
            double t = testClock.Elapsed.TotalSeconds;
            try
            {
                if (testStep == 0 && t > 2)
                {
                    Check(!engine.State.Talking && engine.State.Error == null, "啟動靜音");
                    Capture("collapsed.png"); mouse_event(0x80, 0, 0, 1, UIntPtr.Zero); testStep++;
                }
                else if (testStep == 1 && t > 3)
                {
                    Check(engine.State.Talking, "側鍵1按下開麥"); Capture("speaking.png");
                    mouse_event(0x100, 0, 0, 1, UIntPtr.Zero); testStep++;
                }
                else if (testStep == 2 && t > 4)
                {
                    Check(!engine.State.Talking, "側鍵1鬆開靜音"); ToggleSettings(); testStep++;
                }
                else if (testStep == 3 && t > 5)
                {
                    Capture("settings.png"); Find<ComboBox>("KeyChoice").SelectedIndex = 1;
                    mouse_event(0x80, 0, 0, 2, UIntPtr.Zero); testStep++;
                }
                else if (testStep == 4 && t > 6)
                {
                    Check(engine.State.Talking, "側鍵2按下開麥");
                    mouse_event(0x100, 0, 0, 2, UIntPtr.Zero); testStep++;
                }
                else if (testStep == 5 && t > 7)
                {
                    Check(!engine.State.Talking, "側鍵2鬆開靜音");
                    engine.Locked = true; mouse_event(0x80, 0, 0, 2, UIntPtr.Zero); testStep++;
                }
                else if (testStep == 6 && t > 8)
                {
                    Check(!engine.State.Talking, "鎖定時不開麥");
                    mouse_event(0x100, 0, 0, 2, UIntPtr.Zero); engine.Locked = false;
                    Find<ComboBox>("KeyChoice").SelectedIndex = 0; testStep++;
                }
                else if (testStep == 7 && t > 9)
                {
                    config.Save(); Check(Settings.Load().SideButton == 1, "設定保存與載入");
                    testLines.Add("PrivateBytes=" + Process.GetCurrentProcess().PrivateMemorySize64);
                    File.WriteAllLines(System.IO.Path.Combine(Settings.Folder, "ui-test.txt"), testLines);
                    test = false; Window.Close();
                }
            }
            catch (Exception e)
            {
                mouse_event(0x100, 0, 0, 1, UIntPtr.Zero); mouse_event(0x100, 0, 0, 2, UIntPtr.Zero);
                testLines.Add("FAIL " + e.Message); File.WriteAllLines(System.IO.Path.Combine(Settings.Folder, "ui-test.txt"), testLines);
                test = false; Window.Close();
            }
        }
        private void Check(bool ok, string message) { if (!ok) throw new Exception(message + ": " + engine.State.Error); testLines.Add("PASS " + message); }
        private void Capture(string file)
        {
            Window.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(Window.ActualWidth), (int)Math.Ceiling(Window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(Window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var f = File.Create(System.IO.Path.Combine(Settings.Folder, file))) encoder.Save(f);
        }
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    }
    internal static class Diagnostics
    {
        internal static int Resilience(string folder)
        {
            Directory.CreateDirectory(folder); var lines = new List<string>();
            List<Endpoint> devices = null; Process app = null;
            try
            {
                devices = Audio.Enumerate();
                if (devices.Count == 0) throw new Exception("沒有可測試的麥克風");
                string exe = Assembly.GetExecutingAssembly().Location;
                app = Process.Start(new ProcessStartInfo(exe, "--profile \"" + folder + "\"") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                Thread.Sleep(2500);
                foreach (var d in devices) if (!d.Muted) throw new Exception("啟動未靜音");
                lines.Add("PASS startup mute (independent endpoint readback)");
                mouse_event(0x80, 0, 0, 1, UIntPtr.Zero); Thread.Sleep(500);
                foreach (var d in devices) if (d.Muted) throw new Exception("按住未開麥");
                lines.Add("PASS XButton1 unmute (independent endpoint readback)");
                mouse_event(0x100, 0, 0, 1, UIntPtr.Zero); Thread.Sleep(400);
                foreach (var d in devices) if (!d.Muted) throw new Exception("鬆開未靜音");
                lines.Add("PASS release mute (independent endpoint readback)");
                app.Refresh(); double cpuBefore = app.TotalProcessorTime.TotalMilliseconds;
                Thread.Sleep(4000); app.Refresh();
                lines.Add("IdleCPU_one_core_percent=" + ((app.TotalProcessorTime.TotalMilliseconds - cpuBefore) / 40).ToString("F2", CultureInfo.InvariantCulture));
                lines.Add("WorkingSetBytes=" + app.WorkingSet64 + "; PrivateBytes=" + app.PrivateMemorySize64);
                mouse_event(0x80, 0, 0, 1, UIntPtr.Zero); Thread.Sleep(500);
                foreach (var d in devices) if (d.Muted) throw new Exception("崩潰測試前未開麥");
                app.Kill(); app.WaitForExit(); mouse_event(0x100, 0, 0, 1, UIntPtr.Zero);
                Thread.Sleep(2500);
                foreach (var d in devices) if (!d.Muted) throw new Exception("異常退出保護未靜音");
                lines.Add("PASS watchdog mutes all endpoints after forced process termination");
                return 0;
            }
            catch (Exception e) { lines.Add("FAIL " + e); return 1; }
            finally
            {
                if (app != null) { try { if (!app.HasExited) { app.Kill(); app.WaitForExit(); Thread.Sleep(2200); } } catch { } app.Dispose(); }
                mouse_event(0x100, 0, 0, 1, UIntPtr.Zero);
                if (devices != null) foreach (var d in devices) { try { d.SetMute(d.OriginalMute); } finally { d.Dispose(); } }
                File.WriteAllLines(System.IO.Path.Combine(folder, "resilience-test.txt"), lines);
            }
        }
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        internal static int Probe(string path, bool test)
        {
            var lines = new List<string>(); List<Endpoint> devices = null;
            try
            {
                devices = Audio.Enumerate(); lines.Add("Devices=" + devices.Count);
                foreach (var d in devices)
                {
                    lines.Add(d.Name + " | " + d.Id + " | Muted=" + d.Muted);
                    if (test)
                    {
                        try
                        {
                            d.SetMute(true); if (!d.Muted) throw new Exception("靜音驗證失敗");
                            d.SetMute(false); if (d.Muted) throw new Exception("開麥驗證失敗");
                            d.StartMeter(); Thread.Sleep(150); lines.Add("PASS mute/unmute; Meter=" + d.MeterAvailable + "; Peak=" + d.Peak().ToString(CultureInfo.InvariantCulture));
                        }
                        finally { d.StopMeter(); d.SetMute(d.OriginalMute); }
                    }
                }
                lines.Add("PASS"); return 0;
            }
            catch (Exception e) { lines.Add("FAIL " + e); return 1; }
            finally
            {
                if (devices != null) foreach (var d in devices) d.Dispose();
                File.WriteAllLines(path, lines);
            }
        }
    }
}
