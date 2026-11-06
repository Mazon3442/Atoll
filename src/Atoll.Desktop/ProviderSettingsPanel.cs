using System.Security.Cryptography;
using Atoll.Desktop.Providers;
using Atoll.Settings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Atoll.Desktop;

public sealed partial class SettingsWindow
{
    private Control ProviderSettingsPanel()
    {
        var settings = _settings.Current.Providers;
        var status = Views.Label("", 12);
        var enabled = Check("Enable local API gateway", settings.GatewayEnabled, value =>
        {
            if (value && SecretStore.Unprotect(settings.EncryptedGatewayToken) is not { Length: >= 32 })
                settings.EncryptedGatewayToken = SecretStore.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            settings.GatewayEnabled = value;
        });
        var copy = Views.Button("Copy gateway token", () => { });
        copy.Click += async (_, _) =>
        {
            try
            {
                if (SecretStore.Unprotect(settings.EncryptedGatewayToken) is not { Length: >= 32 } token) { status.Text = "Enable the gateway first to create its local token."; return; }
                if (Clipboard is not null) { await Clipboard.SetDataAsync(GatewayClipboardData(token)); status.Text = "Local gateway token copied. Use it as the harness API key."; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { status.Text = "Could not copy the gateway token."; }
        };
        var panel = Views.Stack(Views.Label("Provider API keys", 24),
            Views.Label("Point your harness at the local base URL below and use ATOLL's gateway token as its API key. ATOLL forwards to your provider and shows request activity and token totals. Requests made directly to a provider are visible only in provider account metrics, where available.", 12),
            enabled, copy, status,
            Views.Label("OpenAI-compatible Chat Completions / Responses and Anthropic Messages are supported. Model names pass through unchanged. Keys are encrypted on this device; prompts and replies are forwarded without being logged or saved.", 12));
        foreach (var account in settings.Accounts.ToList())
        {
            panel.Children.Add(ProviderEditor(account));
            panel.Children.Add(Views.Label($"Harness base URL: http://127.0.0.1:{ProviderGateway.Port}/providers/{account.Id}/v1", 12));
        }
        panel.Children.Add(ProviderEditor(null)); return panel;
    }
    internal static DataTransfer GatewayClipboardData(string token)
    {
        var item = DataTransferItem.CreateText(token);
        item.Set(DataFormat.CreateStringApplicationFormat("atoll.concealed"), "1");
        if (OperatingSystem.IsWindows()) item.Set(DataFormat.CreateBytesPlatformFormat("ExcludeClipboardContentFromMonitorProcessing"), new byte[] { 1, 0, 0, 0 });
        if (OperatingSystem.IsMacOS()) item.Set(DataFormat.CreateStringPlatformFormat("org.nspasteboard.ConcealedType"), "1");
        var data = new DataTransfer(); data.Add(item); return data;
    }
    private Control ProviderEditor(ProviderAccount? existing)
    {
        TextBox Input(string title, string value = "", bool secret = false)
        {
            var text = new TextBox { Text = value, PasswordChar = secret ? '●' : '\0', HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(text, title); return text;
        }
        var name = Input("Provider name", existing?.Name ?? "");
        var url = Input("API base URL", existing?.BaseUrl ?? "https://api.openai.com/v1/");
        var key = Input("Provider API key", secret: true);
        key.Watermark = existing is null ? "Paste provider key" : "Leave blank to keep saved key";
        var admin = Input("OpenAI admin metrics key", secret: true);
        admin.Watermark = "Optional; leave blank to keep saved key";
        var message = Views.Label("", 12);
        var choices = new ComboBox { ItemsSource = new[] { "OpenAI", "DeepSeek", "Anthropic", "Custom compatible provider" }, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose provider" };
        AutomationProperties.SetName(choices, "Provider preset");
        choices.SelectionChanged += (_, _) =>
        {
            name.Text = choices.SelectedItem as string;
            url.Text = choices.SelectedIndex switch { 0 => "https://api.openai.com/v1/", 1 => "https://api.deepseek.com/v1/", 2 => "https://api.anthropic.com/v1/", _ => "" };
        };
        var actions = Views.Row(Views.Button(existing is null ? "Add provider" : "Save provider", () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Length > 100) { message.Text = "Enter a provider name (up to 100 characters)."; return; }
                var account = new ProviderAccount
                {
                    Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim(), BaseUrl = url.Text?.Trim() ?? "",
                    EncryptedKey = string.IsNullOrWhiteSpace(key.Text) ? existing?.EncryptedKey ?? "" : SecretStore.Protect(key.Text.Trim()),
                    EncryptedAdminKey = string.IsNullOrWhiteSpace(admin.Text) ? existing?.EncryptedAdminKey ?? "" : SecretStore.Protect(admin.Text.Trim()),
                };
                _ = ProviderEndpoint.From(account);
                var accounts = _settings.Current.Providers.Accounts;
                if (existing is not null) accounts[accounts.IndexOf(existing)] = account; else accounts.Add(account);
                key.Text = ""; admin.Text = ""; _settings.NotifyChanged(); ShowPage();
            }
            catch (ArgumentException ex) { message.Text = ex.Message; }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException) { message.Text = "Could not encrypt this key. Check access to ATOLL's settings folder."; }
        }));
        if (existing is not null)
        {
            actions.Children.Add(Views.Button("Remove provider", () => { _settings.Current.Providers.Accounts.Remove(existing); _settings.NotifyChanged(); ShowPage(); }));
            actions.Children.Add(Views.Button("Clear admin metrics key", () => { existing.EncryptedAdminKey = ""; _settings.NotifyChanged(); message.Text = "Admin metrics key cleared."; }));
        }
        return Views.Stack(Views.Label(existing?.Name ?? "Add a provider", 18), choices, Views.Label("Name", 12), name, Views.Label("API base URL", 12), url,
            Views.Label("Provider API key", 12), key, Views.Label("OpenAI organization admin key (optional, sent only to api.openai.com)", 12), admin,
            Views.Label("OpenAI organization costs and usage need an admin key. DeepSeek balance uses your normal key. Other providers show local gateway totals.", 12), actions, message);
    }
}
