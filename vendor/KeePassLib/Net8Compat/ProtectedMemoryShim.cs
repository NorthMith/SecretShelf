// SecretShelf: замена System.Security.Cryptography.ProtectedMemory, которой нет в .NET 8.
// В .NET Framework этот класс был обёрткой над тем же CryptProtectMemory (DPAPI),
// поэтому поведение ProtectedBinary остаётся прежним: секреты в памяти процесса
// зашифрованы ключом, доступным только этому процессу.
using System;
using System.Runtime.InteropServices;

namespace KeePassLib.Security
{
	internal enum MemoryProtectionScope
	{
		SameProcess = 0, // CRYPTPROTECTMEMORY_SAME_PROCESS
		CrossProcess = 1,
		SameLogon = 2
	}

	internal static class ProtectedMemory
	{
		[DllImport("crypt32.dll", SetLastError = true)]
		private static extern bool CryptProtectMemory(byte[] pData, uint cbData, uint dwFlags);

		[DllImport("crypt32.dll", SetLastError = true)]
		private static extern bool CryptUnprotectMemory(byte[] pData, uint cbData, uint dwFlags);

		public static void Protect(byte[] userData, MemoryProtectionScope scope)
		{
			if(userData == null) throw new ArgumentNullException("userData");
			if(!CryptProtectMemory(userData, (uint)userData.Length, (uint)scope))
				throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
		}

		public static void Unprotect(byte[] encryptedData, MemoryProtectionScope scope)
		{
			if(encryptedData == null) throw new ArgumentNullException("encryptedData");
			if(!CryptUnprotectMemory(encryptedData, (uint)encryptedData.Length, (uint)scope))
				throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
		}
	}
}
