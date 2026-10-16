using System.Runtime.InteropServices;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

/// <summary>Ubuntu Secret Service integration. No subprocesses, plaintext files, or password arguments.</summary>
public sealed class LibSecretCredentialStore : ICredentialStore
{
    private readonly string schemaName;
    public LibSecretCredentialStore() : this("org.rowfinch.SqlPassword") { }
    internal LibSecretCredentialStore(string schemaName) => this.schemaName = schemaName;
    private static readonly Lazy<NativeSymbols> Symbols = new(() => new NativeSymbols());

    public Task<string?> GetAsync(CredentialIdentity identity, CancellationToken cancellationToken) =>
        RunAsync((schema, cancellable, token) =>
        {
            using var attributes = Attributes.ForIdentity(identity);
            using var items = FindUnlocked(schema, attributes.Handle, cancellable, token);
            if (items.Handle == 0) return null;
            var password = Native.secret_password_lookupv_sync(schema, attributes.Handle, cancellable, out var error);
            try
            {
                CheckError(error, token);
                return password == 0 ? null : Marshal.PtrToStringUTF8(password);
            }
            finally { if (password != 0) Native.secret_password_free(password); }
        }, cancellationToken);

    public async Task StoreAsync(CredentialIdentity identity, string label, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Contains('\0')) throw new ArgumentException("A keyring password cannot contain a NUL character.", nameof(password));
        await RunAsync((schema, cancellable, token) =>
        {
            using var attributes = Attributes.ForIdentity(identity);
            var stored = Native.secret_password_storev_sync(schema, attributes.Handle, null, label, password, cancellable, out var error);
            CheckError(error, token);
            if (stored == 0) throw new CredentialStoreException(CredentialStoreFailure.LockedOrDenied);
            return true;
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await RunAsync((schema, cancellable, token) =>
        {
            using var attributes = Attributes.ForProfile(profileId);
            // clearv_sync silently skips locked entries; search/unlock and delete each item instead.
            using var items = FindUnlocked(schema, attributes.Handle, cancellable, token);
            for (var node = items.Handle; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                token.ThrowIfCancellationRequested();
                var item = Marshal.ReadIntPtr(node);
                var deleted = Native.secret_item_delete_sync(item, cancellable, out var error);
                CheckError(error, token);
                if (deleted == 0) throw new CredentialStoreException(CredentialStoreFailure.Failed);
            }
            return true;
        }, cancellationToken);
    }

    private static Items FindUnlocked(nint schema, nint attributes, nint cancellable, CancellationToken token)
    {
        // SECRET_SEARCH_ALL | SECRET_SEARCH_UNLOCK. Never enumerate unrelated keyring entries.
        var items = new Items(Native.secret_service_search_sync(0, schema, attributes, 2 | 4, cancellable, out var error));
        try
        {
            CheckError(error, token);
            for (var node = items.Handle; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                if (Native.secret_item_get_locked(Marshal.ReadIntPtr(node)) != 0)
                    throw new CredentialStoreException(CredentialStoreFailure.LockedOrDenied);
            return items;
        }
        catch { items.Dispose(); throw; }
    }

    private async Task<T> RunAsync<T>(Func<nint, nint, CancellationToken, T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new CredentialStoreException(CredentialStoreFailure.Unavailable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            // libsecret's sync API may prompt or block, so it must run off the UI thread.
            return await Task.Run(() =>
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var types = Attributes.ForSchema();
                var schema = Native.secret_schema_newv(schemaName, 0, types.Handle);
                if (schema == 0) throw new CredentialStoreException(CredentialStoreFailure.Failed);
                var cancellable = Native.g_cancellable_new();
                try
                {
                    using var registration = timeout.Token.Register(() => Native.g_cancellable_cancel(cancellable));
                    return action(schema, cancellable, timeout.Token);
                }
                finally
                {
                    Native.g_object_unref(cancellable);
                    Native.secret_schema_unref(schema);
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CredentialStoreException(CredentialStoreFailure.TimedOut); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw new CredentialStoreException(CredentialStoreFailure.Unavailable); }
    }

    private static void CheckError(nint error, CancellationToken token)
    {
        if (error != 0)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var native = Marshal.PtrToStructure<GError>(error);
                var domain = Marshal.PtrToStringUTF8(Native.g_quark_to_string(native.Domain));
                var failure = domain == "g-dbus-error-quark" && native.Code is 2 or 3 or 4 or 11 or 15
                    ? CredentialStoreFailure.Unavailable
                    : domain == "g-dbus-error-quark" && native.Code is 9 or 10
                        ? CredentialStoreFailure.LockedOrDenied : CredentialStoreFailure.Failed;
                // Native error text is deliberately excluded: credentials never enter diagnostics.
                throw new CredentialStoreException(failure);
            }
            finally { Native.g_error_free(error); }
        }
        token.ThrowIfCancellationRequested();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError { public uint Domain; public int Code; public nint Message; }

    private sealed class Attributes : IDisposable
    {
        public nint Handle { get; }
        private Attributes(bool schema = false)
        {
            var symbols = Symbols.Value;
            Handle = Native.g_hash_table_new_full(symbols.Hash, symbols.Equal, symbols.Free, schema ? 0 : symbols.Free);
        }
        public static Attributes ForSchema()
        {
            var attributes = new Attributes(schema: true);
            // SECRET_SCHEMA_ATTRIBUTE_STRING = 0, represented as GINT_TO_POINTER(0).
            Native.g_hash_table_insert(attributes.Handle, Native.g_strdup("profile"), 0);
            Native.g_hash_table_insert(attributes.Handle, Native.g_strdup("target"), 0);
            return attributes;
        }
        public static Attributes ForProfile(Guid id)
        { var attributes = new Attributes(); attributes.Add("profile", id.ToString("N")); return attributes; }
        public static Attributes ForIdentity(CredentialIdentity identity)
        { var attributes = ForProfile(identity.ProfileId); attributes.Add("target", identity.Target); return attributes; }
        private void Add(string key, string value) => Native.g_hash_table_insert(Handle, Native.g_strdup(key), Native.g_strdup(value));
        public void Dispose() => Native.g_hash_table_unref(Handle);
    }

    private sealed class Items(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;
        public void Dispose() { if (Handle != 0) Native.g_list_free_full(Handle, Symbols.Value.Unref); }
    }

    private sealed class NativeSymbols
    {
        // These libraries stay loaded for process lifetime because native tables retain their function pointers.
        private readonly nint glib = NativeLibrary.Load("libglib-2.0.so.0");
        private readonly nint gobject = NativeLibrary.Load("libgobject-2.0.so.0");
        public nint Hash => NativeLibrary.GetExport(glib, "g_str_hash");
        public nint Equal => NativeLibrary.GetExport(glib, "g_str_equal");
        public nint Free => NativeLibrary.GetExport(glib, "g_free");
        public nint Unref => NativeLibrary.GetExport(gobject, "g_object_unref");
    }

    private static class Native
    {
        private const string Secret = "libsecret-1.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string Gio = "libgio-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";

        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint secret_schema_newv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, nint attributes);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void secret_schema_unref(nint schema);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, out nint error);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int secret_password_storev_sync(nint schema, nint attributes,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string password, nint cancellable, out nint error);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void secret_password_free(nint password);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint secret_service_search_sync(nint service, nint schema, nint attributes, int flags, nint cancellable, out nint error);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int secret_item_get_locked(nint item);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int secret_item_delete_sync(nint item, nint cancellable, out nint error);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint g_hash_table_new_full(nint hash, nint equal, nint keyDestroy, nint valueDestroy);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int g_hash_table_insert(nint table, nint key, nint value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void g_hash_table_unref(nint table);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint g_strdup([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void g_list_free_full(nint list, nint destroy);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void g_error_free(nint error);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint g_quark_to_string(uint quark);
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint g_cancellable_new();
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void g_cancellable_cancel(nint cancellable);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void g_object_unref(nint instance);
    }
}
