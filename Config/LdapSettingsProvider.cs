using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Config;

public class LdapSettingsProvider
{
    private readonly IConfigService _configService;
    private readonly EncryptionHelper _encryptionHelper;

    public LdapSettingsProvider(IConfigService configService, EncryptionHelper encryptionHelper)
    {
        _configService = configService;
        _encryptionHelper = encryptionHelper;
    }

    public async Task<string> GetServerAsync()
    {
        return (await _configService.GetLdapSettingsAsync()).LdapServer;
    }

    public async Task<string> GetDomainAsync()
    {
        return (await _configService.GetLdapSettingsAsync()).LdapDomain;
    }

    public async Task<int> GetPortAsync()
    {
        return (await _configService.GetLdapSettingsAsync()).LdapPort;
    }

    public async Task<string> GetBaseDnAsync()
    {
        return (await _configService.GetLdapSettingsAsync()).LdapBaseDn;
    }

    public async Task<string> GetUsernameAsync()
    {
        return (await _configService.GetLdapSettingsAsync()).LdapUsername;
    }

    public async Task<string> GetPasswordAsync()
    {
        var settings = await _configService.GetLdapSettingsAsync();
        return _encryptionHelper.DecryptString(settings.LdapPassword);
    }

    public async Task<bool> GetSslAsync()
    {
        return (await _configService.GetLdapSettingsAsync()).LdapSsl;
    }

    public async Task<int> GetPageSizeAsync()
    {
        return (await _configService.GetLdapSettingsAsync()).LdapPageSize;
    }
}