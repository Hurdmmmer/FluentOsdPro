using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace FluentOsdPro
{
    internal static class AutoStartManager
    {
        private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "FluentOsdPro";
        private const string LegacyRunValueName = "UULocalVolumeGuard";
        private const string AutoStartTaskName = "FluentOsdPro";
        private const string LegacyAutoStartTaskName = "UULocalVolumeGuard";

        private static bool _cachedAutoStartEnabled;

        public static bool IsAutoStartEnabled()
        {
            return _cachedAutoStartEnabled;
        }

        public static void RefreshCache()
        {
            _cachedAutoStartEnabled = QueryAutoStartEnabledCore();
        }

        public static void SetAutoStart(bool enable)
        {
            _cachedAutoStartEnabled = enable;
            ThreadPool.QueueUserWorkItem(delegate
            {
                SetAutoStartCore(enable);
            });
        }

        public static void SetAutoStartImmediate(bool enable)
        {
            _cachedAutoStartEnabled = enable;
            SetAutoStartCore(enable);
        }

        public static void MigrateAutoStartToTask()
        {
            try
            {
                bool legacy = false;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false))
                    {
                        if (key != null)
                        {
                            object val = key.GetValue(RunValueName);
                            legacy = val != null && !string.IsNullOrEmpty(val.ToString());
                        }
                    }
                }
                catch
                {
                }

                string taskFile = GetTaskFilePath();
                bool taskExists = File.Exists(taskFile) || AutoStartTaskExists();
                bool legacyTaskExists = RunSchTasks("/Query /TN \"" + LegacyAutoStartTaskName + "\"") == 0;

                if ((legacy || legacyTaskExists) && !taskExists)
                {
                    SetAutoStartCore(true);
                    Program.LogInfo("Migrated legacy startup entry/task to FluentOsdPro elevated logon task (silent startup).");
                }
                else if (File.Exists(taskFile))
                {
                    try
                    {
                        string xml = File.ReadAllText(taskFile, Encoding.Unicode);
                        if (xml.IndexOf("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            xml.IndexOf("<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            xml.IndexOf("<ExecutionTimeLimit>PT72H</ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            SetAutoStartCore(true);
                            Program.LogInfo("Upgraded existing logon task XML to allow battery startup and unlimited execution time.");
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogError("Auto-start migration failed", ex);
            }
            finally
            {
                _cachedAutoStartEnabled = QueryAutoStartEnabledCore();
            }
        }

        private static bool QueryAutoStartEnabledCore()
        {
            if (AutoStartTaskExists())
            {
                return true;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(RunValueName);
                        if (val != null && !string.IsNullOrEmpty(val.ToString()))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static string GetTaskFilePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"Tasks\" + AutoStartTaskName);
        }

        private static string BuildSilentLogonTaskXml(string exePath, string workDir, string userId)
        {
            string escapedExe = System.Security.SecurityElement.Escape(exePath);
            string escapedDir = System.Security.SecurityElement.Escape(workDir);
            string escapedUser = System.Security.SecurityElement.Escape(userId);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Author>" + escapedUser + "</Author>");
            sb.AppendLine("    <Description>FluentOsdPro silent elevated logon startup</Description>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    <LogonTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("    </LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>" + escapedUser + "</UserId>");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("    <RestartOnFailure>");
            sb.AppendLine("      <Interval>PT1M</Interval>");
            sb.AppendLine("      <Count>3</Count>");
            sb.AppendLine("    </RestartOnFailure>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>\"" + escapedExe + "\"</Command>");
            sb.AppendLine("      <WorkingDirectory>" + escapedDir + "</WorkingDirectory>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private static void SetAutoStartCore(bool enable)
        {
            try
            {
                if (enable)
                {
                    string exePath = Assembly.GetExecutingAssembly().Location;
                    string workDir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                    string userId = Environment.UserDomainName + "\\" + Environment.UserName;
                    try
                    {
                        using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                        {
                            if (id != null && !string.IsNullOrEmpty(id.Name))
                            {
                                userId = id.Name;
                            }
                        }
                    }
                    catch
                    {
                    }

                    string tempXml = Path.Combine(Path.GetTempPath(), "FluentOsdPro_Task.xml");
                    int exitCode = -1;
                    try
                    {
                        File.WriteAllText(tempXml, BuildSilentLogonTaskXml(exePath, workDir, userId), Encoding.Unicode);
                        exitCode = RunSchTasks("/Create /TN \"" + AutoStartTaskName + "\" /XML \"" + tempXml + "\" /F");
                    }
                    finally
                    {
                        try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
                    }

                    if (exitCode != 0)
                    {
                        string args = "/Create /TN \"" + AutoStartTaskName + "\" /TR \"\\\"" + exePath + "\\\"\" /SC ONLOGON /RL HIGHEST /F";
                        RunSchTasks(args);
                    }
                    RunSchTasks("/Delete /TN \"" + LegacyAutoStartTaskName + "\" /F");
                    RemoveLegacyRunEntry();
                    Program.LogInfo("Registered elevated logon task for silent startup (no UAC prompt, battery/timeout unrestricted).");
                }
                else
                {
                    RunSchTasks("/Delete /TN \"" + AutoStartTaskName + "\" /F");
                    RunSchTasks("/Delete /TN \"" + LegacyAutoStartTaskName + "\" /F");
                    RemoveLegacyRunEntry();
                    Program.LogInfo("Removed the logon task.");
                }
            }
            catch (Exception ex)
            {
                Program.LogError("Failed to update the auto-start task", ex);
            }
        }

        private static bool AutoStartTaskExists()
        {
            try
            {
                if (File.Exists(GetTaskFilePath()))
                {
                    return true;
                }
                return RunSchTasks("/Query /TN \"" + AutoStartTaskName + "\"") == 0;
            }
            catch
            {
                return false;
            }
        }

        private static int RunSchTasks(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private static void RemoveLegacyRunEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
