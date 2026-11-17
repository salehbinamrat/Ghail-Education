using System.Windows;
using System.Windows.Controls;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private void ShowStudentTransitions()
    {
        var root=Stack(16);
        root.Children.Add(Label("ترحيل الطلاب ونقلهم بين الشعب والمدارس",22,true));
        root.Children.Add(Label("العام النشط المستهدف: "+_store.AcademicYear));
        root.Children.Add(Label("الترقية والإعادة تتطلبان عاما جديدا. النقل بين الشعب والمدارس يكون خلال العام النشط.",14));
        var schools=_store.GetSchools(_actor).Where(s=>s.IsActive).ToArray();
        var years=_store.GetAcademicYears(_actor).ToArray();
        var sourceSchool=new ComboBox { Width=340,DisplayMemberPath="Name",ItemsSource=schools };
        var destinationSchool=new ComboBox { Width=340,DisplayMemberPath="Name",ItemsSource=schools };
        var sourceYear=new ComboBox { Width=220,DisplayMemberPath="Year",ItemsSource=years };
        var student=new ComboBox { Width=420,DisplayMemberPath="FullName" };
        var targetSection=new ComboBox { Width=340,DisplayMemberPath="DisplayName" };
        var action=new ComboBox { Width=220,ItemsSource=new[]
        {
            "promote","repeat","section_transfer","school_transfer","graduate","withdraw"
        }};
        root.Children.Add(Label("مدرسة الطالب الحالية"));
        root.Children.Add(sourceSchool);
        root.Children.Add(Label("السنة التي ينتسب إليها الطالب"));
        root.Children.Add(sourceYear);
        root.Children.Add(Label("الطالب"));
        root.Children.Add(student);
        root.Children.Add(Label("العملية"));
        root.Children.Add(action);
        root.Children.Add(Label("المدرسة المستهدفة"));
        root.Children.Add(destinationSchool);
        root.Children.Add(Label("الصف والشعبة المستهدفان"));
        root.Children.Add(targetSection);
        void RefreshStudents()
        {
            student.ItemsSource=null;
            if(sourceSchool.SelectedItem is not School school ||
                sourceYear.SelectedItem is not AcademicYearRecord year) return;
            student.ItemsSource=_store.GetStudentsInAcademicYear(_actor,school.Id,year.Year);
        }
        void RefreshSections()
        {
            targetSection.ItemsSource=null;
            if(destinationSchool.SelectedItem is not School school) return;
            targetSection.ItemsSource=_store.GetSchoolGrades(_actor,school.Id);
        }
        sourceSchool.SelectionChanged+=(_,_)=>Safe(RefreshStudents);
        sourceYear.SelectionChanged+=(_,_)=>Safe(RefreshStudents);
        destinationSchool.SelectionChanged+=(_,_)=>Safe(RefreshSections);
        if(schools.Length>0) { sourceSchool.SelectedIndex=0; destinationSchool.SelectedIndex=0; }
        if(years.Length>0) sourceYear.SelectedIndex=0;
        action.SelectedIndex=0;
        root.Children.Add(Btn("تنفيذ الترحيل بعد التأكيد",(_,_)=>Safe(()=>
        {
            if(sourceSchool.SelectedItem is not School source ||
                destinationSchool.SelectedItem is not School destination ||
                student.SelectedItem is not SchoolStudent selected ||
                action.SelectedItem is not string operation)
                throw new InvalidOperationException("اختر المدرسة والطالب ونوع العملية.");
            var completing=operation is "graduate" or "withdraw";
            if(!completing && targetSection.SelectedItem is not SchoolGrade)
                throw new InvalidOperationException("اختر الصف والشعبة المستهدفين.");
            if(MessageBox.Show("هل تريد تنفيذ العملية "+operation+" للطالب "+selected.FullName+
                "؟ سيتم حفظ أثر تاريخي للعملية.",
                "تأكيد الترحيل",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
                return;
            if(completing)
                _store.CompleteStudentEnrollment(_actor,source.Id,selected.Id,operation);
            else
            {
                var grade=(SchoolGrade)targetSection.SelectedItem;
                _store.TransitionStudent(_actor,source.Id,selected.Id,
                    destination.Id,grade.Grade,grade.Section,operation);
            }
            ShowStudentTransitions();
        }),tone:"warning"));
        Display("ترحيل ونقل الطلاب",root);
    }
}
