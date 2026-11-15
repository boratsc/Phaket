using System.Runtime.InteropServices;

namespace Phaket.Core.Accounts;

public interface ILibsecretNative
{
    string? Invoke(string operation, string key, string? value, CancellationToken cancellationToken);
}

public sealed class LinuxLibsecretStore(ILibsecretNative? native = null, TimeSpan? timeout = null) : ISecretStore
{
    private readonly ILibsecretNative _native = native ?? new LibsecretNative();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = await Run("get", key, null, cancellationToken);
        try { return value is null ? null : Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Nieprawidłowe dane magazynu / Invalid secret store data.", ex); }
    }
    public async Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await Run("set", key, Convert.ToBase64String(value), cancellationToken);
    }
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) => await Run("delete", key, null, cancellationToken);

    private async Task<string?> Run(string operation, string key, string? value, CancellationToken cancellationToken)
    {
        if (_native is LibsecretNative) SecretStoreFactory.CheckNativeAccess();
        if (string.IsNullOrEmpty(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Invalid secret key.", nameof(key));
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // Await native cancellation: abandoning a write could silently mutate the keyring later.
            return await Task.Run(() => _native.Invoke(operation, key, value, linked.Token));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new SecretStoreException("SECRET_STORE_TIMEOUT", "Przekroczono czas oczekiwania na magazyn / Secret store timed out."); }
        catch (DllNotFoundException ex)
        { throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Brak biblioteki magazynu / Secret store library unavailable.", ex); }
    }
}

internal sealed class LibsecretNative : ILibsecretNative
{
    private const string Secret = "libsecret-1.so.0", Glib = "libglib-2.0.so.0", Gio = "libgio-2.0.so.0", GObject = "libgobject-2.0.so.0";
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr secret_schema_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string attr1, int type1, [MarshalAs(UnmanagedType.LPUTF8Str)] string attr2, int type2, IntPtr end);
    [DllImport(Secret)] private static extern void secret_schema_unref(IntPtr schema);
    [DllImport(Secret)] private static extern uint secret_error_get_quark();
    [DllImport(Secret)] private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attrs, IntPtr collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attrs, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attrs, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern void secret_password_free(IntPtr password);
    [DllImport(Glib)] private static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);
    [DllImport(Glib)] private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(Glib)] private static extern void g_hash_table_destroy(IntPtr table);
    [DllImport(Glib)] private static extern void g_error_free(IntPtr error);
    [DllImport(Gio)] private static extern IntPtr g_cancellable_new();
    [DllImport(Gio)] private static extern void g_cancellable_cancel(IntPtr cancellable);
    [DllImport(GObject)] private static extern void g_object_unref(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)] private struct GError { public uint Domain; public int Code; public IntPtr Message; }

    public string? Invoke(string operation, string key, string? value, CancellationToken cancellationToken)
    {
        IntPtr schema = IntPtr.Zero, table = IntPtr.Zero, cancel = IntPtr.Zero, error = IntPtr.Zero, password = IntPtr.Zero;
        IntPtr[] strings = [Marshal.StringToCoTaskMemUTF8("service"), Marshal.StringToCoTaskMemUTF8("org.phaket"), Marshal.StringToCoTaskMemUTF8("key"), Marshal.StringToCoTaskMemUTF8(key)];
        try
        {
            schema = secret_schema_new("org.phaket.Secret", 0, "service", 0, "key", 0, IntPtr.Zero);
            IntPtr glib = NativeLibrary.Load(Glib);
            try { table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal")); }
            finally { NativeLibrary.Free(glib); }
            g_hash_table_insert(table, strings[0], strings[1]); g_hash_table_insert(table, strings[2], strings[3]);
            cancel = g_cancellable_new();
            using var registration = cancellationToken.Register(() => g_cancellable_cancel(cancel));
            if (operation == "set")
            {
                int ok = secret_password_storev_sync(schema, table, IntPtr.Zero, "Phaket — " + key, value!, cancel, out error);
                Check(error, cancellationToken); error = IntPtr.Zero;
                if (ok == 0) throw Unavailable("Store returned false.");
            }
            else if (operation == "get")
            {
                password = secret_password_lookupv_sync(schema, table, cancel, out error);
                Check(error, cancellationToken); error = IntPtr.Zero;
                return password == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(password);
            }
            else
            {
                secret_password_clearv_sync(schema, table, cancel, out error);
                Check(error, cancellationToken); error = IntPtr.Zero;
            }
            return null;
        }
        finally
        {
            if (error != IntPtr.Zero) g_error_free(error);
            if (password != IntPtr.Zero) secret_password_free(password);
            if (cancel != IntPtr.Zero) g_object_unref(cancel);
            if (table != IntPtr.Zero) g_hash_table_destroy(table);
            if (schema != IntPtr.Zero) secret_schema_unref(schema);
            foreach (var item in strings) Marshal.FreeCoTaskMem(item);
        }
    }
    private static void Check(IntPtr error, CancellationToken token)
    {
        if (error == IntPtr.Zero) return;
        token.ThrowIfCancellationRequested();
        var e = Marshal.PtrToStructure<GError>(error);
        string detail = Marshal.PtrToStringUTF8(e.Message) ?? "Unknown error";
        throw MapError(e.Domain == secret_error_get_quark(), e.Code, detail);
    }
    internal static SecretStoreException MapError(bool secretDomain, int code, string detail) =>
        secretDomain && code == 2
            ? new SecretStoreException("SECRET_STORE_LOCKED", "Magazyn jest zablokowany / Secret store is locked. " + detail)
            : Unavailable(detail);
    private static SecretStoreException Unavailable(string detail) => new("SECRET_STORE_UNAVAILABLE", "Magazyn jest niedostępny / Secret store is unavailable. " + detail);
}
