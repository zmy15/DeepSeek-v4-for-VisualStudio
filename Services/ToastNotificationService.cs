using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Windows Toast 通知服务。
    /// 使用 Windows.UI.Notifications API 发送桌面通知，
    /// 在任务完成或需要用户操作时提醒用户。
    /// 
    /// 要求：Windows 10 1607+，通过 Start Menu 快捷方式注册 AppUserModelID。
    /// 低于 Windows 10 的系统会静默降级，不抛出异常。
    /// </summary>
    public class ToastNotificationService
    {
        private const string AppId = "DeepSeekV4.VisualStudio.Extension";
        private const string ShortcutName = "DeepSeek V4 for Visual Studio.lnk";
        private static readonly string ShortcutDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "DeepSeek V4 for Visual Studio");

        private static readonly string ShortcutPath = Path.Combine(ShortcutDirectory, ShortcutName);

        private bool _initialized;
        private bool _supported;
        private readonly object _initLock = new();

        /// <summary>
        /// 初始化 Toast 通知系统（创建 Start Menu 快捷方式）。
        /// 线程安全，仅执行一次。
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    _supported = CheckPlatformSupport();
                    if (!_supported)
                    {
                        Logger.Info("[Toast] 当前系统不支持 Toast 通知（需 Windows 10 1607+）");
                        return;
                    }

                    EnsureShortcutExists();
                    Logger.Info("[Toast] Toast 通知服务初始化成功");
                }
                catch (Exception ex)
                {
                    _supported = false;
                    Logger.Warn($"[Toast] 初始化 Toast 通知服务失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 异步发送 Toast 通知。
        /// </summary>
        /// <param name="title">通知标题</param>
        /// <param name="message">通知正文</param>
        public Task ShowAsync(string title, string message)
        {
            return Task.Run(() => Show(title, message));
        }

        /// <summary>
        /// 同步发送 Toast 通知（在 UI 线程调用）。
        /// </summary>
        public void Show(string title, string message)
        {
            if (!_initialized)
                Initialize();

            if (!_supported)
                return;

            try
            {
                // 使用 Windows.UI.Notifications API（来自 Microsoft.Windows.SDK.Contracts）
                var template = Windows.UI.Notifications.ToastNotificationManager.GetTemplateContent(
                    Windows.UI.Notifications.ToastTemplateType.ToastText02);

                var textNodes = template.GetElementsByTagName("text");
                textNodes[0].AppendChild(template.CreateTextNode(title ?? ""));
                textNodes[1].AppendChild(template.CreateTextNode(message ?? ""));

                var toast = new Windows.UI.Notifications.ToastNotification(template);
                var notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppId);
                notifier.Show(toast);

                Logger.Debug($"[Toast] 通知已发送: {title}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Toast] 发送通知失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送带进度条的 Toast 通知（用于长时间操作）。
        /// </summary>
        public void ShowProgress(string title, string message, string progressStatus, double progressValue)
        {
            if (!_initialized)
                Initialize();

            if (!_supported)
                return;

            try
            {
                var template = Windows.UI.Notifications.ToastNotificationManager.GetTemplateContent(
                    Windows.UI.Notifications.ToastTemplateType.ToastText03);

                var textNodes = template.GetElementsByTagName("text");
                textNodes[0].AppendChild(template.CreateTextNode(title ?? ""));
                textNodes[1].AppendChild(template.CreateTextNode(message ?? ""));
                textNodes[2].AppendChild(template.CreateTextNode(progressStatus ?? ""));

                // 添加进度条
                var toastElement = template.DocumentElement;
                var progressNode = template.CreateElement("progress");
                progressNode.SetAttribute("title", progressStatus ?? "进度");
                progressNode.SetAttribute("value", progressValue.ToString("F2"));
                progressNode.SetAttribute("status", progressStatus ?? "");
                toastElement.AppendChild(progressNode);

                var toast = new Windows.UI.Notifications.ToastNotification(template);
                var notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppId);
                notifier.Show(toast);

                Logger.Debug($"[Toast] 进度通知已发送: {title} ({progressValue:P0})");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Toast] 发送进度通知失败: {ex.Message}");
            }
        }

        #region Platform Support & Shortcut Management

        private static bool CheckPlatformSupport()
        {
            // Windows 10 (NT 10.0) 或更高版本
            var os = Environment.OSVersion;
            return os.Platform == PlatformID.Win32NT && os.Version.Major >= 10;
        }

        private static void EnsureShortcutExists()
        {
            // 如果快捷方式已存在且指向正确的 exe，跳过创建
            if (File.Exists(ShortcutPath))
            {
                try
                {
                    var existingAumid = GetShortcutAppId(ShortcutPath);
                    if (existingAumid == AppId)
                    {
                        Logger.Debug("[Toast] Start Menu 快捷方式已存在，AUMID 正确");
                        return;
                    }
                }
                catch
                {
                    // 读取失败则重新创建
                }
            }

            CreateShortcut();
        }

        private static void CreateShortcut()
        {
            try
            {
                Directory.CreateDirectory(ShortcutDirectory);

                // 获取 devenv.exe 路径
                var vsExePath = GetVisualStudioExePath();
                if (string.IsNullOrEmpty(vsExePath))
                {
                    Logger.Warn("[Toast] 无法找到 devenv.exe 路径，跳过快捷方式创建");
                    return;
                }

                // 创建 Shell Link
                var shellLink = (IShellLinkW)new CShellLink();

                shellLink.SetPath(vsExePath);
                shellLink.SetArguments("/RootSuffix Exp");
                shellLink.SetWorkingDirectory(Path.GetDirectoryName(vsExePath));
                shellLink.SetDescription("DeepSeek V4 for Visual Studio - AI 编程助手通知");
                shellLink.SetIconLocation(vsExePath, 0);

                // 设置 AppUserModelID
                var propertyStore = (IPropertyStore)shellLink;
                var appIdKey = new PropertyKey(
                    new Guid("{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}"), 5); // PKEY_AppUserModel_ID
                var propVariant = new PropVariant();
                propVariant.SetValue(AppId);
                propertyStore.SetValue(ref appIdKey, ref propVariant);
                propertyStore.Commit();

                // 持久化快捷方式
                var persistFile = (IPersistFile)shellLink;
                persistFile.Save(ShortcutPath, true);

                Logger.Info($"[Toast] 已创建 Start Menu 快捷方式: {ShortcutPath}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Toast] 创建快捷方式失败: {ex.Message}");
                throw;
            }
        }

        private static string? GetVisualStudioExePath()
        {
            // 优先使用当前进程路径（devenv.exe）
            using var process = Process.GetCurrentProcess();
            var mainModule = process.MainModule;
            if (mainModule != null && !string.IsNullOrEmpty(mainModule.FileName))
            {
                var path = mainModule.FileName;
                if (path.EndsWith("devenv.exe", StringComparison.OrdinalIgnoreCase))
                    return path;
            }

            // 备用：通过 VS 安装路径查找
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var vsPath = Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Professional",
                "Common7", "IDE", "devenv.exe");
            if (File.Exists(vsPath))
                return vsPath;

            vsPath = Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Enterprise",
                "Common7", "IDE", "devenv.exe");
            if (File.Exists(vsPath))
                return vsPath;

            vsPath = Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Community",
                "Common7", "IDE", "devenv.exe");
            if (File.Exists(vsPath))
                return vsPath;

            return null;
        }

        private static string? GetShortcutAppId(string shortcutPath)
        {
            var shellLink = (IShellLinkW)new CShellLink();
            var persistFile = (IPersistFile)shellLink;
            persistFile.Load(shortcutPath, 0);

            var propertyStore = (IPropertyStore)shellLink;
            var appIdKey = new PropertyKey(
                new Guid("{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}"), 5);
            var propVariant = new PropVariant();
            propertyStore.GetValue(ref appIdKey, out propVariant);

            return propVariant.GetValue() as string;
        }

        #endregion

        #region COM Interop for Shortcut Management

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath,
                out IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotKey(out ushort pwHotkey);
            void SetHotKey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath,
                out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig]
            int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;

            public PropertyKey(Guid fmtid, uint pid)
            {
                this.fmtid = fmtid;
                this.pid = pid;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            private ushort vt;
            private ushort wReserved1;
            private ushort wReserved2;
            private ushort wReserved3;
            private IntPtr ptrVal;
            // Additional union members omitted - we only need LPWSTR (vt = 31)

            public void SetValue(string value)
            {
                vt = 31; // VT_LPWSTR
                ptrVal = Marshal.StringToCoTaskMemUni(value);
            }

            public object? GetValue()
            {
                if (vt == 31) // VT_LPWSTR
                    return Marshal.PtrToStringUni(ptrVal);
                return null;
            }

            public void Clear()
            {
                if (vt == 31 && ptrVal != IntPtr.Zero) // VT_LPWSTR
                {
                    Marshal.FreeCoTaskMem(ptrVal);
                    ptrVal = IntPtr.Zero;
                }
                vt = 0;
            }
        }

        #endregion
    }
}
