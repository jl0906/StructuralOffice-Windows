using System.Windows;

namespace StructuralOffice.Desktop;

public partial class DunningScheduleDialog : Window
{
    public DateTime DueDate => DueDatePicker.SelectedDate ?? DateTime.Today.AddDays(14);

    public DunningScheduleDialog()
    {
        InitializeComponent();
        DueDatePicker.SelectedDate = DateTime.Today.AddDays(14);
        DueDatePicker.DisplayDateStart = DateTime.Today;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    private void Schedule_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DueDatePicker.SelectedDate is null || DueDatePicker.SelectedDate < DateTime.Today)
        {
            MessageBox.Show("Bitte wähle ein heutiges oder zukünftiges Datum.",
                "StructuralOffice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
