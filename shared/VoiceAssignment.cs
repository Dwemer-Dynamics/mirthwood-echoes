using System;
using System.Security.Cryptography;
using System.Text;

namespace Mirthwood.Echoes
{
    internal static class VoiceAssignment
    {
        internal static string Key(params string[] parts)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\0", parts))), 0, 16)
                    .Replace("-", "").ToLowerInvariant();
        }

        // Keep the automatic player index and identity-derived pitch stable across releases.
        internal static int Index(string birth,string player,string owner,string native,int count,out int pitch)
        {
            using(var sha=SHA256.Create())
            {
                var hash=sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",birth,player,owner,native)));
                pitch=((hash[2]<<8)|hash[3])%5-2;
                return ((hash[0]<<8)|hash[1])%count;
            }
        }
    }
}
