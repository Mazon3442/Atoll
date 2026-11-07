using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Atoll.Modules.Providers;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Settings;

/// <summary>A saved provider account. Keys stay encrypted; only whether one is saved is shown.</summary>
public sealed class ProviderAccountRow(ProviderAccount account)
{
    public ProviderAccount Account { get; } = account;
    public string Name => Account.Name;
    public string Detail => Uri.TryCreate(Account.BaseUrl, UriKind.Absolute, out var uri)
        ? $"{uri.Host} · {(Account.EncryptedAdminKey.Length > 0 ? "API key and metrics key saved" : "API key saved")}"
        : "API key saved";

    /// <summary>The base URL to give an app or SDK instead of the provider's own.</summary>
    public string GatewayUrl => $"http://127.0.0.1:{ProviderGateway.Port}/providers/{Account.Id}/v1";
}

/// <summary>Your own API keys, routed through ATOLL's local gateway (Settings › Agents).</summary>
public sealed partial class SettingsViewModel
{
    public IReadOnlyList<Choice<string>> ProviderPresets { get; } =
    [
        new("https://api.openai.com/v1/", "OpenAI"),
        new("https://api.anthropic.com/v1/", "Anthropic"),
        new("https://api.deepseek.com/v1/", "DeepSeek"),
        new("", "Other (OpenAI-compatible)"),
    ];

    public ObservableCollection<ProviderAccountRow> ProviderAccounts { get; } = [];

    private string _providerPreset = "https://api.openai.com/v1/";
    public string ProviderPreset
    {
        get => _providerPreset;
        set
        {
            if (!SetProperty(ref _providerPreset, value)) return;
            var preset = ProviderPresets.FirstOrDefault(p => p.Value == value);
            NewProviderName = preset is null || value.Length == 0 ? "" : preset.Label;
            NewProviderUrl = value;
        }
    }

    private string _newProviderName = "OpenAI";
    public string NewProviderName { get => _newProviderName; set => SetProperty(ref _newProviderName, value); }

    private string _newProviderUrl = "https://api.openai.com/v1/";
    public string NewProviderUrl { get => _newProviderUrl; set => SetProperty(ref _newProviderUrl, value); }

    private string _providerMessage = "";
    public string ProviderMessage { get => _providerMessage; private set => SetProperty(ref _providerMessage, value); }

    public bool GatewayEnabled
    {
        get => S.Providers.GatewayEnabled;
        set
        {
            if (value) EnsureGatewayToken();
            Set(S.Providers.GatewayEnabled, value, v => S.Providers.GatewayEnabled = v);
        }
    }

    private void LoadProviderAccounts()
    {
        ProviderAccounts.Clear();
        foreach (var account in S.Providers.Accounts) ProviderAccounts.Add(new ProviderAccountRow(account));
    }

    /// <summary>Validates and saves a provider. Keys are encrypted before they reach settings.</summary>
    public bool AddProvider(string apiKey, string metricsKey)
    {
        var name = NewProviderName.Trim();
        if (name.Length is 0 or > 100) { ProviderMessage = "Enter a name for this key (up to 100 characters)."; return false; }
        try
        {
            var account = new ProviderAccount
            {
                Name = name,
                BaseUrl = NewProviderUrl.Trim(),
                EncryptedKey = string.IsNullOrWhiteSpace(apiKey) ? "" : SecretStore.Protect(apiKey.Trim()),
                EncryptedAdminKey = string.IsNullOrWhiteSpace(metricsKey) ? "" : SecretStore.Protect(metricsKey.Trim()),
            };
            _ = ProviderEndpoint.From(account); // same checks the gateway applies
            S.Providers.Accounts.Add(account);
            ProviderAccounts.Add(new ProviderAccountRow(account));
            // Adding a key is the signal to use the feature: switch on the gateway and the island's tab.
            EnsureGatewayToken();
            S.Providers.GatewayEnabled = true;
            OnPropertyChanged(nameof(GatewayEnabled));
            if (S.Modules.FirstOrDefault(m => m.Id == "providers") is { Enabled: false } entry)
            {
                entry.Enabled = true;
                foreach (var tab in ModuleTabs) tab.Refresh();
            }
            ProviderMessage = $"Saved {name}. Point your app at its address below and use the gateway token as its API key.";
            Save();
            return true;
        }
        catch (ArgumentException ex) { ProviderMessage = ex.Message; }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        { ProviderMessage = "Couldn't encrypt this key. Check access to ATOLL's settings folder."; }
        return false;
    }

    [RelayCommand]
    private void RemoveProvider(ProviderAccountRow row)
    {
        S.Providers.Accounts.Remove(row.Account);
        ProviderAccounts.Remove(row);
        ProviderMessage = $"Removed {row.Name} and its saved keys.";
        Save();
    }

    /// <summary>The token apps send to the local gateway, created on first use.</summary>
    public string GatewayToken()
    {
        EnsureGatewayToken();
        return SecretStore.Unprotect(S.Providers.EncryptedGatewayToken) ?? "";
    }

    private void EnsureGatewayToken()
    {
        if (SecretStore.Unprotect(S.Providers.EncryptedGatewayToken) is not { Length: >= 32 })
            S.Providers.EncryptedGatewayToken = SecretStore.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public void ReportProviderMessage(string message) => ProviderMessage = message;
}
