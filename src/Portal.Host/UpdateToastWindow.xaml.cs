using System.Windows;

namespace Portal.Host;

public partial class UpdateToastWindow : Window
{
    public UpdateToastWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Services.LocalizationService.ApplyToWindow(this);
    }
}
