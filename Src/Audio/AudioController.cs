using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FluentOsdPro
{
    /// <summary>
    /// Cycles the system default audio render endpoint (speakers / headphones / HDMI / ...)
    /// through the Core Audio COM API.
    /// Switch work runs on ThreadPool threads; the keyboard hook callback only calls <see cref="TriggerSwitch"/>.
    /// </summary>
    internal static class AudioDeviceSwitcher
    {
        private const int EDataFlowRender = 0;      // eRender
        private const int ERoleMultimedia = 1;      // eMultimedia
        private const int DeviceStateActive = 0x1;  // DEVICE_STATE_ACTIVE
        private const int StgmRead = 0;             // STGM_READ
        private const uint ClsCtxAll = 0x17;        // CLSCTX_ALL
        private const ushort VtLpwstr = 31;         // VT_LPWSTR
        private const long MinSameDirectionIntervalMs = 100;

        private static readonly PropertyKey PKeyDeviceFriendlyName = new PropertyKey
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };
        private static readonly PropertyKey PKeyDeviceEnumeratorName = new PropertyKey
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 24
        };

        private static readonly object SwitchLock = new object();
        private static Action<AudioVolumeController.VolumeState> _onSwitched;
        private static bool _busy;
        private static bool _pending;
        private static bool _pendingNext;
        private static long _lastNextSwitchTick = -10000L;
        private static long _lastPrevSwitchTick = -10000L;

        public static void Init(Action<AudioVolumeController.VolumeState> onSwitched)
        {
            _onSwitched = onSwitched;
        }

        public static void TriggerSwitch(bool next)
        {
            long now = Program.MonotonicTickMs();
            long last = next ? _lastNextSwitchTick : _lastPrevSwitchTick;
            if (now - last < MinSameDirectionIntervalMs)
            {
                return;
            }
            if (next)
            {
                _lastNextSwitchTick = now;
            }
            else
            {
                _lastPrevSwitchTick = now;
            }

            bool runNow = false;
            lock (SwitchLock)
            {
                if (_busy)
                {
                    _pending = true;
                    _pendingNext = next;
                }
                else
                {
                    _busy = true;
                    runNow = true;
                }
            }

            if (runNow)
            {
                ThreadPool.QueueUserWorkItem(delegate { SwitchWorker(next); });
            }
        }

        private static void SwitchWorker(bool next)
        {
            try
            {
                while (true)
                {
                    SwitchResult result = DoSwitch(next);
                    if (result != null && !string.IsNullOrEmpty(result.DisplayName))
                    {
                        Program.LogInfo("Ctrl+Volume" + (next ? "Up" : "Down") + " -> switched default audio output to: " + result.DisplayName);
                        if (_onSwitched != null)
                        {
                            AudioVolumeController.VolumeState state = AudioVolumeController.GetStateForDevice(result.DeviceId, result.DisplayName);
                            if (state == null)
                            {
                                state = AudioVolumeController.GetCurrentState();
                            }
                            _onSwitched(state);
                        }
                    }
                    else
                    {
                        Program.LogError("Ctrl+Volume" + (next ? "Up" : "Down") + " -> device switch failed.", null);
                    }

                    bool another = false;
                    lock (SwitchLock)
                    {
                        if (_pending)
                        {
                            _pending = false;
                            next = _pendingNext;
                            another = true;
                        }
                        else
                        {
                            _busy = false;
                        }
                    }

                    if (!another)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (SwitchLock)
                {
                    _pending = false;
                    _busy = false;
                }
                Program.LogError("AudioDeviceSwitcher worker failed", ex);
            }
        }

        private static SwitchResult DoSwitch(bool next)
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                List<AudioDeviceInfo> devices = GetActiveRenderDevices(enumerator);
                if (devices.Count == 0)
                {
                    Program.LogError("No active render devices found.", null);
                    return null;
                }

                string currentId = GetCurrentDefaultDeviceId(enumerator);
                if (currentId == null)
                {
                    Program.LogError("Could not read the current default endpoint.", null);
                    return null;
                }

                int currentIndex = -1;
                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(devices[i].Id, currentId, StringComparison.OrdinalIgnoreCase))
                    {
                        currentIndex = i;
                        break;
                    }
                }

                int targetIndex;
                if (devices.Count == 1)
                {
                    targetIndex = 0;
                }
                else if (currentIndex < 0)
                {
                    targetIndex = next ? 0 : devices.Count - 1;
                }
                else
                {
                    targetIndex = next
                        ? (currentIndex + 1) % devices.Count
                        : (currentIndex - 1 + devices.Count) % devices.Count;
                }

                int attempts = devices.Count;
                while (attempts > 0)
                {
                    attempts--;
                    if (SetDefaultEndpoint(devices[targetIndex].Id))
                    {
                        string newId = GetCurrentDefaultDeviceId(enumerator);
                        bool changed = newId != null &&
                            (string.Equals(devices[targetIndex].Id, currentId, StringComparison.OrdinalIgnoreCase) ||
                             !string.Equals(newId, currentId, StringComparison.OrdinalIgnoreCase));

                        if (!changed)
                        {
                            Program.LogError("Default endpoint did not change after SetDefaultEndpoint (" + devices[targetIndex].DisplayName + ").", null);
                        }
                        else
                        {
                            string newName = GetDefaultDeviceFriendlyName(enumerator);
                            if (string.IsNullOrEmpty(newName))
                            {
                                newName = devices[targetIndex].DisplayName;
                            }
                            _lastProgrammaticSwitchTick = Program.MonotonicTickMs();
                            UpdateCachedDefaultDeviceName(newName);
                            AudioVolumeController.NotifyDefaultEndpointSwitchedProgrammatically();
                            return new SwitchResult
                            {
                                DisplayName = newName,
                                DeviceId = devices[targetIndex].Id
                            };
                        }
                    }

                    targetIndex = next
                        ? (targetIndex + 1) % devices.Count
                        : (targetIndex - 1 + devices.Count) % devices.Count;
                }

                return null;
            }
            catch (Exception ex)
            {
                Program.LogError("AudioDeviceSwitcher.DoSwitch failed", ex);
                return null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }

        private static List<AudioDeviceInfo> GetActiveRenderDevices(IMMDeviceEnumerator enumerator)
        {
            List<AudioDeviceInfo> result = new List<AudioDeviceInfo>();
            IMMDeviceCollection collection;
            if (enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out collection) != 0 || collection == null)
            {
                return result;
            }

            try
            {
                uint count;
                if (collection.GetCount(out count) != 0)
                {
                    return result;
                }

                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (collection.Item(i, out device) != 0 || device == null)
                    {
                        continue;
                    }

                    try
                    {
                        string id;
                        if (device.GetId(out id) != 0 || string.IsNullOrEmpty(id))
                        {
                            continue;
                        }

                        string name = string.Empty;
                        string enumeratorName = string.Empty;
                        IPropertyStore store;
                        if (device.OpenPropertyStore(StgmRead, out store) == 0 && store != null)
                        {
                            try
                            {
                                name = CleanDeviceDisplayName(GetStoreString(store, PKeyDeviceFriendlyName));
                                enumeratorName = GetStoreString(store, PKeyDeviceEnumeratorName);
                            }
                            finally
                            {
                                Marshal.FinalReleaseComObject(store);
                            }
                        }

                        if (string.Equals(enumeratorName, "ROOT", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(enumeratorName, "SWD", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        result.Add(new AudioDeviceInfo(id, name));
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(collection);
            }

            return result;
        }

        public static void LogAvailableDevices()
        {
            try
            {
                IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                try
                {
                    List<AudioDeviceInfo> devices = GetActiveRenderDevices(enumerator);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Active render endpoints in switch cycle: ");
                    for (int i = 0; i < devices.Count; i++)
                    {
                        if (i > 0) sb.Append(" | ");
                        sb.Append(devices[i].DisplayName);
                    }
                    Program.LogInfo(sb.ToString());
                }
                finally
                {
                    Marshal.FinalReleaseComObject(enumerator);
                }
            }
            catch (Exception ex)
            {
                Program.LogError("LogAvailableDevices failed", ex);
            }
        }

        public static object ActivateDefaultRenderEndpointInterface(Guid iid)
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                IMMDevice device;
                int hrDefault = enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device);
                if (hrDefault != 0 || device == null)
                {
                    Program.LogError("GetDefaultAudioEndpoint failed, hr=0x" + hrDefault.ToString("X8"), null);
                    return null;
                }

                try
                {
                    object result;
                    int hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out result);
                    if (hr != 0 || result == null)
                    {
                        Program.LogError("IMMDevice.Activate failed, hr=0x" + hr.ToString("X8"), null);
                        return null;
                    }
                    return result;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }

        public static object ActivateRenderEndpointInterface(string deviceId, Guid iid)
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                IMMDevice device;
                if (enumerator.GetDevice(deviceId, out device) != 0 || device == null)
                {
                    return null;
                }

                try
                {
                    object result;
                    int hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out result);
                    return (hr == 0 && result != null) ? result : null;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }

        private static string _cachedDefaultDeviceName = string.Empty;
        private static long _cachedDefaultDeviceTick = -10000L;
        private static long _lastProgrammaticSwitchTick = -10000L;

        public static long LastProgrammaticSwitchTick => _lastProgrammaticSwitchTick;

        public static void InvalidateDefaultDeviceCache()
        {
            _cachedDefaultDeviceName = string.Empty;
            _cachedDefaultDeviceTick = -10000L;
        }

        private static void UpdateCachedDefaultDeviceName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _cachedDefaultDeviceName = name;
                _cachedDefaultDeviceTick = Program.MonotonicTickMs();
            }
        }

        public static string GetDefaultDeviceDisplayName()
        {
            long now = Program.MonotonicTickMs();
            string cached = _cachedDefaultDeviceName;
            if (!string.IsNullOrEmpty(cached) && (now - _cachedDefaultDeviceTick) >= 0 && (now - _cachedDefaultDeviceTick) < 1500)
            {
                return cached;
            }

            try
            {
                IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                try
                {
                    string name = GetDefaultDeviceFriendlyName(enumerator);
                    if (!string.IsNullOrEmpty(name))
                    {
                        UpdateCachedDefaultDeviceName(name);
                        return name;
                    }
                    return string.Empty;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(enumerator);
                }
            }
            catch (Exception ex)
            {
                Program.LogError("GetDefaultDeviceDisplayName failed", ex);
                return cached ?? string.Empty;
            }
        }

        private static string GetCurrentDefaultDeviceId(IMMDeviceEnumerator enumerator)
        {
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device) != 0 || device == null)
            {
                return null;
            }

            try
            {
                string id;
                return device.GetId(out id) == 0 ? id : null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(device);
            }
        }

        private static string GetDefaultDeviceFriendlyName(IMMDeviceEnumerator enumerator)
        {
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device) != 0 || device == null)
            {
                return string.Empty;
            }

            try
            {
                return GetDeviceFriendlyName(device);
            }
            finally
            {
                Marshal.FinalReleaseComObject(device);
            }
        }

        private static string GetDeviceFriendlyName(IMMDevice device)
        {
            try
            {
                IPropertyStore store;
                if (device.OpenPropertyStore(StgmRead, out store) != 0 || store == null)
                {
                    return string.Empty;
                }

                try
                {
                    return CleanDeviceDisplayName(GetStoreString(store, PKeyDeviceFriendlyName));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(store);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        private static string GetStoreString(IPropertyStore store, PropertyKey key)
        {
            PropVariant value = new PropVariant();
            try
            {
                PropertyKey k = key;
                if (store.GetValue(ref k, out value) == 0 && value.vt == VtLpwstr)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
            catch
            {
            }
            finally
            {
                try
                {
                    PropVariantClear(ref value);
                }
                catch
                {
                }
            }
            return string.Empty;
        }

        private static string CleanDeviceDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            int i = 0;
            while (i < name.Length && char.IsDigit(name[i]))
            {
                i++;
            }
            if (i > 0)
            {
                int j = i;
                while (j < name.Length && name[j] == ' ')
                {
                    j++;
                }
                if (j < name.Length && name[j] == '-')
                {
                    j++;
                    while (j < name.Length && name[j] == ' ')
                    {
                        j++;
                    }
                    return name.Substring(j);
                }
            }
            return name;
        }

        private static bool SetDefaultEndpoint(string deviceId)
        {
            try
            {
                object client = new PolicyConfigClientComObject();
                try
                {
                    int hr;
                    try
                    {
                        hr = ((IPolicyConfig)client).SetDefaultEndpoint(deviceId, ERoleMultimedia);
                    }
                    catch (InvalidCastException)
                    {
                        hr = ((IPolicyConfigWin10)client).SetDefaultEndpoint(deviceId, ERoleMultimedia);
                    }

                    if (hr != 0)
                    {
                        Program.LogError("SetDefaultEndpoint failed, hr=0x" + hr.ToString("X8"), null);
                        return false;
                    }
                    return true;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(client);
                }
            }
            catch (Exception ex)
            {
                Program.LogError("SetDefaultEndpoint failed", ex);
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        internal struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointerValue;

            public string GetString()
            {
                return pointerValue != IntPtr.Zero ? Marshal.PtrToStringUni(pointerValue) : string.Empty;
            }
        }

        private sealed class AudioDeviceInfo
        {
            public readonly string Id;
            public readonly string Name;

            public AudioDeviceInfo(string id, string name)
            {
                Id = id;
                Name = name;
            }

            public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;
        }

        private sealed class SwitchResult
        {
            public string DisplayName;
            public string DeviceId;
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumeratorComObject
        {
        }

        [ComImport]
        [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class PolicyConfigClientComObject
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
            [PreserveSig] int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
        }

        [ComImport]
        [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint propCount);
            [PreserveSig] int GetAt(uint property, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [ComImport]
        [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
        }

        [ComImport]
        [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfigWin10
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int config, ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
        }
    }

    internal static class AudioVolumeController
    {
        private static readonly Guid IidIAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        // Unique event context GUID used by FluentOsdPro when stepping volume or toggling mute via keyboard hook,
        // so IAudioEndpointVolumeCallback.OnNotify can distinguish our own changes from external Bluetooth AVRCP changes.
        private static readonly Guid OurVolumeEventContext = new Guid("7F8A41C2-93E5-4B18-A402-554C564F5344");
        private const float StepSize = 0.02f;
        private const int VolumeOpLockTimeoutMs = 1200;
        private const int PbtApmSuspend = 0x0004;
        private const int PbtApmResumeSuspend = 0x0007;
        private const int PbtApmResumeAutomatic = 0x0012;

        // Strictly separated locks to eliminate Core Audio AB-BA deadlocks:
        // - VolumeOpLock: ONLY guards keyboard/UI volume read-write operations (never held during callback rebind/unbind).
        // - StateLock: ONLY guards tiny in-memory tracking fields (ZERO COM calls inside this lock).
        // - SubscriptionLock: ONLY swaps COM object references in memory (ZERO COM calls inside this lock).
        private static readonly object VolumeOpLock = new object();
        private static readonly object StateLock = new object();
        private static readonly object SubscriptionLock = new object();
        private static Action<VolumeState> _onChanged;

        private static AudioDeviceSwitcher.IMMDeviceEnumerator _notificationEnumerator;
        private static DefaultDeviceNotificationClient _deviceNotificationClient;
        private static IAudioEndpointVolume _subscribedEndpointVolume;
        private static EndpointVolumeCallback _volumeCallback;
        private static string _subscribedDeviceId = string.Empty;
        private static int _lastNotifiedPercent = -1;
        private static bool? _lastNotifiedMuted;
        private static long _lastRebindTick = -10000L;
        private static long _lastPowerResumeScheduleTick = -10000L;
        private static int _rebindGeneration;

        public sealed class VolumeState
        {
            public string DeviceName;
            public float Level; // 0..1
            public bool Muted;

            public int VolumePercent => Level < 0.0f ? -1 : (int)Math.Round(Level * 100.0f);
            public bool IsMuted => Muted;
        }

        public static void Init(Action<VolumeState> onChanged)
        {
            _onChanged = onChanged;
            ThreadPool.QueueUserWorkItem(delegate
            {
                InitializeHardwareVolumeNotifications();
            });
        }

        public static void OnPowerBroadcast(int powerEvent)
        {
            if (powerEvent == PbtApmSuspend)
            {
                // Proactively detach stale endpoint COM pointer (e.g. Bluetooth headset) before suspend/hibernate
                // so resuming without that headset cannot hang Core Audio teardown.
                Interlocked.Increment(ref _rebindGeneration);
                AudioDeviceSwitcher.InvalidateDefaultDeviceCache();
                DetachSubscribedEndpointAsync("PowerSuspend");
                return;
            }

            if (powerEvent == PbtApmResumeSuspend || powerEvent == PbtApmResumeAutomatic)
            {
                long now = Program.MonotonicTickMs();
                lock (StateLock)
                {
                    if ((now - _lastPowerResumeScheduleTick) >= 0 && (now - _lastPowerResumeScheduleTick) < 1200)
                    {
                        return;
                    }
                    _lastPowerResumeScheduleTick = now;
                    _subscribedDeviceId = string.Empty;
                }

                int generation = Interlocked.Increment(ref _rebindGeneration);
                AudioDeviceSwitcher.InvalidateDefaultDeviceCache();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    // Allow Windows Audio & Bluetooth stacks time to finish endpoint re-enumeration after resume
                    Thread.Sleep(1500);
                    if (Volatile.Read(ref _rebindGeneration) != generation)
                    {
                        return;
                    }
                    Program.LogInfo("AudioVolumeController: rebinding default audio endpoint after power resume.");
                    RebindEndpointVolumeNotification(showOsdOnDeviceSwitch: false, expectedGeneration: generation);
                });
            }
        }

        public static void Shutdown()
        {
            Interlocked.Increment(ref _rebindGeneration);

            IAudioEndpointVolume oldVol;
            EndpointVolumeCallback oldCb;
            AudioDeviceSwitcher.IMMDeviceEnumerator oldEnum;
            DefaultDeviceNotificationClient oldClient;

            lock (SubscriptionLock)
            {
                oldVol = _subscribedEndpointVolume;
                oldCb = _volumeCallback;
                _subscribedEndpointVolume = null;
                _volumeCallback = null;

                oldEnum = _notificationEnumerator;
                oldClient = _deviceNotificationClient;
                _notificationEnumerator = null;
                _deviceNotificationClient = null;
            }

            oldCb?.Detach();
            ReleaseEndpointVolumeCom(oldVol, oldCb);

            if (oldEnum != null && oldClient != null)
            {
                try
                {
                    oldEnum.UnregisterEndpointNotificationCallback(oldClient);
                }
                catch
                {
                }
            }

            if (oldEnum != null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(oldEnum);
                }
                catch
                {
                }
            }
        }

        public static void NotifyDefaultEndpointSwitchedProgrammatically()
        {
            int generation = Interlocked.Increment(ref _rebindGeneration);
            ThreadPool.QueueUserWorkItem(delegate
            {
                RebindEndpointVolumeNotification(showOsdOnDeviceSwitch: false, expectedGeneration: generation);
            });
        }

        private static void InitializeHardwareVolumeNotifications()
        {
            try
            {
                bool alreadyRegistered;
                lock (SubscriptionLock)
                {
                    alreadyRegistered = _notificationEnumerator != null;
                }

                if (!alreadyRegistered)
                {
                    AudioDeviceSwitcher.IMMDeviceEnumerator enumerator = (AudioDeviceSwitcher.IMMDeviceEnumerator)new AudioDeviceSwitcher.MMDeviceEnumeratorComObject();
                    DefaultDeviceNotificationClient client = new DefaultDeviceNotificationClient();
                    int hr = enumerator.RegisterEndpointNotificationCallback(client);
                    if (hr == 0)
                    {
                        bool stored = false;
                        lock (SubscriptionLock)
                        {
                            if (_notificationEnumerator == null)
                            {
                                _notificationEnumerator = enumerator;
                                _deviceNotificationClient = client;
                                stored = true;
                            }
                        }

                        if (!stored)
                        {
                            try { enumerator.UnregisterEndpointNotificationCallback(client); } catch { }
                            try { Marshal.FinalReleaseComObject(enumerator); } catch { }
                        }
                    }
                    else
                    {
                        Program.LogError("RegisterEndpointNotificationCallback failed, hr=0x" + hr.ToString("X8"), null);
                        try { Marshal.FinalReleaseComObject(enumerator); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogError("Failed to register IMMNotificationClient", ex);
            }

            int generation = Interlocked.Increment(ref _rebindGeneration);
            RebindEndpointVolumeNotification(showOsdOnDeviceSwitch: false, expectedGeneration: generation);
        }

        private static void DetachSubscribedEndpointAsync(string reason)
        {
            IAudioEndpointVolume oldVol;
            EndpointVolumeCallback oldCb;
            lock (SubscriptionLock)
            {
                oldVol = _subscribedEndpointVolume;
                oldCb = _volumeCallback;
                _subscribedEndpointVolume = null;
                _volumeCallback = null;
            }

            if (oldVol == null && oldCb == null)
            {
                return;
            }

            oldCb?.Detach();
            if (!string.IsNullOrEmpty(reason))
            {
                Program.LogInfo("Detached IAudioEndpointVolumeCallback (" + reason + ").");
            }

            // Always run UnregisterControlChangeNotify + FinalReleaseComObject off any lock and off the rebind path,
            // because a disconnected Bluetooth COM proxy can stall inside AudioSes.dll during teardown.
            ThreadPool.QueueUserWorkItem(delegate
            {
                ReleaseEndpointVolumeCom(oldVol, oldCb);
            });
        }

        private static void ReleaseEndpointVolumeCom(IAudioEndpointVolume vol, EndpointVolumeCallback cb)
        {
            if (vol == null)
            {
                return;
            }

            try
            {
                if (cb != null)
                {
                    vol.UnregisterControlChangeNotify(cb);
                }
            }
            catch
            {
            }

            try
            {
                Marshal.FinalReleaseComObject(vol);
            }
            catch
            {
            }
        }

        private static void RebindEndpointVolumeNotification(bool showOsdOnDeviceSwitch, int expectedGeneration)
        {
            VolumeState switchedState = null;
            try
            {
                DetachSubscribedEndpointAsync(null);
                AudioDeviceSwitcher.InvalidateDefaultDeviceCache();

                if (Volatile.Read(ref _rebindGeneration) != expectedGeneration)
                {
                    return;
                }

                IAudioEndpointVolume vol = ActivateVolume();
                if (vol == null)
                {
                    Program.LogError("RebindEndpointVolumeNotification: ActivateVolume returned null.", null);
                    return;
                }

                float level = 0.0f;
                bool muted = false;
                vol.GetMasterVolumeLevelScalar(out level);
                vol.GetMute(out muted);

                EndpointVolumeCallback newCallback = new EndpointVolumeCallback();
                int hr = vol.RegisterControlChangeNotify(newCallback);
                if (hr != 0)
                {
                    Program.LogError("RegisterControlChangeNotify failed, hr=0x" + hr.ToString("X8"), null);
                    try { Marshal.FinalReleaseComObject(vol); } catch { }
                    return;
                }

                bool superseded = Volatile.Read(ref _rebindGeneration) != expectedGeneration;
                if (superseded)
                {
                    newCallback.Detach();
                    ReleaseEndpointVolumeCom(vol, newCallback);
                    return;
                }

                IAudioEndpointVolume prevVol;
                EndpointVolumeCallback prevCb;
                lock (SubscriptionLock)
                {
                    prevVol = _subscribedEndpointVolume;
                    prevCb = _volumeCallback;
                    _subscribedEndpointVolume = vol;
                    _volumeCallback = newCallback;
                }

                if (prevVol != null || prevCb != null)
                {
                    prevCb?.Detach();
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        ReleaseEndpointVolumeCom(prevVol, prevCb);
                    });
                }

                int pct = (int)Math.Round(level * 100.0f);
                lock (StateLock)
                {
                    _lastNotifiedPercent = pct;
                    _lastNotifiedMuted = muted;
                    _lastRebindTick = Program.MonotonicTickMs();
                }

                string devName = AudioDeviceSwitcher.GetDefaultDeviceDisplayName();
                Program.LogInfo("Bound IAudioEndpointVolumeCallback to default audio endpoint: " + devName + " (Vol=" + pct + "%, Muted=" + muted + ")");
                if (showOsdOnDeviceSwitch)
                {
                    switchedState = new VolumeState
                    {
                        DeviceName = devName,
                        Level = level,
                        Muted = muted
                    };
                }
            }
            catch (Exception ex)
            {
                Program.LogError("RebindEndpointVolumeNotification failed", ex);
            }

            if (switchedState != null && _onChanged != null)
            {
                _onChanged(switchedState);
            }
        }

        private static void OnExternalHardwareVolumeChanged(float level, bool muted)
        {
            // Immediately suppress the native Windows volume flyout triggered by Bluetooth AVRCP
            Program.SuppressNativeVolumeFlyout("BluetoothAvrcpNotify");

            int pct = (int)Math.Round(Math.Max(0.0f, Math.Min(1.0f, level)) * 100.0f);
            lock (StateLock)
            {
                long now = Program.MonotonicTickMs();
                bool inConnectGracePeriod = (now - _lastRebindTick) >= 0 && (now - _lastRebindTick) < 1800;
                if (inConnectGracePeriod)
                {
                    _lastNotifiedPercent = pct;
                    _lastNotifiedMuted = muted;
                    Program.LogInfo("Ignored initial endpoint volume sync during connect grace period (" + pct + "%, Muted=" + muted + ")");
                    return;
                }

                if (_lastNotifiedPercent == pct && _lastNotifiedMuted.HasValue && _lastNotifiedMuted.Value == muted)
                {
                    return;
                }
                _lastNotifiedPercent = pct;
                _lastNotifiedMuted = muted;
            }

            string devName = AudioDeviceSwitcher.GetDefaultDeviceDisplayName();
            Program.LogInfo("External/Bluetooth AVRCP volume change detected -> " + devName + " (" + pct + "%, Muted=" + muted + ")");
            if (_onChanged != null)
            {
                _onChanged(new VolumeState
                {
                    DeviceName = devName,
                    Level = level,
                    Muted = muted
                });
            }
        }

        public static void TriggerStepVolume(bool up)
        {
            bool stepUp = up;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    VolumeState state = StepVolumeCore(stepUp);
                    if (state != null && _onChanged != null)
                    {
                        _onChanged(state);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogError("AudioVolumeController volume step failed", ex);
                }
            });
        }

        public static VolumeState GetCurrentState()
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(VolumeOpLock, VolumeOpLockTimeoutMs, ref lockTaken);
                if (!lockTaken)
                {
                    Program.LogError("GetCurrentState timed out waiting for VolumeOpLock.", null);
                    return null;
                }

                IAudioEndpointVolume volume = ActivateVolume();
                if (volume == null)
                {
                    return null;
                }

                try
                {
                    float level;
                    bool muted;
                    if (volume.GetMasterVolumeLevelScalar(out level) != 0 || volume.GetMute(out muted) != 0)
                    {
                        return null;
                    }
                    return new VolumeState
                    {
                        DeviceName = AudioDeviceSwitcher.GetDefaultDeviceDisplayName(),
                        Level = level,
                        Muted = muted
                    };
                }
                finally
                {
                    Marshal.FinalReleaseComObject(volume);
                }
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(VolumeOpLock);
                }
            }
        }

        public static VolumeState GetStateForDevice(string deviceId, string displayName)
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(VolumeOpLock, VolumeOpLockTimeoutMs, ref lockTaken);
                if (!lockTaken)
                {
                    Program.LogError("GetStateForDevice timed out waiting for VolumeOpLock.", null);
                    return null;
                }

                Guid iid = IidIAudioEndpointVolume;
                IAudioEndpointVolume volume = (IAudioEndpointVolume)AudioDeviceSwitcher.ActivateRenderEndpointInterface(deviceId, iid);
                if (volume == null)
                {
                    return null;
                }

                try
                {
                    float level;
                    bool muted;
                    if (volume.GetMasterVolumeLevelScalar(out level) != 0 || volume.GetMute(out muted) != 0)
                    {
                        return null;
                    }
                    return new VolumeState
                    {
                        DeviceName = displayName,
                        Level = level,
                        Muted = muted
                    };
                }
                finally
                {
                    Marshal.FinalReleaseComObject(volume);
                }
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(VolumeOpLock);
                }
            }
        }

        public static void TriggerToggleMute()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    VolumeState state = ToggleMuteCore();
                    if (state != null && _onChanged != null)
                    {
                        _onChanged(state);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogError("AudioVolumeController mute toggle failed", ex);
                }
            });
        }

        private static VolumeState StepVolumeCore(bool up)
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(VolumeOpLock, VolumeOpLockTimeoutMs, ref lockTaken);
                if (!lockTaken)
                {
                    Program.LogError("StepVolumeCore timed out waiting for VolumeOpLock.", null);
                    return null;
                }

                IAudioEndpointVolume volume = ActivateVolume();
                if (volume == null)
                {
                    Program.LogError("StepVolumeCore: ActivateVolume returned null.", null);
                    return null;
                }

                try
                {
                    float current;
                    int hr = volume.GetMasterVolumeLevelScalar(out current);
                    if (hr != 0)
                    {
                        Program.LogError("GetMasterVolumeLevelScalar failed, hr=0x" + hr.ToString("X8"), null);
                        return null;
                    }

                    float next = up ? current + StepSize : current - StepSize;
                    if (next > 1.0f) next = 1.0f;
                    if (next < 0.0f) next = 0.0f;

                    Guid ctx = OurVolumeEventContext;
                    hr = volume.SetMasterVolumeLevelScalar(next, ref ctx);
                    if (hr != 0)
                    {
                        Program.LogError("SetMasterVolumeLevelScalar failed, hr=0x" + hr.ToString("X8"), null);
                        return null;
                    }

                    bool muted;
                    volume.GetMute(out muted);
                    lock (StateLock)
                    {
                        _lastNotifiedPercent = (int)Math.Round(next * 100.0f);
                        _lastNotifiedMuted = muted;
                    }

                    return new VolumeState
                    {
                        DeviceName = AudioDeviceSwitcher.GetDefaultDeviceDisplayName(),
                        Level = next,
                        Muted = muted
                    };
                }
                finally
                {
                    Marshal.FinalReleaseComObject(volume);
                }
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(VolumeOpLock);
                }
            }
        }

        private static VolumeState ToggleMuteCore()
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(VolumeOpLock, VolumeOpLockTimeoutMs, ref lockTaken);
                if (!lockTaken)
                {
                    Program.LogError("ToggleMuteCore timed out waiting for VolumeOpLock.", null);
                    return null;
                }

                IAudioEndpointVolume volume = ActivateVolume();
                if (volume == null)
                {
                    Program.LogError("ToggleMuteCore: ActivateVolume returned null.", null);
                    return null;
                }

                try
                {
                    bool muted;
                    int hr = volume.GetMute(out muted);
                    if (hr != 0)
                    {
                        Program.LogError("GetMute failed, hr=0x" + hr.ToString("X8"), null);
                        return null;
                    }

                    Guid ctx = OurVolumeEventContext;
                    hr = volume.SetMute(!muted, ref ctx);
                    if (hr != 0)
                    {
                        Program.LogError("SetMute failed, hr=0x" + hr.ToString("X8"), null);
                        return null;
                    }

                    float level;
                    volume.GetMasterVolumeLevelScalar(out level);
                    lock (StateLock)
                    {
                        _lastNotifiedPercent = (int)Math.Round(level * 100.0f);
                        _lastNotifiedMuted = !muted;
                    }

                    return new VolumeState
                    {
                        DeviceName = AudioDeviceSwitcher.GetDefaultDeviceDisplayName(),
                        Level = level,
                        Muted = !muted
                    };
                }
                finally
                {
                    Marshal.FinalReleaseComObject(volume);
                }
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(VolumeOpLock);
                }
            }
        }

        private static IAudioEndpointVolume ActivateVolume()
        {
            Guid iid = IidIAudioEndpointVolume;
            return (IAudioEndpointVolume)AudioDeviceSwitcher.ActivateDefaultRenderEndpointInterface(iid);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioVolumeNotificationData
        {
            public Guid guidEventContext;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bMuted;
            public float fMasterVolume;
            public uint nChannels;
        }

        [ComImport]
        [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolumeCallback
        {
            [PreserveSig]
            int OnNotify(IntPtr pNotify);
        }

        private sealed class EndpointVolumeCallback : IAudioEndpointVolumeCallback
        {
            private volatile bool _detached;

            public void Detach()
            {
                _detached = true;
            }

            public int OnNotify(IntPtr pNotify)
            {
                if (_detached || pNotify == IntPtr.Zero)
                {
                    return 0;
                }

                try
                {
                    AudioVolumeNotificationData data = (AudioVolumeNotificationData)Marshal.PtrToStructure(
                        pNotify,
                        typeof(AudioVolumeNotificationData));

                    // Ignore volume notifications originating from our own keyboard hook StepVolumeCore / ToggleMuteCore
                    if (data.guidEventContext == OurVolumeEventContext)
                    {
                        return 0;
                    }

                    // Pre-emptively arm native volume flyout suppression as soon as hardware/AVRCP callback arrives
                    Program.SuppressNativeVolumeFlyout("EndpointVolumeCallback");

                    float level = data.fMasterVolume;
                    bool muted = data.bMuted;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        if (!_detached)
                        {
                            OnExternalHardwareVolumeChanged(level, muted);
                        }
                    });
                }
                catch
                {
                }

                return 0;
            }
        }

        private sealed class DefaultDeviceNotificationClient : AudioDeviceSwitcher.IMMNotificationClient
        {
            public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState)
            {
                return 0;
            }

            public int OnDeviceAdded(string pwstrDeviceId)
            {
                return 0;
            }

            public int OnDeviceRemoved(string pwstrDeviceId)
            {
                return 0;
            }

            public int OnDefaultDeviceChanged(int flow, int role, string pwstrDefaultDeviceId)
            {
                // EDataFlowRender = 0, ERoleMultimedia = 1
                if (flow != 0 || role != 1)
                {
                    return 0;
                }

                // NEVER acquire any COM-holding lock inside Core Audio's MMDevApi notification thread!
                string newId = pwstrDefaultDeviceId ?? string.Empty;
                long now = Program.MonotonicTickMs();
                lock (StateLock)
                {
                    if (string.Equals(_subscribedDeviceId, newId, StringComparison.OrdinalIgnoreCase) &&
                        (now - _lastRebindTick) >= 0 && (now - _lastRebindTick) < 400)
                    {
                        return 0;
                    }
                    _subscribedDeviceId = newId;
                    _lastRebindTick = now;
                }

                bool wasProgrammatic = (now - AudioDeviceSwitcher.LastProgrammaticSwitchTick) >= 0 &&
                                       (now - AudioDeviceSwitcher.LastProgrammaticSwitchTick) < 600;
                int generation = Interlocked.Increment(ref _rebindGeneration);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    // Allow Bluetooth AVRCP endpoint properties 120ms to settle when newly connected
                    if (!wasProgrammatic)
                    {
                        Thread.Sleep(120);
                    }
                    if (Volatile.Read(ref _rebindGeneration) != generation)
                    {
                        return;
                    }
                    // Never pop up the Volume OSD merely because a Bluetooth headset connected or disconnected;
                    // only rebind silently so subsequent volume adjustments on the headset trigger the OSD.
                    RebindEndpointVolumeNotification(showOsdOnDeviceSwitch: false, expectedGeneration: generation);
                });
                return 0;
            }

            public int OnPropertyValueChanged(string pwstrDeviceId, AudioDeviceSwitcher.PropertyKey key)
            {
                return 0;
            }
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
            [PreserveSig] int UnregisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
            [PreserveSig] int VolumeStepUp(ref Guid eventContext);
            [PreserveSig] int VolumeStepDown(ref Guid eventContext);
            [PreserveSig] int QueryHardwareSupport(out uint support);
            [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
        }
    }
}
