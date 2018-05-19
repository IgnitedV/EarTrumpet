﻿using EarTrumpet.DataModel;
using EarTrumpet.Extensions;
using EarTrumpet.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace EarTrumpet.Services
{
    public class AppInformationService
    {
        public static bool IsImmersiveProcess(int processId)
        {
            var processHandle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (processHandle == IntPtr.Zero)
                return false;

            bool result = User32.IsImmersiveProcess(processHandle) != 0;
            Kernel32.CloseHandle(processHandle);
            return result;
        }

        private static bool CanResolveAppByApplicationUserModelId(string aumid)
        {
            try
            {
                var iid = typeof(IShellItem2).GUID;
                Shell32.SHCreateItemInKnownFolder(
                ref FolderIds.AppsFolder,
                Shell32.KF_FLAG_DONT_VERIFY,
                aumid,
                ref iid,
                out IShellItem2 shellitem);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetAppUserModelIdByPid(int processId)
        {
            string appUserModelId = string.Empty;

            var processHandle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (processHandle != IntPtr.Zero)
            {
                int amuidBufferLength = Kernel32.MAX_AUMID_LEN;
                var amuidBuffer = new StringBuilder(amuidBufferLength);

                Kernel32.GetApplicationUserModelId(processHandle, ref amuidBufferLength, amuidBuffer);
                appUserModelId = amuidBuffer.ToString();
                Kernel32.CloseHandle(processHandle);

                //
                // We may receive an AUMID for an app in a package that doesn't have
                // the metadata we need (e.g. Skype). If the AUMID doesn't resolve to
                // an app, we need to bust open the package, find the main app, and
                // retrieve the metadata we need.
                //

                if (!CanResolveAppByApplicationUserModelId(appUserModelId))
                {
                    int packageFamilyNameLength = Kernel32.PACKAGE_FAMILY_NAME_MAX_LENGTH_INCL_Z;
                    var packageFamilyNameBuilder = new StringBuilder(packageFamilyNameLength);

                    int packageRelativeApplicationIdLength = Kernel32.PACKAGE_RELATIVE_APPLICATION_ID_MAX_LENGTH_INCL_Z;
                    var packageRelativeApplicationIdBuilder = new StringBuilder(packageRelativeApplicationIdLength);

                    Kernel32.ParseApplicationUserModelId(
                        appUserModelId,
                        ref packageFamilyNameLength,
                        packageFamilyNameBuilder,
                        ref packageRelativeApplicationIdLength,
                        packageRelativeApplicationIdBuilder);

                    string packageFamilyName = packageFamilyNameBuilder.ToString();

                    int packageCount = 0;
                    int packageNamesBufferLength = 0;
                    Kernel32.FindPackagesByPackageFamilyInitial(
                        packageFamilyName,
                        Kernel32.PACKAGE_FILTER_HEAD | Kernel32.PACKAGE_INFORMATION_BASIC,
                        ref packageCount,
                        IntPtr.Zero,
                        ref packageNamesBufferLength,
                        IntPtr.Zero,
                        IntPtr.Zero);

                    if (packageCount > 0)
                    {
                        var pointers = new IntPtr[packageCount];
                        IntPtr buffer = Marshal.AllocHGlobal(packageNamesBufferLength * 2 /* sizeof(wchar_t) */);

                        Kernel32.FindPackagesByPackageFamily(
                            packageFamilyName,
                            Kernel32.PACKAGE_FILTER_HEAD | Kernel32.PACKAGE_INFORMATION_BASIC,
                            ref packageCount,
                            pointers,
                            ref packageNamesBufferLength,
                            buffer,
                            IntPtr.Zero);

                        var packageFullName = Marshal.PtrToStringUni(pointers[0]);
                        Marshal.FreeHGlobal(buffer);

                        Kernel32.OpenPackageInfoByFullName(packageFullName, 0, out IntPtr packageInfoReference);

                        int bufferLength = 0;
                        Kernel32.GetPackageApplicationIds(packageInfoReference, ref bufferLength, IntPtr.Zero, out int appIdCount);

                        buffer = Marshal.AllocHGlobal(bufferLength);
                        Kernel32.GetPackageApplicationIds(packageInfoReference, ref bufferLength, buffer, out appIdCount);

                        appUserModelId = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer));
                        Marshal.FreeHGlobal(buffer);

                        Kernel32.ClosePackageInfo(packageInfoReference);
                    }
                }
            }

            return appUserModelId;
        }

        public static AppInformation GetInformationForAppByPid(int processId, Action<string> displayNameResolved)
        {
            var appInfo = new AppInformation();

            if (processId == 0)
            {
                return GetInformationForSystemProcess();
            }

            bool shouldResolvedDisplayNameFromMainWindow = true;

            if (IsImmersiveProcess(processId))
            {
                appInfo.AppUserModelId = GetAppUserModelIdByPid(processId);

                var iid = typeof(IShellItem2).GUID;
                Shell32.SHCreateItemInKnownFolder(
                    ref FolderIds.AppsFolder,
                    Shell32.KF_FLAG_DONT_VERIFY,
                    appInfo.AppUserModelId,
                    ref iid,
                    out IShellItem2 shellitem);

                // TODO: thsi should be exe name
                appInfo.ExeName = shellitem.GetString(ref PropertyKeys.PKEY_ItemNameDisplay);

                displayNameResolved(appInfo.ExeName);
                shouldResolvedDisplayNameFromMainWindow = false;

                appInfo.BackgroundColor = shellitem.GetUInt32(ref PropertyKeys.PKEY_AppUserModel_Background);
                appInfo.PackageInstallPath = shellitem.GetString(ref PropertyKeys.PKEY_AppUserModel_PackageInstallPath);
                appInfo.PackageFullName = shellitem.GetString(ref PropertyKeys.PKEY_AppUserModel_PackageFullName);

                string rawSmallLogoPath = shellitem.GetString(ref PropertyKeys.PKEY_Tile_SmallLogoPath);
                if (Uri.IsWellFormedUriString(rawSmallLogoPath, UriKind.RelativeOrAbsolute))
                {
                    appInfo.SmallLogoPath = new Uri(rawSmallLogoPath).LocalPath;
                }
                else
                {
                    iid = typeof(IResourceMap).GUID;
                    var mrtResourceManager = (IMrtResourceManager)new MrtResourceManager();
                    mrtResourceManager.InitializeForPackage(appInfo.PackageFullName);
                    mrtResourceManager.GetMainResourceMap(ref iid, out IResourceMap map);

                    map.GetFilePath(rawSmallLogoPath, out string mrtSmallLogoPath);
                    appInfo.SmallLogoPath = Path.Combine(appInfo.PackageInstallPath, mrtSmallLogoPath);
                }
            }
            else
            {
                appInfo.IsDesktopApp = true;

                const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
                var handle = Kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (handle != IntPtr.Zero)
                {
                    var fileNameBuilder = new StringBuilder(255);
                    uint bufferLength = (uint)fileNameBuilder.Capacity + 1;
                    Kernel32.QueryFullProcessImageName(handle, 0, fileNameBuilder, ref bufferLength);

                    if (fileNameBuilder.Length > 0)
                    {
                        var processFullPath = fileNameBuilder.ToString();
                        appInfo.ExeName = Path.GetFileName(processFullPath);
                        appInfo.SmallLogoPath = processFullPath;
                        appInfo.PackageInstallPath = processFullPath;

                        if (appInfo.ExeName.ToLowerInvariant() == "speechruntime.exe")
                        {
                            displayNameResolved(Properties.Resources.SpeechRuntimeDisplayName);
                            shouldResolvedDisplayNameFromMainWindow = false;
                        }

                        // override for SpeechRuntime.exe (Repo -> HEY CORTANA)
                        if (appInfo.SmallLogoPath.ToLowerInvariant().Contains("speechruntime.exe"))
                        {
                            var sysType = Environment.Is64BitOperatingSystem ? "SysNative" : "System32";
                            appInfo.SmallLogoPath = Path.Combine("%windir%", sysType, "Speech\\SpeechUX\\SpeechUXWiz.exe");
                        }
                    }
                }
            }

            if (shouldResolvedDisplayNameFromMainWindow)
            {
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        var proc = Process.GetProcessById(processId);
                        if (!string.IsNullOrWhiteSpace(proc.MainWindowTitle))
                        {
                            displayNameResolved(proc.MainWindowTitle);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                });
            }

            return appInfo;
        }

        private static AppInformation GetInformationForSystemProcess()
        {
            return new AppInformation()
            {
                DisplayName = Properties.Resources.SystemSoundsDisplayName,
                BackgroundColor = 0x000000,
                PackageInstallPath = "System.SystemSoundsSession",
                IsDesktopApp = true,
                SmallLogoPath = Environment.ExpandEnvironmentVariables(
                    $"%windir%\\{(Environment.Is64BitOperatingSystem ? "sysnative" : "system32")}\\audiosrv.dll")
            };
        }
    }
}
