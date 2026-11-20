using System.Security.Cryptography;
using System.Text;

namespace Mirthwood.Echoes
{
    internal static class VoiceAssignment
    {
        // The first-round roster order and these identity fields keep a resident's voice across reloads.
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
