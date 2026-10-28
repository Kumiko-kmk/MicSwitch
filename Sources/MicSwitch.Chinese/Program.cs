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

[assembly: AssemblyTitle("MicSwitch 中文轻量版")]
[assembly: AssemblyDescription("按住热键说话，松开即静音")]
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]

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
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            if(args.Length>1&&args[0]=="--wait-parent") { try{Process.GetProcessById(Int32.Parse(args[1])).WaitForExit(5000);}catch(ArgumentException){} }
            if (args.Length > 0 && args[0] == "--guard") return Guardian.Run(Int32.Parse(args[1]), args[2]);
            if (args.Length > 1 && args[0] == "--probe") return Diagnostics.Probe(args[1], false);
            if (args.Length > 1 && args[0] == "--audio-test") return Diagnostics.Probe(args[1], true);

            if (args.Length > 1 && args[0] == "--profile") Settings.Folder = System.IO.Path.GetFullPath(args[1]);
            bool test = args.Length > 1 && args[0] == "--ui-test";
            if (test) Settings.Folder = System.IO.Path.GetFullPath(args[1]);
            bool created;
            using (var mutex = new Mutex(true, "Local\\MicSwitch.Chinese.SingleInstance", out created))
            {
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                if (!created) { MessageBox.Show("麦克风已在运行，请从系统托盘打开。", "麦克风"); return 0; }
                if (Process.GetProcessesByName("MicSwitch").Length > 0)
                {
                    MessageBox.Show("请先退出原版 MicSwitch，再启动麦克风。", "麦克风"); return 1;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var window = new Shell(test);
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log(e.Exception); window.EmergencyMute();
                        MessageBox.Show("程序发生错误，已要求麦克风静音。请重新打开麦克风。", "麦克风");
                        e.Handled = true; window.Window.Close();
                    };
                    return app.Run(window.Window);
                }
                catch (Exception e) { Log(e); MessageBox.Show("无法启动麦克风，请确认麦克风可用。", "麦克风"); return 1; }
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
            if (!ready.WaitOne(5000)) throw new InvalidOperationException("静音保护无法启动");
        }
        internal void Pulse() { if (child.HasExited) throw new InvalidOperationException("静音保护已停止"); pulse.Set(); }
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

}
