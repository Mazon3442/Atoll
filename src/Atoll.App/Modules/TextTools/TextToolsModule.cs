using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Atoll.Modules.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;

namespace Atoll.Modules.TextTools;

/// <summary>
/// The Text tab: fix a wrong keyboard layout, change case, transliterate, format JSON, encode Base64 and URLs, make a
/// QR code, or hand the text to the AI for a rewrite. Everything except the AI buttons works offline on this PC.
/// </summary>
public sealed partial class TextToolsViewModel(Action<string>? askAi = null) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInput))] private string _input = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOutput))] private string _output = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasQr))] private BitmapSource? _qr;

    public bool HasInput => Input.Length > 0;
    public bool HasOutput => Output.Length > 0;
    public bool HasQr => Qr is not null;

    /// <summary>False when there is no way to ask an AI (the Agents tab is off), so those buttons hide.</summary>
    public bool HasAi => askAi is not null;

    private void Show(string text, string message = "")
    {
        Output = text;
        Message = message;
        Qr = null;
    }

    private void Run(Func<string, string> transform, string name)
    {
        if (!HasInput) { Message = "Type or paste some text first."; return; }
        Show(transform(Input), name);
    }

    private void Run(Func<string, TextResult> transform, string name)
    {
        if (!HasInput) { Message = "Type or paste some text first."; return; }
        var result = transform(Input);
        if (result.Ok) Show(result.Text, name);
        else { Output = ""; Qr = null; Message = result.Error ?? ""; }
    }

    [RelayCommand] private void FixLayout() => Run(TextTransforms.FixLayout, "Layout fixed.");
    [RelayCommand] private void Upper() => Run(TextTransforms.Upper, "UPPER CASE.");
    [RelayCommand] private void Lower() => Run(TextTransforms.Lower, "lower case.");
    [RelayCommand] private void TitleCase() => Run(TextTransforms.Title, "Title Case.");
    [RelayCommand] private void SentenceCase() => Run(TextTransforms.Sentence, "Sentence case.");
    [RelayCommand] private void Snake() => Run(TextTransforms.Snake, "snake_case.");
    [RelayCommand] private void Camel() => Run(TextTransforms.Camel, "camelCase.");
    [RelayCommand] private void Kebab() => Run(TextTransforms.Kebab, "kebab-case.");
    [RelayCommand] private void Translit() => Run(TextTransforms.Transliterate, "Transliterated.");
    [RelayCommand] private void JsonPretty() => Run(TextTransforms.JsonPretty, "JSON formatted.");
    [RelayCommand] private void JsonMinify() => Run(TextTransforms.JsonMinify, "JSON minified.");
    [RelayCommand] private void ToBase64() => Run(TextTransforms.Base64Encode, "Base64 encoded.");
    [RelayCommand] private void FromBase64() => Run(TextTransforms.Base64Decode, "Base64 decoded.");
    [RelayCommand] private void UrlEncode() => Run(TextTransforms.UrlEncode, "URL encoded.");
    [RelayCommand] private void UrlDecode() => Run(TextTransforms.UrlDecode, "URL decoded.");
    [RelayCommand] private void CleanLines() => Run(TextTransforms.CleanLines, "Lines cleaned.");

    [RelayCommand]
    private void MakeQr()
    {
        if (!HasInput) { Message = "Type or paste some text first."; return; }
        try
        {
            Qr = QrImage.Make(Input.Trim());
            Output = "";
            Message = "QR code ready. Scan it, or copy it.";
        }
        catch (Exception ex) when (ex is QRCoder.Exceptions.DataTooLongException or ArgumentException)
        {
            Qr = null;
            Message = "That is too long for a QR code.";
        }
    }

    [RelayCommand]
    private void PasteInput()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText()) Input = System.Windows.Clipboard.GetText();
            else Message = "The clipboard has no text.";
        }
        catch (System.Runtime.InteropServices.COMException) { Message = "The clipboard is busy. Try again."; }
    }

    [RelayCommand]
    private void CopyOutput()
    {
        try
        {
            if (Qr is not null) System.Windows.Clipboard.SetImage(Qr);
            else if (HasOutput) System.Windows.Clipboard.SetText(Output);
            else return;
            Message = "Copied.";
        }
        catch (System.Runtime.InteropServices.COMException) { Message = "The clipboard is busy. Try again."; }
    }

    /// <summary>Moves the result back into the input, so transforms can be chained.</summary>
    [RelayCommand]
    private void UseOutput()
    {
        if (!HasOutput) return;
        Input = Output;
        Output = "";
        Message = "";
    }

    [RelayCommand]
    private void ClearAll()
    {
        Input = "";
        Output = "";
        Qr = null;
        Message = "";
    }

    [RelayCommand] private void Translate() => Ask("Translate this text into English. If it is already English, translate it into Russian. Reply with only the translation.");
    [RelayCommand] private void FixText() => Ask("Fix the spelling, grammar and punctuation of this text. Keep the meaning and the language. Reply with only the corrected text.");
    [RelayCommand] private void Shorten() => Ask("Shorten this text and keep the meaning. Reply with only the shorter text.");
    [RelayCommand] private void Explain() => Ask("Explain this text in plain words.");
    [RelayCommand] private void Reply() => Ask("Write a short, friendly reply to this message. Reply with only the reply.");

    private void Ask(string instruction)
    {
        if (!HasInput) { Message = "Type or paste some text first."; return; }
        askAi?.Invoke(instruction + "\n\n" + Input);
    }
}

/// <summary>Draws a QR code as a bitmap: black modules on a white quiet zone, so any scanner reads it.</summary>
internal static class QrImage
{
    public static BitmapSource Make(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// <summary>Text tab: transforms for the text you paste. Opened from the ⋯ menu.</summary>
public sealed class TextToolsModule : IIslandModule
{
    private TextToolsViewModel? _vm;

    public object? Panel => _vm;

    public void Start(ModuleContext context) => _vm = new TextToolsViewModel(Atoll.Navigation.AskAi);

    public void Dispose() { }
}
