using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MicSwitch.Chinese
{
    // Native Core Audio adapter. The upstream controller's hold semantics are preserved:
    // MicrophoneControllerViewModel: Controller.Mute = !isActive (PushToTalk).
    internal static class Audio
    {
        internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        internal static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        internal static List<Endpoint> Enumerate()
        {
            var result = new List<Endpoint>();
            var e = (IMMDeviceEnumerator)new DeviceEnumerator();
            IMMDeviceCollection collection = null;
            try
            {
                Check(e.EnumAudioEndpoints(1, 1, out collection));
                uint count; Check(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device; Check(collection.Item(i, out device));
                    try { result.Add(new Endpoint(device)); } catch { Release(device); throw; }
                }
                return result;
            }
            catch { foreach (var d in result) d.Dispose(); throw; }
            finally { Release(collection); Release(e); }
        }
        internal static string DefaultId()
        {
            var e = (IMMDeviceEnumerator)new DeviceEnumerator(); IMMDevice d = null;
            try { Check(e.GetDefaultAudioEndpoint(1, 2, out d)); string id; Check(d.GetId(out id)); return id; }
            catch { return ""; }
            finally { Release(d); Release(e); }
        }
    }
    internal sealed class Endpoint : IDisposable
    {
        internal readonly string Id, Name;
        internal readonly bool OriginalMute;
        private IMMDevice device;
        private IAudioEndpointVolume volume;
        private IAudioMeterInformation meter;
        private IAudioClient client;
        private IAudioCaptureClient capture;
        private bool captureFailed;
        internal Endpoint(IMMDevice d)
        {
            device = d; string id; Audio.Check(d.GetId(out id)); Id = id;
            IPropertyStore props = null;
            try
            {
                Audio.Check(d.OpenPropertyStore(0, out props));
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PropVariant v; Audio.Check(props.GetValue(ref key, out v));
                try { Name = Marshal.PtrToStringUni(v.pointer) ?? "麥克風"; }
                finally { PropVariantClear(ref v); }
            }
            catch { Name = "麥克風"; }
            finally { Audio.Release(props); }
            object obj; var iid = typeof(IAudioEndpointVolume).GUID;
            Audio.Check(d.Activate(ref iid, 23, IntPtr.Zero, out obj)); volume = (IAudioEndpointVolume)obj;
            try
            {
                iid = typeof(IAudioMeterInformation).GUID;
                Audio.Check(d.Activate(ref iid, 23, IntPtr.Zero, out obj)); meter = (IAudioMeterInformation)obj;
                OriginalMute = Muted;
            }
            catch { Audio.Release(meter); meter = null; Audio.Release(volume); volume = null; throw; }
        }
        internal bool Muted { get { bool b; Audio.Check(volume.GetMute(out b)); return b; } }
        internal void SetMute(bool muted) { if (Muted != muted) Audio.Check(volume.SetMute(muted, IntPtr.Zero)); }
        internal bool MeterAvailable { get { return !captureFailed; } }
        internal void StartMeter()
        {
            if (client != null || captureFailed) return;
            IntPtr format = IntPtr.Zero;
            try
            {
                object obj; Guid iid = typeof(IAudioClient).GUID;
                Audio.Check(device.Activate(ref iid, 23, IntPtr.Zero, out obj)); client = (IAudioClient)obj;
                Audio.Check(client.GetMixFormat(out format));
                Audio.Check(client.Initialize(0, 0, 1000000, 0, format, IntPtr.Zero));
                iid = typeof(IAudioCaptureClient).GUID;
                Audio.Check(client.GetService(ref iid, out obj)); capture = (IAudioCaptureClient)obj;
                Audio.Check(client.Start());
            }
            catch { StopMeter(); captureFailed = true; }
            finally { if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format); }
        }
        internal float Peak()
        {
            if (capture != null)
            {
                uint next; Audio.Check(capture.GetNextPacketSize(out next)); int limit = 40;
                while (next > 0 && limit-- > 0)
                {
                    IntPtr data; uint frames, flags; ulong position, clock;
                    Audio.Check(capture.GetBuffer(out data, out frames, out flags, out position, out clock));
                    // Audio stays in Windows' transient buffer: never copied, recorded or sent anywhere.
                    Audio.Check(capture.ReleaseBuffer(frames));
                    Audio.Check(capture.GetNextPacketSize(out next));
                }
            }
            float p; Audio.Check(meter.GetPeakValue(out p)); return p;
        }
        internal void StopMeter()
        {
            if (client != null) { try { client.Stop(); } catch { } }
            Audio.Release(capture); capture = null; Audio.Release(client); client = null;
            captureFailed = false;
        }
        public void Dispose() { StopMeter(); Audio.Release(meter); meter = null; Audio.Release(volume); volume = null; Audio.Release(device); device = null; }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { internal Guid fmtid; internal uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] internal ushort vt; [FieldOffset(8)] internal IntPtr pointer; }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value); [PreserveSig] int Commit();
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr p); [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
        [PreserveSig] int GetChannelCount(out uint n);
        [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr context); [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float level); [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr context); [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level); [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count); [PreserveSig] int VolumeStepUp(IntPtr p); [PreserveSig] int VolumeStepDown(IntPtr p);
        [PreserveSig] int QueryHardwareSupport(out uint mask); [PreserveSig] int GetVolumeRange(out float min, out float max, out float step);
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float value); [PreserveSig] int GetMeteringChannelCount(out int count);
        [PreserveSig] int GetChannelsPeakValues(int count, IntPtr values); [PreserveSig] int QueryHardwareSupport(out int mask);
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int share, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames); [PreserveSig] int GetStreamLatency(out long latency); [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int share, IntPtr format, out IntPtr closest); [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long min); [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle); [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong clock);
        [PreserveSig] int ReleaseBuffer(uint frames); [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
