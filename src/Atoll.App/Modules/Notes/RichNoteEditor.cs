using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Atoll.Settings;

namespace Atoll.Modules.Notes;

/// <summary>Formatting commands the toolbar binds to; <see cref="RichNoteEditor"/> handles them.</summary>
public static class NoteCommands
{
    public static readonly RoutedUICommand Strike = new("Strikethrough", nameof(Strike), typeof(NoteCommands));
    public static readonly RoutedUICommand Mark = new("Highlight", nameof(Mark), typeof(NoteCommands));
    public static readonly RoutedUICommand Task = new("Task", nameof(Task), typeof(NoteCommands));
}

/// <summary>
/// A <see cref="RichTextBox"/> that edits one <see cref="NoteItem"/>: it loads the saved formatting (or the plain
/// text of an older note), and writes plain text and XAML back after a short pause in typing.
/// Tasks are lines that start with a ballot box; clicking the box ticks it.
/// </summary>
public sealed class RichNoteEditor : RichTextBox
{
    public const string Open = "☐", Done = "☑";

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(NoteItem), typeof(RichNoteEditor), new PropertyMetadata(null, OnNoteChanged));

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(NotesViewModel), typeof(RichNoteEditor));

    private static readonly Brush MarkBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xD6, 0x0A)));
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private NoteItem? _loaded;
    private bool _loading;

    static RichNoteEditor()
    {
        CommandManager.RegisterClassCommandBinding(typeof(RichNoteEditor), new CommandBinding(NoteCommands.Strike, (s, _) => ((RichNoteEditor)s).ToggleStrike()));
        CommandManager.RegisterClassCommandBinding(typeof(RichNoteEditor), new CommandBinding(NoteCommands.Mark, (s, _) => ((RichNoteEditor)s).ToggleMark()));
        CommandManager.RegisterClassCommandBinding(typeof(RichNoteEditor), new CommandBinding(NoteCommands.Task, (s, _) => ((RichNoteEditor)s).ToggleTask()));
    }

    public RichNoteEditor()
    {
        AcceptsTab = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph)) { Setters = { new Setter(Block.MarginProperty, new Thickness(0, 0, 0, 3)) } });
        _debounce.Tick += (_, _) => Flush();
        TextChanged += (_, _) => { if (!_loading) { _debounce.Stop(); _debounce.Start(); } };
        Unloaded += (_, _) => Flush();
        PreviewMouseLeftButtonUp += OnClick;
    }

    public NoteItem? Note { get => (NoteItem?)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    public NotesViewModel? Model { get => (NotesViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    private static void OnNoteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichNoteEditor)d;
        editor.Flush(); // the note we are leaving keeps what was typed
        editor.Load(e.NewValue as NoteItem);
    }

    private void Load(NoteItem? note)
    {
        _loading = true;
        try
        {
            _loaded = note;
            IsEnabled = note is not null;
            Document = note is null ? new FlowDocument() : ReadDocument(note);
        }
        finally { _loading = false; }
    }

    private static FlowDocument ReadDocument(NoteItem note)
    {
        if (note.Rich.Length > 0)
        {
            try { if (XamlReader.Parse(note.Rich) is FlowDocument saved) return saved; }
            catch (Exception ex) when (ex is XamlParseException or InvalidCastException or IOException) { Log.Info($"A note's formatting could not be read: {ex.Message}"); }
        }
        var document = new FlowDocument();
        foreach (var line in note.Text.Replace("\r\n", "\n").Split('\n'))
            document.Blocks.Add(new Paragraph(new Run(line)));
        return document;
    }

    /// <summary>Writes the text now instead of after the pause. Safe to call when nothing changed.</summary>
    public void Flush()
    {
        _debounce.Stop();
        if (_loaded is null || Model is null) return;
        var plain = new TextRange(Document.ContentStart, Document.ContentEnd).Text.TrimEnd('\r', '\n');
        Model.UpdateNote(_loaded, plain, XamlWriter.Save(Document));
    }

    private void ToggleStrike()
    {
        var current = Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var on = current is TextDecorationCollection decorations && decorations.Contains(TextDecorations.Strikethrough[0]);
        Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, on ? null : TextDecorations.Strikethrough);
        Focus();
    }

    private void ToggleMark()
    {
        var on = Selection.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush brush && brush.Color == ((SolidColorBrush)MarkBrush).Color;
        Selection.ApplyPropertyValue(TextElement.BackgroundProperty, on ? null : MarkBrush);
        Focus();
    }

    /// <summary>Starts a task on this line, or cycles it: none, open, done, none.</summary>
    private void ToggleTask()
    {
        if (CaretPosition.Paragraph is not { } paragraph) return;
        var first = paragraph.Inlines.FirstInline as Run;
        var text = first?.Text ?? "";
        if (text.StartsWith(Open + " ", StringComparison.Ordinal)) first!.Text = Done + text[1..];
        else if (text.StartsWith(Done + " ", StringComparison.Ordinal)) first!.Text = text[2..];
        else if (first is not null) first.Text = Open + " " + text;
        else paragraph.Inlines.Add(new Run(Open + " "));
        Focus();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !Selection.IsEmpty) return;
        var at = GetPositionFromPoint(e.GetPosition(this), snapToText: false);
        if (at?.Paragraph is not { } paragraph || paragraph.Inlines.FirstInline is not Run run) return;
        // Only a click on the box itself (the first character of the line) toggles it.
        var start = run.ContentStart;
        if (!(at.CompareTo(start) >= 0 && at.CompareTo(start.GetPositionAtOffset(2) ?? start) <= 0)) return;
        if (run.Text.StartsWith(Open, StringComparison.Ordinal)) { run.Text = Done + run.Text[1..]; e.Handled = true; }
        else if (run.Text.StartsWith(Done, StringComparison.Ordinal)) { run.Text = Open + run.Text[1..]; e.Handled = true; }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
