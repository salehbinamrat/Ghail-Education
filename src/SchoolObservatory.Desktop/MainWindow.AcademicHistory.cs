using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private void ShowAcademicYears()
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException();
        var years = _store.GetAcademicYears(_actor);
        var root = Stack();
        root.Children.Add(Label("إدارة الأعوام الدراسية والأرشيف", 21, true));
        root.Children.Add(Label(
            "لكل عام سجل مستقل. فتح عام جديد لا يغير العام الحالي. تفعيل العام يغير السنة التشغيلية، " +
            "وإغلاق العام السابق يمنع تعديل درجاته وتقييماته وحضوره وإحصاءاته. يحتفظ البرنامج بجميع السجلات.",
            14));
        var rows = GridFor(years, ("العام الدراسي", "Year", false),
            ("نشط", "IsActive", false), ("مغلق", "IsClosed", false),
            ("تاريخ الفتح", "OpenedUtc", false), ("تاريخ الإغلاق", "ClosedUtc", false));
        rows.MaxHeight = 290;
        root.Children.Add(Box(rows));

        if (_actor.IsOwner)
        {
            var editor = Stack();
            editor.Children.Add(Label("إدارة الدورة الدراسية — للمسؤول المؤسس فقط", 18, true));
            var nextYear = Field("", 175);
            var open = Btn("فتح عام دراسي جديد", (_, _) => Safe(() =>
            {
                _store.OpenAcademicYear(_actor, nextYear.Text.Trim());
                Info("تم فتح العام الجديد دون تغيير العام النشط.");
                ShowAcademicYears();
            }), tone: "success");
            editor.Children.Add(LineOfFields(("العام الجديد مثل 2027/2028", nextYear)));
            editor.Children.Add(open);
            var buttons = new WrapPanel();
            buttons.Children.Add(Btn("تفعيل العام المحدد", (_, _) => Safe(() =>
            {
                if (rows.SelectedItem is not AcademicYearRecord selected)
                    throw new InvalidOperationException("حدد عاما من الجدول.");
                if (MessageBox.Show($"تفعيل العام {selected.Year}؟ ستصبح جميع إدخالات المدرسة الجديدة فيه.",
                    "تأكيد التفعيل", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _store.ActivateAcademicYear(_actor, selected.Year);
                YearText.Text = $"العام الدراسي: {_store.AcademicYear}";
                ShowAcademicYears();
            }), tone: "primary"));
            buttons.Children.Add(Btn("إغلاق العام المحدد نهائيا", (_, _) => Safe(() =>
            {
                if (rows.SelectedItem is not AcademicYearRecord selected)
                    throw new InvalidOperationException("حدد عاما من الجدول.");
                if (MessageBox.Show($"إغلاق العام {selected.Year} يمنع تعديل سجلاته الرسمية. لا يمكن إغلاق العام النشط. هل تؤكد؟",
                    "قفل سجلات العام الدراسي", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _store.CloseAcademicYear(_actor, selected.Year);
                ShowAcademicYears();
            }), tone: "danger"));
            editor.Children.Add(buttons);
            root.Children.Add(Box(editor));
        }
        if (AccessPolicy.Has(_actor, PermissionFlags.ViewStudents))
            root.Children.Add(Btn("عرض كشوف الأعوام السابقة وترفيع الطلاب", (_, _) =>
                ShowHistoricalRosters(), tone: "secondary"));
        root.Children.Add(Label("تاريخ السنوات المغلقة متاح للعرض والمراجعة، دون تعديل قيودها الرسمية.", 13));
        Display("الأعوام الدراسية والأرشيف", root);
    }

    private void AppendStudentTransitions(StackPanel root, School school, string studentId)
    {
        var history = _store.GetStudentEnrollmentHistory(_actor, studentId);
        var section = Stack();
        section.Children.Add(Label("السجل الدراسي التاريخي وحركة الطالب", 18, true));
        section.Children.Add(Label(
            "هوية الطالب واحدة عبر السنوات والمدارس. الترفيع والإعادة والنقل تنشئ قيدا تاريخيا جديدا " +
            "ولا تحذف كشوف الأعوام السابقة.", 13));
        var grid = GridFor(history, ("العام", "AcademicYear", false),
            ("المدرسة", "SchoolId", false), ("الصف", "Grade", false),
            ("الشعبة", "Section", false), ("الحالة", "Status", false),
            ("بداية القيد", "StartedUtc", false), ("نهاية القيد", "EndedUtc", false));
        grid.MaxHeight = 250;
        section.Children.Add(grid);
        if (AccessPolicy.CanManageStudents(_actor, school.Id))
        {
            var schools = _store.GetSchools(_actor).Where(x => x.IsActive).ToArray();
            var workplace = new ComboBox { ItemsSource = schools, DisplayMemberPath = "Name",
                Width = 280, HorizontalAlignment = HorizontalAlignment.Left,
                SelectedItem = schools.FirstOrDefault(x => x.Id == school.Id) };
            var grade = Field((history.LastOrDefault()?.Grade ?? 1).ToString(CultureInfo.InvariantCulture), 95);
            grade.MaxLength = 2;
            var sub = Field("أ", 95);
            var action = Choice(new[] { "ترفيع", "إعادة", "نقل شعبة", "نقل مدرسة" }, 180);
            section.Children.Add(LineOfFields(("الإجراء", action), ("المدرسة الوجهة", workplace),
                ("الصف الوجهة", grade), ("الشعبة", sub)));
            var row = new WrapPanel();
            row.Children.Add(Btn("تنفيذ حركة الطالب", (_, _) => Safe(() =>
            {
                if (workplace.SelectedItem is not School target ||
                    !int.TryParse(grade.Text, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var nextGrade))
                    throw new ArgumentException("اختر المدرسة والصف الصحيحين.");
                var chosen = action.SelectedItem?.ToString() ?? "";
                if (MessageBox.Show($"تنفيذ {chosen} للطالب إلى {target.Name} / الصف {nextGrade} / الشعبة {sub.Text}؟",
                    "تأكيد الحركة المدرسية", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                switch (chosen)
                {
                    case "ترفيع":
                        _store.PromoteStudent(_actor, studentId, target.Id, nextGrade, sub.Text, false);
                        break;
                    case "إعادة":
                        _store.PromoteStudent(_actor, studentId, target.Id, nextGrade, sub.Text, true);
                        break;
                    case "نقل شعبة":
                        _store.TransferStudentSection(_actor, studentId, target.Id, nextGrade, sub.Text);
                        break;
                    case "نقل مدرسة":
                        _store.TransferStudentSchool(_actor, studentId, target.Id, nextGrade, sub.Text);
                        break;
                    default: throw new ArgumentException("حدد الإجراء.");
                }
                Info("تم حفظ الحركة في سجل الطالب التاريخي.");
                ShowStudentsCore(target.Id, nextGrade, sub.Text);
            }), tone: "success"));
            row.Children.Add(Btn("تخرج الطالب", (_, _) => Safe(() =>
            {
                if (MessageBox.Show("تأكيد إنهاء قيد الطالب للتخرج مع الاحتفاظ بجميع النتائج السابقة؟",
                    "تخرج", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _store.GraduateStudent(_actor, studentId);
                ShowStudentsCore(school.Id);
            }), tone: "secondary"));
            row.Children.Add(Btn("انسحاب الطالب وأرشفة قيده", (_, _) => Safe(() =>
            {
                if (MessageBox.Show("هل تؤكد حفظ انسحاب الطالب دون حذف أي من سجلاته؟",
                    "أرشفة قيد الطالب", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _store.WithdrawStudent(_actor, studentId);
                ShowStudentsCore(school.Id);
            }), tone: "danger"));
            section.Children.Add(row);
        }
        root.Children.Add(Box(section));
    }

    private void AppendEmployeeAssignments(StackPanel root, School school, string staffId)
    {
        var history = _store.GetEmployeeAssignments(_actor, staffId);
        var career = _store.GetEmployeeCareer(_actor, staffId);
        var section = Stack();
        section.Children.Add(Label("الهوية الوظيفية وسجل الانتداب والنقل", 18, true));
        section.Children.Add(Label("رقم الموظف: " + career.EmployeeCode +
            " — مدرسة الراتب الأصلية ثابتة: " + school.Name, 14));
        var grid = GridFor(history, ("جهة الراتب", "PayrollSchoolId", false),
            ("جهة المباشرة", "WorkplaceSchoolId", false),
            ("نوع العمل", "AssignmentType", false),
            ("البداية", "StartDate", false), ("النهاية", "EndDate", false));
        grid.MaxHeight = 240;
        section.Children.Add(grid);
        if (AccessPolicy.CanEditStaff(_actor, school.Id))
        {
            var schools = _store.GetSchools(_actor).Where(s => s.IsActive).ToArray();
            var target = new ComboBox { ItemsSource = schools, DisplayMemberPath = "Name",
                Width = 270, HorizontalAlignment = HorizontalAlignment.Left,
                SelectedItem = schools.FirstOrDefault(s => s.Id == school.Id) };
            var type = Choice(new[] { "أصلي", "انتداب", "نقل", "تكليف", "إعارة", "إجازة" }, 160);
            var from = Field(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 170);
            var until = Field("", 170);
            section.Children.Add(LineOfFields(("جهة العمل الفعلي", target),
                ("نوع الانتقال", type), ("بداية YYYY-MM-DD", from),
                ("نهاية اختيارية", until)));
            section.Children.Add(Btn("حفظ تكليف جديد مع الاحتفاظ بالتاريخ", (_, _) => Safe(() =>
            {
                if (target.SelectedItem is not School destination ||
                    !DateOnly.TryParseExact(from.Text, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new ArgumentException("تاريخ البداية أو الجهة غير صحيح.");
                DateOnly? end = null;
                if (!string.IsNullOrWhiteSpace(until.Text))
                {
                    if (!DateOnly.TryParseExact(until.Text, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        throw new ArgumentException("تاريخ النهاية غير صحيح.");
                    end = parsed;
                }
                _store.AssignEmployee(_actor, staffId, destination.Id,
                    type.SelectedItem?.ToString() ?? "", date, end);
                ShowStaffProfile(school.Id, staffId);
            }), tone: "success"));
        }
        root.Children.Add(Box(section));
    }

    private void AppendSchoolInfrastructure(StackPanel root, School school)
    {
        var data = _store.GetSchoolInfrastructure(_actor, school.Id);
        var facilities = _store.GetSchoolFacilities(_actor, school.Id);
        var section = Stack();
        section.Children.Add(Label("المبنى والمرافق والفترات الدراسية", 18, true));
        var area = Field(data.GeographicArea, 180);
        var address = Field(data.PostalAddress, 330);
        var type = Field(data.BuildingType, 170);
        var ownership = Field(data.BuildingOwnership, 160);
        var rooms = Field(data.PhysicalRooms.ToString(CultureInfo.InvariantCulture), 110);
        var shifts = Field(data.OperatingShifts, 175);
        section.Children.Add(LineOfFields(("المنطقة الجغرافية", area),
            ("العنوان", address), ("نوع المبنى", type),
            ("ملكية المبنى", ownership), ("عدد الغرف", rooms), ("فترات الدوام", shifts)));
        var water = new CheckBox { Content = "المياه متوفرة", IsChecked = data.HasWater, Margin = new Thickness(8) };
        var power = new CheckBox { Content = "الكهرباء متوفرة", IsChecked = data.HasElectricity, Margin = new Thickness(8) };
        var net = new CheckBox { Content = "الاتصال متوفر", IsChecked = data.HasInternet, Margin = new Thickness(8) };
        section.Children.Add(new WrapPanel { Children = { water, power, net } });
        var notes = ProfileNotes(data.Notes);
        section.Children.Add(FormField("ملاحظات البنية المدرسية", notes));
        var editable = AccessPolicy.CanEditSchoolProfile(_actor, school.Id);
        foreach (var input in new[] { area,address,type,ownership,rooms,shifts,notes })
            input.IsReadOnly = !editable;
        water.IsEnabled = power.IsEnabled = net.IsEnabled = editable;
        section.Children.Add(Btn("حفظ بيانات المبنى", (_, _) => Safe(() =>
        {
            if (!int.TryParse(rooms.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var roomCount))
                throw new ArgumentException("عدد الغرف غير صحيح.");
            _store.SaveSchoolInfrastructure(_actor, new SchoolInfrastructureRecord(
                school.Id,area.Text,address.Text,type.Text,ownership.Text,roomCount,
                shifts.Text,water.IsChecked==true,power.IsChecked==true,
                net.IsChecked==true,notes.Text));
            ShowSchoolProfile(school.Id);
        }), editable, "success"));
        section.Children.Add(Label("المرافق المسجلة", 16, true));
        section.Children.Add(GridFor(facilities, ("نوع المرفق", "Name", false),
            ("العدد", "Quantity", false), ("الحالة", "Condition", false)));
        var facility = Field("", 180);
        var quantity = Field("1", 80);
        var condition = Field("جيد", 160);
        section.Children.Add(LineOfFields(("المرفق", facility),
            ("عدد الوحدات", quantity), ("حالته", condition)));
        section.Children.Add(Btn("إضافة / تحديث المرفق", (_, _) => Safe(() =>
        {
            if (!int.TryParse(quantity.Text, NumberStyles.None,
                CultureInfo.InvariantCulture, out var count))
                throw new ArgumentException("العدد غير صحيح.");
            _store.SaveSchoolFacility(_actor,
                new SchoolFacility(school.Id, facility.Text, count, condition.Text));
            ShowSchoolProfile(school.Id);
        }), editable, "secondary"));
        root.Children.Add(Box(section));
    }
}
