using Microsoft.AspNetCore.DataProtection;
using System;

namespace ADManagerAPI.Config
{
    public class EncryptionHelper
    {
        private readonly IDataProtectionProvider _dataProtectionProvider;
        private const string Key = "LdapSecretKey";

        public EncryptionHelper(IDataProtectionProvider dataProtectionProvider)
        {
            _dataProtectionProvider = dataProtectionProvider;
        }

        public string EncryptString(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            var protector = _dataProtectionProvider.CreateProtector(Key);
            return protector.Protect(input);
        }

        public string DecryptString(string encryptedInput)
        {
            if (string.IsNullOrEmpty(encryptedInput)) return encryptedInput;
            
            var protector = _dataProtectionProvider.CreateProtector(Key);
            return protector.Unprotect(encryptedInput);
        }
    }
} 