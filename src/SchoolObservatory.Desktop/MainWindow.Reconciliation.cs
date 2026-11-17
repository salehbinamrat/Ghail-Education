using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private void ShowReconciliation()
    {
        var root = Stack();
        root.Children.Add(Label("مطابقة السجلات التشغيلية مع الإحصاء المعتمد", 21, true));
        root.Children.Add(Label(
            "مصدر الطلاب: القيد الفعلي في سنة محددة. مصدر الشعب: الصفوف والشعب المسجلة، لا الغرف. " +
            "كادر الملاك مستقل عن العاملين بحسب التكليف. الاستمارات المعتمدة لا تتغير عند تعديل السجلات.", 14));

        var years = _store.GetAcademicYears(_actor);
        var selector = new ComboBox
        {
            Width = 190, ItemsSource = years, DisplayMemberPath = "Year",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var showAll = new CheckBox
        {
            Content = "عرض جميع المؤشرات بما فيها المتطابقة والمرجعية",
            FontWeight = FontWeights.Bold, Margin = new Thickness(14, 14, 0, 7)
        };
        var controls = new WrapPanel();
        controls.Children.Add(LineOfFields(("السنة الدراسية", selector)));
        controls.Children.Add(showAll);
        root.Children.Add(Box(controls));
        root.Children.Add(Label(
            "الفرق = العدد التشغيلي - العدد المعتمد. الخانة الرسمية الفارغة تعني عدم وجود قيمة معتمدة قابلة للمقارنة، لا الصفر. " +
            "قد تنتج بعض الفروق عن اختلاف تاريخ لقطة الإحصاء عن السجلات الحالية.", 13));

        var summary = Label("", 15, true);
        var tableArea = Stack();
        var details = Stack();
        root.Children.Add(summary);
        root.Children.Add(Box(tableArea));
        root.Children.Add(Box(details));

        void Render()
        {
            if (selector.SelectedItem is not AcademicYearRecord selectedYear) return;
            var all = _store.GetCensusReconciliation(_actor, selectedYear.Year);
            var current = all.Where(x => x.IsDiscrepant ||
                (x.MetricKey == "students.total" && x.SubmissionId is null));
            var rows = (showAll.IsChecked == true ? all : current).ToList();
            summary.Text = $"العام {selectedYear.Year} | المدارس: {all.Select(x => x.SchoolId).Distinct().Count()} | " +
                $"الفروق المكتشفة: {all.Count(x => x.IsDiscrepant)} | " +
                $"مدارس دون إحصاء معتمد: {all.Where(x => x.MetricKey == "students.total" && x.SubmissionId is null).Count()}";
            tableArea.Children.Clear();
            details.Children.Clear();
            if (rows.Count == 0)
            {
                tableArea.Children.Add(Label("لا توجد فروق رقمية في البيانات القابلة للمقارنة. يمكنك إظهار بقية المؤشرات لمراجعة مصادرها.", 15, true));
                return;
            }
            var table = GridFor(rows,
                ("المدرسة", "SchoolName", false),
                ("الفئة", "Label", false),
                ("التشغيلي", "OperationalValue", false),
                ("المعتمد", "ApprovedValue", false),
                ("الفرق", "Difference", false),
                ("الحالة", "ReviewStatus", false));
            table.MaxHeight = 460;
            tableArea.Children.Add(table);
            var explanation = Label("حدد مؤشرا لعرض مصدره وطريقة احتسابه.", 14);
            details.Children.Add(explanation);
            table.SelectionChanged += (_, _) =>
            {
                if (table.SelectedItem is not CensusReconciliationRow chosen) return;
                explanation.Text =
                    $"المدرسة: {chosen.SchoolName}  |  العام: {chosen.AcademicYear}\n" +
                    $"المصدر: {chosen.Source}\n" +
                    $"المعادلة: {chosen.Formula}\n" +
                    $"التفسير: {chosen.Observation}";
            };

            var mayReview = AccessPolicy.Has(_actor, PermissionFlags.ReviewCensus) &&
                _actor.Role is Roles.Admin or Roles.Statistician or Roles.Reviewer;
            if (!mayReview) return;
            var reason = Field(width: 480);
            reason.MaxLength = 1000;
            details.Children.Add(LineOfFields(("سبب توثيق المراجعة", reason)));
            details.Children.Add(Btn("توثيق مراجعة المؤشر المحدد", (_, _) => Safe(() =>
            {
                if (table.SelectedItem is not CensusReconciliationRow chosen)
                    throw new InvalidOperationException("حدد مؤشرا من جدول المطابقة.");
                if (string.IsNullOrWhiteSpace(reason.Text))
                    throw new ArgumentException("اكتب سبب المراجعة أو توضيح الفرق أولا.");
                if (MessageBox.Show(
                    $"توثيق مراجعة {chosen.Label} في {chosen.SchoolName} لسنة {chosen.AcademicYear}؟\n" +
                    "لن يغير هذا الإجراء أي قيمة في الإحصاء المعتمد.",
                    "تأكيد تسجيل المراجعة", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _store.ReviewReconciliation(_actor, chosen.SchoolId, chosen.AcademicYear,
                    chosen.MetricKey, reason.Text);
                Info("تم حفظ المراجعة في سجل العمليات مع القيم والمصدر وسبب المراجعة، دون تعديل الاستمارة المعتمدة.");
                reason.Text = "";
                Render();
            }), tone: "purple"));
        }

        selector.SelectionChanged += (_, _) => Safe(Render);
        showAll.Checked += (_, _) => Safe(Render);
        showAll.Unchecked += (_, _) => Safe(Render);
        selector.SelectedItem = years.FirstOrDefault(x => x.Year == _store.AcademicYear) ?? years.FirstOrDefault();
        Display("مطابقة السجلات والإحصاء", root);
    }
}
