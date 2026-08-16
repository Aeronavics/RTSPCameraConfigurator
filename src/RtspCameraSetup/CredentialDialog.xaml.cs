using System.Windows;

namespace RtspCameraSetup;

public partial class CredentialDialog : Window
{
    public string Username => UserBox.Text.Trim();
    public string Password => PassBox.Password;
    public bool ShouldSave => SaveCheck.IsChecked == true;

    public CredentialDialog(string address, string suggestedUser, string? failureReason = null)
    {
        InitializeComponent();

        Title = $"Sign in to {address}";
        PromptText.Text = failureReason is null
            ? $"Enter credentials for {address}."
            : $"Could not sign in to {address}.{Environment.NewLine}{failureReason}";

        UserBox.Text = suggestedUser;

        // Land the caret where the user almost certainly needs to type.
        Loaded += (_, _) =>
        {
            if (UserBox.Text.Length == 0) UserBox.Focus();
            else PassBox.Focus();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Username.Length == 0)
        {
            MessageBox.Show(this, "Enter a username.", "Sign in",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            UserBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
