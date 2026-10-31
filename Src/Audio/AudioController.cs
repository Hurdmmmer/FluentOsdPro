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
        private static long _lastNextSwitchTick;
        private static long _lastPrevSwitchTick;

        public static void Init(Action<AudioVolumeController.VolumeState> onSwitched)
        {
            _onSwitched = onSwitched;
        }

        public static void TriggerSwitch(bool next)
        {
            long now = Environment.TickCount & Int32.MaxValue;
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
                            UpdateCachedDefaultDeviceName(newName);
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
                if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device) != 0 || device == null)
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
        private static long _cachedDefaultDeviceTick;

        private static void UpdateCachedDefaultDeviceName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _cachedDefaultDeviceName = name;
                _cachedDefaultDeviceTick = Environment.TickCount & Int32.MaxValue;
            }
        }

        public static string GetDefaultDeviceDisplayName()
        {
            long now = Environment.TickCount & Int32.MaxValue;
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
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
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
        private class MMDeviceEnumeratorComObject
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
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
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
        private const float StepSize = 0.02f;
        private static readonly object VolumeLock = new object();
        private static Action<VolumeState> _onChanged;

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
            lock (VolumeLock)
            {
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
        }

        public static VolumeState GetStateForDevice(string deviceId, string displayName)
        {
            lock (VolumeLock)
            {
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
            lock (VolumeLock)
            {
                IAudioEndpointVolume volume = ActivateVolume();
                if (volume == null)
                {
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

                    Guid ctx = Guid.Empty;
                    hr = volume.SetMasterVolumeLevelScalar(next, ref ctx);
                    if (hr != 0)
                    {
                        Program.LogError("SetMasterVolumeLevelScalar failed, hr=0x" + hr.ToString("X8"), null);
                        return null;
                    }

                    bool muted;
                    volume.GetMute(out muted);
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
        }

        private static VolumeState ToggleMuteCore()
        {
            lock (VolumeLock)
            {
                IAudioEndpointVolume volume = ActivateVolume();
                if (volume == null)
                {
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

                    Guid ctx = Guid.Empty;
                    hr = volume.SetMute(!muted, ref ctx);
                    if (hr != 0)
                    {
                        Program.LogError("SetMute failed, hr=0x" + hr.ToString("X8"), null);
                        return null;
                    }

                    float level;
                    volume.GetMasterVolumeLevelScalar(out level);
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
        }

        private static IAudioEndpointVolume ActivateVolume()
        {
            Guid iid = IidIAudioEndpointVolume;
            return (IAudioEndpointVolume)AudioDeviceSwitcher.ActivateDefaultRenderEndpointInterface(iid);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
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
