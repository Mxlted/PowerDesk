using System.Windows;

namespace PowerDesk.Modules.StartupPilot.Views;

public partial class NoteDialog : Window
{
    public string NoteText { get; private set; } = string.Empty;

    public NoteDialog(string itemName, string existingNote)
    {
        InitializeComponent();
        HeaderLabel.Text = $"Note for {itemName}";
        NoteInput.Text = existingNote ?? string.Empty;
        NoteText = existingNote ?? string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        NoteText = NoteInput.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
