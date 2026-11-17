using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private void ShowHistoricalRosters()
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewStudents))
            throw new UnauthorizedAccessException();
        var root = Stack();
        root.Children.Add(Label("كشوف الطلاب التاريخية والترفيع", 21, true));
        root.Children.Add(Label(
            "اختر العام والمدرسة لعرض جميع قيودها السابقة، بما فيها الطلاب المنسحبون والمتخرجون والمنقولون. " +
            "الكشوف المغلقة متاحة للقراءة فقط؛ الترفيع يُضاف إلى العام النشط ولا يعدل النتائج السابقة.", 14));
        var years = _store.GetAcademicYears(_actor).ToArray();
        var schools = _store.GetSchools(_actor).ToArray();
        if (years.Length == 0 || schools.Length == 0)
        {
            root.Children.Add(Label("لا تتوفر أعوام ومدارس للعرض."));
            Display("كشوف الطلاب التاريخية", root);
            return;
        }
        var yearChoice = new ComboBox { ItemsSource = years, DisplayMemberPath = "Year",
            Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        var schoolChoice = new ComboBox { ItemsSource = schools, DisplayMemberPath = "Name",
            Width = 305, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(Box(LineOfFields(("العام الدراسي", yearChoice), ("المدرسة", schoolChoice))));
        var count = Label("", 14, true);
        root.Children.Add(count);
        var grid = GridFor(Array.Empty<SchoolStudent>(),
            ("رمز الطالب", "StudentCode", false),
            ("اسم الطالب", "FullName", false),
            ("الصف", "Grade", false), ("الشعبة", "Section", false),
            ("العام الدراسي", "AcademicYear", false));
        grid.MaxHeight = 460;
        root.Children.Add(Box(grid));
        void Refresh()
        {
            if (yearChoice.SelectedItem is not AcademicYearRecord year ||
                schoolChoice.SelectedItem is not School school) return;
            var rows = _store.GetHistoricalStudents(_actor, school.Id, year.Year);
            grid.ItemsSource = rows;
            count.Text = $"عدد الطلاب في الأرشيف: {rows.Count}  |  حالة العام: " +
                (year.IsClosed ? "مغلق للقراءة فقط" : year.IsActive ? "العام الحالي" : "عام مفتوح");
        }
        yearChoice.SelectionChanged += (_, _) => Safe(Refresh);
        schoolChoice.SelectionChanged += (_, _) => Safe(Refresh);
        root.Children.Add(Btn("عرض السجل الدراسي للطالب المحدد", (_, _) => Safe(() =>
        {
            if (grid.SelectedItem is not SchoolStudent student ||
                schoolChoice.SelectedItem is not School school)
                throw new InvalidOperationException("حدد الطالب والمدرسة.");
            ShowHistoricalStudentProfile(school, student);
        }), tone: "secondary"));
        Display("كشوف الطلاب التاريخية", root);
        schoolChoice.SelectedItem = schools[0];
        yearChoice.SelectedItem = years.LastOrDefault(y => y.IsClosed) ?? years[^1];
    }

    private void ShowHistoricalStudentProfile(School school, SchoolStudent student)
    {
        var root = Stack();
        root.Children.Add(Btn("العودة إلى الأرشيف", (_, _) => ShowHistoricalRosters(), tone: "secondary"));
        root.Children.Add(Label("الملف التاريخي: " + student.FullName, 21, true));
        root.Children.Add(Label($"العام: {student.AcademicYear} | الصف: {student.Grade} / {student.Section} " +
            $"| المدرسة: {school.Name} | الرقم: {student.StudentCode}", 15));
        root.Children.Add(Label("الدرجات والمواظبة السابقة للعرض فقط؛ لا يحق تعديل العام المغلق.", 13));
        var marks = _store.GetStudentResults(_actor, school.Id, student.Id)
            .Where(r => r.AcademicYear == student.AcademicYear).ToArray();
        root.Children.Add(Box(GridFor(marks,
            ("الفصل", "Term", false), ("المادة", "Subject", false),
            ("الدرجة", "Score", false), ("من", "Maximum", false))));
        var attendance = _store.GetStudentAttendance(_actor, school.Id, student.Id);
        root.Children.Add(Box(GridFor(attendance,
            ("التاريخ", "Date", false), ("الحضور", "Status", false), ("ملاحظات", "Notes", false))));
        AppendStudentTransitions(root, school, student.Id);
        Display("كشوف الطلاب التاريخية", root);
    }

    private void ShowPhysicalClassrooms(School school)
    {
        var root = Stack();
        root.Children.Add(Btn("ملف المدرسة", (_, _) => ShowSchoolProfile(school.Id), tone: "secondary"));
        root.Children.Add(Label("الغرف الفعلية والشعب الدراسية — " + school.Name, 21, true));
        root.Children.Add(Label("الصف والشعبة شيئان مختلفان عن رقم الغرفة. يمكن استخدام الغرفة نفسها " +
            "لشعبتين في فترتين مختلفتين، دون تكرار مكان واحد في الفترة ذاتها.", 14));
        var rooms = _store.GetPhysicalClassrooms(_actor, school.Id).ToArray();
        root.Children.Add(Box(GridFor(rooms,
            ("رقم الغرفة", "Code", false), ("السعة", "Capacity", false))));
        var canEdit = AccessPolicy.CanManageStudents(_actor, school.Id);
        if (canEdit)
        {
            var newRoom = Field("", 195);
            var capacity = Field("30", 95);
            root.Children.Add(Box(LineOfFields(("رقم / اسم الغرفة", newRoom), ("السعة", capacity))));
            root.Children.Add(Btn("إضافة غرفة فعلية", (_, _) => Safe(() =>
            {
                if (!int.TryParse(capacity.Text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number))
                    throw new ArgumentException("السعة غير صحيحة.");
                _store.AddPhysicalClassroom(_actor, school.Id, newRoom.Text, number);
                ShowPhysicalClassrooms(school);
            }), tone: "success"));
        }
        var grades = _store.GetSchoolGrades(_actor, school.Id).ToArray();
        var assignments = _store.GetSectionRoomAssignments(_actor, school.Id, _store.AcademicYear);
        root.Children.Add(Label("توزيع الغرف حسب الشعبة والفترة", 17, true));
        root.Children.Add(Box(GridFor(assignments, ("معرف الشعبة", "GradeId", false),
            ("الغرفة", "RoomId", false), ("الفترة", "Shift", false))));
        if (canEdit && grades.Length > 0 && rooms.Length > 0)
        {
            var grade = new ComboBox { ItemsSource = grades, DisplayMemberPath = "DisplayName",
                Width = 250, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = 0 };
            var room = new ComboBox { ItemsSource = rooms, DisplayMemberPath = "Code",
                Width = 200, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = 0 };
            var shift = Choice(new[] { "صباحي", "مسائي", "ليلي" }, 140);
            root.Children.Add(Box(LineOfFields(("الشعبة", grade), ("الغرفة", room), ("الفترة", shift))));
            root.Children.Add(Btn("ربط الشعبة بالغرفة والفترة", (_, _) => Safe(() =>
            {
                if (grade.SelectedItem is not SchoolGrade selected ||
                    room.SelectedItem is not PhysicalClassroom chosen)
                    throw new ArgumentException("حدد الشعبة والغرفة.");
                _store.AssignSectionRoom(_actor, selected.Id, chosen.Id,
                    shift.SelectedItem?.ToString() ?? "");
                ShowPhysicalClassrooms(school);
            }), tone: "success"));
        }
        Display("الغرف الفعلية والشعب", root);
    }
}
