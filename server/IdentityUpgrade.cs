using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

internal static class IdentityUpgrade
{
    // Only the installer upgrading a pre-Echoes database sets this marker.
    public static async Task ReprotectKeys(RuntimePaths paths, Database db, IDataProtectionProvider protection)
    {
        var marker = Path.Combine(paths.State, ".upgrade-provider-identity");
        if (!File.Exists(marker)) return;

        var oldProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(paths.State, "keys")),
            options => options.SetApplicationName("MirthwoodAI"));
        var oldVault = oldProvider.CreateProtector("MirthwoodAI.ProviderKeys.v1");
        var newVault = protection.CreateProtector(Providers.KeyPurpose);
        await using var connection = await db.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var wrappedKeys = new List<(string Profile, string Ciphertext)>();
        await using (var select = Database.Command(connection, """
            SELECT key,value->>'secret' FROM configuration
            WHERE key LIKE 'profile/%' AND coalesce(value->>'secret','')<>'' FOR UPDATE
            """))
        await using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) wrappedKeys.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (profile, ciphertext) in wrappedKeys)
        {
            // A restart after commit and before marker removal must be harmless.
            try { newVault.Unprotect(ciphertext); continue; }
            catch (CryptographicException) { }
            var wrapped = newVault.Protect(oldVault.Unprotect(ciphertext));
            await using var update = Database.Command(connection, """
                UPDATE configuration SET value=jsonb_set(value,'{secret}',to_jsonb(@wrapped::text)) WHERE key=@profile
                """, ("wrapped", wrapped), ("profile", profile));
            await update.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        File.Delete(marker);
    }
}
