using System.ComponentModel;
using System.Runtime.InteropServices;
using Clicky.Core.Secrets;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Security;

/// <summary>
/// Secrets in Windows Credential Manager as generic credentials named "Clicky/&lt;name&gt;". Windows
/// encrypts them per user with DPAPI; they show up (and can be removed) under Control Panel ›
/// Credential Manager › Windows Credentials.
/// </summary>
public sealed class CredentialSecretStore : ISecretStore
{
    private const string Prefix = "Clicky/";
    private const int MaxBlobBytes = 2560;

    public string? Read(string name)
    {
        if (!CredRead(Target(name), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error, $"Could not read the credential {Target(name)}.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public unsafe void Write(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length * 2 > MaxBlobBytes)
        {
            throw new ArgumentException($"Secrets are limited to {MaxBlobBytes / 2} characters.", nameof(value));
        }

        var target = Target(name);
        fixed (char* targetName = target)
        fixed (char* userName = "Clicky")
        fixed (char* blob = value)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = (nint)targetName,
                UserName = (nint)userName,
                CredentialBlob = (nint)blob,
                CredentialBlobSize = (uint)(value.Length * 2),
                Persist = CredPersistLocalMachine,
            };

            if (!CredWrite(credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not save the credential {target}.");
            }
        }
    }

    public void Delete(string name)
    {
        if (!CredDelete(Target(name), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, $"Could not delete the credential {Target(name)}.");
            }
        }
    }

    private static string Target(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Prefix + name;
    }
}
