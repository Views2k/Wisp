using System.Windows.Controls;

namespace Wisp.App;

public partial class SupportReminderPopup : UserControl
{
    private readonly string _defaultTitle, _defaultMessage;
    public SupportReminderPopup()
    {
        InitializeComponent();
        _defaultTitle = NoteTitle.Text;
        _defaultMessage = Message.Text;
    }

    internal void ApplyNote(string? title, string? message)
    {
        NoteTitle.Text = title ?? _defaultTitle;
        Message.Text = message ?? _defaultMessage;
    }
}
