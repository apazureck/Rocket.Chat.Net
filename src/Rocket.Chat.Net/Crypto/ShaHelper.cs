using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Rocket.Chat.Net.Crypto
{
    public class ShaHelper
    {
        public string Sha256Hash(string value)
        {
            var builder = new StringBuilder();
            var encoding = Encoding.UTF8;

            using (var hash = SHA256.Create())
            {
                var result = hash.ComputeHash(encoding.GetBytes(value));
                foreach (var b in result)
                {
                    builder.Append(b.ToString("x2"));
                }
            }

            return builder.ToString();
        }
    }
}
