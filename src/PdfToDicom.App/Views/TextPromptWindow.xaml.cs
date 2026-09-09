using System.Windows;

namespace PdfToDicom.App.Views;

/// <summary>
/// A small modal prompt for a single line of text, used when saving a template.
/// </summary>
public partial class TextPromptWindow : Window
{
    /// <summary>Creates the prompt.</summary>
    /// <param name="title">Window title.</param>
    /// <param name="prompt">Label shown above the input box.</param>
    /// <param name="initialValue">Value the box starts with.</param>
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();

        Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = initialValue;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>The text the operator entered, valid when the dialog returns true.</summary>
    public string EnteredText { get; private set; } = string.Empty;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        EnteredText = InputBox.Text;
        DialogResult = true;
    }
}
