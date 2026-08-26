using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PowerDesk.Modules.StartupPilot.Views;

public partial class NoteDialog : Window
{
    public string NoteText { get; private set; } = string.Empty;

    public NoteDialog(string itemName, string existingNote)
    {
        InitializeComponent();
        HeaderLabel.Text = $"Note for {itemName}";
        HeaderLabel.ToolTip = itemName;
        NoteInput.Text = existingNote ?? string.Empty;
        NoteText = existingNote ?? string.Empty;
        NoteInput.CaretIndex = NoteInput.Text.Length;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void NoteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter inserts a newline in a multi-line box, so Ctrl+Enter is the "apply" shortcut.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Save();
        }
    }

    private void Save()
    {
        NoteText = NoteInput.Text.Trim();
        DialogResult = true;
    }
}
