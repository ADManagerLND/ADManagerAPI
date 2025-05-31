using Microsoft.Extensions.Configuration;
using ADManagerAPI.Services.Interfaces;
using System.Threading.Tasks;

namespace ADManagerAPI.Config
{
    public class LdapSettingsProvider
    {
        private readonly IConfigService _configService;
        private readonly EncryptionHelper _encryptionHelper;

        public LdapSettingsProvider(IConfigService configService, EncryptionHelper encryptionHelper)
        {
            _configService = configService;
            _encryptionHelper = encryptionHelper;
        }

        public async Task<string> GetServerAsync() => (await _configService.GetLdapSettingsAsync()).LdapServer;
        public async Task<string> GetDomainAsync() => (await _configService.GetLdapSettingsAsync()).LdapDomain;
        public async Task<int> GetPortAsync() => (await _configService.GetLdapSettingsAsync()).LdapPort;
        public async Task<string> GetBaseDnAsync() => (await _configService.GetLdapSettingsAsync()).LdapBaseDn;
        public async Task<string> GetUsernameAsync() => (await _configService.GetLdapSettingsAsync()).LdapUsername;
        
        public async Task<string> GetPasswordAsync()
        {
            var settings = await _configService.GetLdapSettingsAsync();
            return _encryptionHelper.DecryptString(settings.LdapPassword);
        }
        
        public async Task<bool> GetSslAsync() => (await _configService.GetLdapSettingsAsync()).LdapSsl;
        public async Task<int> GetPageSizeAsync() => (await _configService.GetLdapSettingsAsync()).LdapPageSize;
    }
} 