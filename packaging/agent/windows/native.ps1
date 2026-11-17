if (-not ('Vigiloom.Setup.Native' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vigiloom.Setup {
    public static class Native {
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Id; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] struct ServiceSidInfo { public uint Type; }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool LookupPrivilegeValue(string system, string name, out Luid id);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr returned);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref ServiceSidInfo info);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool CloseServiceHandle(IntPtr service);

        static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public static void EnableRestorePrivilege() {
            IntPtr token;
            Check(OpenProcessToken(GetCurrentProcess(), 0x28, out token));
            try {
                Luid id; Check(LookupPrivilegeValue(null, "SeRestorePrivilege", out id));
                var privileges = new TokenPrivileges { Count = 1, Id = id, Attributes = 2 };
                Check(AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero));
                int error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error);
            } finally { CloseHandle(token); }
        }
        public static void Create(string name, string binary, string account, bool serviceSid) {
            Create(name, binary, account, serviceSid, false);
        }
        public static void Create(string name, string binary, string account, bool serviceSid, bool manual) {
            var manager = OpenSCManager(null, null, 2);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                // Windows PowerShell converts a null string argument to an empty string.
                var service = CreateService(manager, name, name, 0xF01FF, 0x10, manual ? 3u : 2u, 1, binary, null, IntPtr.Zero, null,
                    string.IsNullOrEmpty(account) ? null : account, null);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try {
                    if (serviceSid) { var info = new ServiceSidInfo { Type = 1 }; Check(ChangeServiceConfig2(service, 5, ref info)); }
                } catch { DeleteService(service); throw; }
                finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
        public static void Delete(string name) {
            var manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var service = OpenService(manager, name, 0x10000);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Check(DeleteService(service)); } finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
    }
}
'@
}
