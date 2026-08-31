using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RtspCameraSetup;

/// <summary>
/// Remembers per-camera logins so a known camera connects on one click.
///
/// Passwords are encrypted with DPAPI under the current Windows user before they
/// touch disk, so the file is useless if copied to another machine or account. That
/// is weaker than a real secret store but strictly better than the plaintext JSON
/// this would otherwise be, and it needs no extra service or prompt.
/// </summary>
public sealed class CredentialStore
{
    private sealed class Entry
    {
        public string Username { get; set; } = "";

        /// <summary>DPAPI blob, base64. Never the password itself.</summary>
        public string Protected { get; set; } = "";
    }

    private readonly string _path;
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public CredentialStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CameraSetup", "credentials.json");

        Load();
    }

    /// <summary>Where the encrypted store lives. Shown on the About tab.</summary>
    public string StorePath => _path;

    public (string User, string Password)? TryGet(string address)
    {
        if (!_entries.TryGetValue(address, out var entry)) return null;

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(entry.Protected), null, DataProtectionScope.CurrentUser);

            return (entry.Username, Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            // Written by a different user, or the profile was rebuilt. Treat as absent.
            _entries.Remove(address);
            return null;
        }
    }

    public void Save(string address, string username, string password)
    {
        try
        {
            var blob = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);

            _entries[address] = new Entry
            {
                Username = username,
                Protected = Convert.ToBase64String(blob)
            };

            Persist();
        }
        catch
        {
            // Never let a credential-saving failure break the connection that worked.
        }
    }

    public void Forget(string address)
    {
        if (_entries.Remove(address)) Persist();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path))
                       ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Options));
        }
        catch
        {
            // Best effort - the app stays usable without persistence.
        }
    }
}
