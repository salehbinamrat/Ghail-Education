using System.Windows;
using System.Windows.Controls;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private void ShowAcademicYears()
    {
        var root=Stack(16);
        root.Children.Add(Label("إدارة الأعوام الدراسية",22,true));
        root.Children.Add(Label("السنة النشطة: " + _store.AcademicYear));
        root.Children.Add(Label("إغلاق السنة نهائي؛ لا يمكن إعادة فتح سجلاتها الرسمية.",14));
        var year=Field("",200);
        year.MaxLength=9;
        root.Children.Add(Label("السنة الجديدة أو السنة المحددة (مثال 2027/2028)"));
        root.Children.Add(year);
        var actions=new WrapPanel { Margin=new Thickness(0,12,0,12) };
        actions.Children.Add(Btn("فتح عام دراسي جديد", (_,_) => Safe(() =>
        {
            _store.OpenAcademicYear(_actor,year.Text.Trim());
            ShowAcademicYears();
        })));
        actions.Children.Add(Btn("تفعيل العام المحدد", (_,_) => Safe(() =>
        {
            if(MessageBox.Show("هل تريد تفعيل العام "+year.Text+"؟",
                "تأكيد تغيير العام",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
                return;
            _store.SetActiveAcademicYear(_actor,year.Text.Trim());
            YearText.Text="العام الدراسي: "+_store.AcademicYear;
            ShowAcademicYears();
        })));
        actions.Children.Add(Btn("إغلاق العام نهائيا", (_,_) => Safe(() =>
        {
            if(MessageBox.Show("سيتم إغلاق العام "+year.Text+
                " ومنع تعديل درجاته وحضوره وكشوفه وإحصاءاته. هل أنت متأكد؟",
                "تأكيد إغلاق نهائي",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
                return;
            _store.CloseAcademicYear(_actor,year.Text.Trim());
            ShowAcademicYears();
        }),tone:"danger"));
        root.Children.Add(actions);
        var years=new DataGrid
        {
            ItemsSource=_store.GetAcademicYears(_actor),
            IsReadOnly=true,AutoGenerateColumns=true,
            MinHeight=180,MaxHeight=380
        };
        years.SelectionChanged+=(_,_) =>
        {
            if(years.SelectedItem is SchoolObservatory.Core.AcademicYearRecord selected)
                year.Text=selected.Year;
        };
        root.Children.Add(Label("سجل الأعوام الدراسية — اختر عاما من الجدول",17,true));
        root.Children.Add(years);
        Display("إدارة الأعوام الدراسية",root);
    }
}
