namespace Blocker.App;

public partial class SoundscapePillControl : System.Windows.Controls.UserControl
{
    public SoundscapePillControl()
    {
        InitializeComponent();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (DataContext is MeditationViewModel vm && vm.IsSoundscapeMenuOpen)
            {
                vm.IsSoundscapeMenuOpen = false;
                e.Handled = true;
            }
        }
    }
}
