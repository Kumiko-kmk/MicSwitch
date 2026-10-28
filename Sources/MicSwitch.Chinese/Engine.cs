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
        public int SideButton = 1;
        public string Device = "all";
        public bool BlockButton = true;
        public bool Topmost = true;
        public bool KeepMuted = true;
        public double Opacity = 0.96;
        public double Scale = 1;
        public double Left = -10000;
        public double Top = -10000;
        public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicSwitch.Chinese");
        public static Settings Load()
        {
            try
            {
                using (var f = File.OpenRead(Path.Combine(Folder, "settings.xml")))
                {
                    var s = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(f);
                    if (s.SideButton != 1 && s.SideButton != 2) s.SideButton = 1;
                    s.Opacity = Clamp(s.Opacity, 0.4, 1); s.Scale = Clamp(s.Scale, 0.85, 1.5);
                    if (String.IsNullOrEmpty(s.Device)) s.Device = "all";
                    return s;
                }
            }
            catch { return new Settings(); }
        }
        private static double Clamp(double d, double a, double b) { return Double.IsNaN(d) || Double.IsInfinity(d) ? b : Math.Max(a, Math.Min(b, d)); }
        public void Save()
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "settings.xml"), temp = path + ".tmp";
            using (var f = File.Create(temp)) new XmlSerializer(typeof(Settings)).Serialize(f, this);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
    internal sealed class DeviceChoice
    {
        internal readonly string Id;
        internal readonly string Name;
        internal DeviceChoice(string id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }
    internal sealed class Snapshot
    {
        internal bool Talking;
        internal float Peak;
        internal string Error;
        internal bool MeterAvailable = true;
        internal DeviceChoice[] Devices = new DeviceChoice[0];
    }
    internal sealed class Engine : IDisposable
    {
        private readonly Thread thread;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Dictionary<string, Endpoint> devices = new Dictionary<string, Endpoint>();
        internal volatile string Selection = "all";
        internal volatile bool Held, Locked, Suspended;
        internal volatile bool KeepMuted = true;
        internal long UiPulse;
        private volatile bool stopping;
        internal volatile Snapshot State = new Snapshot();
        internal Engine()
        {
            UiPulse = Stopwatch.GetTimestamp();
            thread = new Thread(Run) { IsBackground = true, Name = "麥克風控制" };
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        internal void Press(bool value) { Held = value; wake.Set(); }
        internal void Refresh() { refresh = true; wake.Set(); }
        private volatile bool refresh = true;
        private string defaultId = "";
        private void Discover()
        {
            var found = Audio.Enumerate(); var ids = new HashSet<string>();
            foreach (var d in found)
            {
                ids.Add(d.Id);
                if (devices.ContainsKey(d.Id)) d.Dispose();
                else { devices.Add(d.Id, d); d.SetMute(true); }
            }
            var removed = new List<string>();
            foreach (var id in devices.Keys) if (!ids.Contains(id)) removed.Add(id);
            foreach (var id in removed) { devices[id].Dispose(); devices.Remove(id); }
            defaultId = Audio.DefaultId();
        }
        private void Run()
        {
            long nextScan = 0; string scanError = null;
            try
            {
                while (!stopping)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (refresh || now >= nextScan)
                    {
                        refresh = false; nextScan = now + Stopwatch.Frequency * 2;
                        try { Discover(); scanError = null; }
                        catch (Exception e) { scanError = "無法讀取麥克風"; Program.Log(e); }
                    }
                    // Also closes the gate if the UI/hook thread stalls, the session locks, or sleeps.
                    bool allow = Held && !Locked && !Suspended && (now - Interlocked.Read(ref UiPulse)) < Stopwatch.Frequency;
                    if (!allow) Held = false;
                    string selected = Selection == "default" ? defaultId : Selection;
                    bool exists = Selection == "all" ? devices.Count > 0 : devices.ContainsKey(selected);
                    var state = new Snapshot { Error = scanError };
                    var choices = new List<DeviceChoice>(); int open = 0;
                    foreach (var d in devices.Values)
                    {
                        choices.Add(new DeviceChoice(d.Id, d.Name));
                        bool active = allow && exists && (Selection == "all" || selected == d.Id);
                        try
                        {
                            d.SetMute(!active);
                            if (d.Muted == active) throw new InvalidOperationException("麥克風未接受靜音狀態");
                            if (active)
                            {
                                open++;
                                d.StartMeter(); state.MeterAvailable &= d.MeterAvailable;
                                state.Peak = Math.Max(state.Peak, d.Peak());
                            }
                            else d.StopMeter();
                        }
                        catch (Exception e) { state.Error = "麥克風控制失敗"; Program.Log(e); }
                    }
                    if (!exists) state.Error = devices.Count == 0 ? "未偵測到麥克風" : "所選麥克風已離線";
                    // A partial failure must not be presented as safely muted or speaking.
                    if (state.Error != null)
                    {
                        Held = false;
                        foreach (var d in devices.Values) try { d.SetMute(true); d.StopMeter(); } catch { }
                        state.Peak = 0;
                    }
                    state.Talking = allow && open > 0 && state.Error == null;
                    state.Devices = choices.ToArray(); State = state;
                    wake.WaitOne(state.Talking ? 20 : 100);
                }
            }
            catch (Exception e) { State = new Snapshot { Error = "麥克風控制已停止" }; Program.Log(e); }
            finally
            {
                foreach (var d in devices.Values)
                {
                    try { d.SetMute(KeepMuted || d.OriginalMute); } catch (Exception e) { Program.Log(e); }
                    d.Dispose();
                }
                devices.Clear();
            }
        }
        public void Dispose() { Held = false; stopping = true; wake.Set(); if (!thread.Join(3000)) throw new TimeoutException("麥克風控制未能正常結束"); wake.Dispose(); }
    }
    internal sealed class MouseHook : IDisposable
    {
        private IntPtr handle;
        private readonly HookProc callback;
        private readonly Engine engine;
        internal int Button = 1;
        internal bool Block = true;
        private bool suppressedDown;
        private int suppressedButton;
        internal MouseHook(Engine engine)
        {
            this.engine = engine; callback = OnMouse;
            handle = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private IntPtr OnMouse(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0 && (wp.ToInt32() == 0x020B || wp.ToInt32() == 0x020C))
            {
                var data = (MouseData)Marshal.PtrToStructure(lp, typeof(MouseData));
                int button = (int)(data.mouseData >> 16); bool down = wp.ToInt32() == 0x020B;
                if (!down && suppressedDown && button == suppressedButton)
                {
                    suppressedDown = false; engine.Press(false); return new IntPtr(1);
                }
                if (button == Button)
                {
                    engine.Press(down && !engine.Locked);
                    if (Block)
                    {
                        if (down) { suppressedDown = true; suppressedButton = button; }
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(handle, code, wp, lp);
        }
        public void Dispose() { if (handle != IntPtr.Zero) UnhookWindowsHookEx(handle); handle = IntPtr.Zero; engine.Press(false); }
        [StructLayout(LayoutKind.Sequential)] private struct MouseData { internal int x, y; internal uint mouseData, flags, time; internal UIntPtr extra; }
        private delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string module);
    }
}
