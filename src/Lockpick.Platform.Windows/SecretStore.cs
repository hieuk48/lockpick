using System.Runtime.InteropServices;
using System.Text;

namespace Lockpick.Platform.Windows;

/// <summary>
/// Keeps small secrets (e.g. the player's speedrun.com API key) encrypted with Windows DPAPI for the current Windows
/// user: only that user, on this PC, can read them back. Each secret is its own file under <paramref name="directory"/>,
/// never in settings.json, so sharing settings or a support bundle can't leak it.
/// </summary>
public sealed partial class SecretStore(string directory)
{
    private const int UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN: never prompt

    /// <summary>Mixed into the encryption so another program running as the same user can't simply unprotect the file
    /// without knowing it is Lockpick's.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lockpick secret store v1");

    public string Directory { get; } = directory;

    public bool Exists(string name) => File.Exists(PathFor(name));

    public void Save(string name, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var sealedBytes = Protect(plain);
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(name);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, sealedBytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>The secret, or null when there is none or it can't be read (another user, another PC, a damaged file).</summary>
    public string? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[]? plain = null;
        try
        {
            plain = Unprotect(File.ReadAllBytes(path));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            if (plain is not null)
            {
                Array.Clear(plain);
            }
        }
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string name) =>
        Path.Combine(Directory, string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".bin");

    private static unsafe byte[] Protect(byte[] plain)
    {
        fixed (byte* data = plain)
        fixed (byte* entropy = Entropy)
        {
            var input = new DataBlob { Size = plain.Length, Data = (nint)data };
            var salt = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            if (!CryptProtectData(ref input, "Lockpick", ref salt, 0, 0, UiForbidden, out var output))
            {
                throw new IOException($"Windows couldn't encrypt the secret (error {Marshal.GetLastPInvokeError()}).");
            }

            return TakeAndFree(output);
        }
    }

    private static unsafe byte[]? Unprotect(byte[] sealedBytes)
    {
        fixed (byte* data = sealedBytes)
        fixed (byte* entropy = Entropy)
        {
            var input = new DataBlob { Size = sealedBytes.Length, Data = (nint)data };
            var salt = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            return CryptUnprotectData(ref input, 0, ref salt, 0, 0, UiForbidden, out var output) ? TakeAndFree(output) : null;
        }
    }

    private static byte[] TakeAndFree(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.Size];
            Marshal.Copy(blob.Data, bytes, 0, blob.Size);
            return bytes;
        }
        finally
        {
            // The unprotected copy in native memory is wiped before it is freed.
            for (var i = 0; i < blob.Size; i++)
            {
                Marshal.WriteByte(blob.Data, i, 0);
            }

            LocalFree(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
