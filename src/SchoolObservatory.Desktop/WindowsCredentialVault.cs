using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SchoolObservatory.Desktop;

/// <summary>
/// An explicitly opted-in Windows-only credential vault. Windows Credential Manager
/// encrypts the generic credential under the current Windows user. No password is
/// written to SQLite, settings, logs, source control or an application plaintext file.
/// </summary>
internal static class WindowsCredentialVault
{
    private const string TargetName = "SchoolObservatory/LocalDatabaseLogin";
    private const int CredentialTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);

    internal static void Save(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) ||
            username.Length > 64 || Encoding.Unicode.GetByteCount(password) > 512)
            throw new ArgumentException("بيانات تذكر تسجيل الدخول غير صالحة.");
        // Windows copies the secret into its encrypted credential store.
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var item = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName,
                UserName = username,
                CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(password),
                CredentialBlob = blob,
                Persist = PersistLocalMachine
            };
            if (!CredWrite(ref item, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "تعذر حفظ تسجيل الدخول بأمان في مدير بيانات اعتماد ويندوز.");
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    internal static (string Username, string Password)? Read()
    {
        if (!CredRead(TargetName, CredentialTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "تعذر قراءة بيانات الدخول المحفوظة من ويندوز.");
        }
        try
        {
            var item = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (item.CredentialBlobSize > 512 || item.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("بيانات اعتماد محلية غير صالحة.");
            return (item.UserName, Marshal.PtrToStringUni(
                item.CredentialBlob, (int)item.CredentialBlobSize / 2) ?? "");
        }
        finally
        {
            CredFree(ptr);
        }
    }

    internal static void Delete()
    {
        if (CredDelete(TargetName, CredentialTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "تعذر إزالة بيانات الدخول المحفوظة من ويندوز.");
    }
}
