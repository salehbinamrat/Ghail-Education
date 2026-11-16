using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private sealed record StudentRosterItem(SchoolStudent Student)
    {
        public string StudentCode => Student.StudentCode;
        public string FullName => Student.FullName;
        public int Grade => Student.Grade;
        public string Section => Student.Section;
        public string GuardianName => Student.GuardianName;
        public string StatusArabic => Student.IsActive ? "مسجل" : "غير نشط";
    }

    private static ComboBox Choice(IEnumerable<string> items, double width = 160)
    {
        return new ComboBox
        {
            ItemsSource = items.ToArray(), Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            SelectedIndex = 0
        };
    }

    private static StackPanel FormField(string label, UIElement editor)
    {
        var column = Stack();
        column.Margin = new Thickness(4, 4, 12, 9);
        column.Children.Add(Label(label, 13));
        column.Children.Add(editor);
        return column;
    }

    private static WrapPanel LineOfFields(params (string Caption, UIElement Input)[] fields)
    {
        var line = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (caption, input) in fields)
            line.Children.Add(FormField(caption, input));
        return line;
    }

    private void ShowStudents() => ShowStudentsCore();

    private void ShowStudentsCore(string? preferredSchool = null, int? preferredGrade = null,
        string? preferredSection = null)
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewStudents))
            throw new UnauthorizedAccessException("لا تملك صلاحية الاطلاع على ملفات الطلاب.");
        var root = Stack();
        root.Children.Add(Label("كشوف الصفوف وملفات الطلاب", 20, true));
        root.Children.Add(Label("اختر المدرسة والصف والشعبة ثم ابحث باسم الطالب أو رمزه. اضغط على صف الطالب لفتح ملفه: البيانات، النتائج، التقييمات والحضور.", 14));
        var schools = _store.GetSchools(_actor).Where(s => s.IsActive).ToArray();
        if (schools.Length == 0)
        {
            root.Children.Add(Label("لا توجد مدارس نشطة لهذا الحساب."));
            Display("الطلاب والصفوف", root);
            return;
        }
        var schoolPicker = new ComboBox
        {
            ItemsSource = schools, Width = 350, DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var gradePicker = new ComboBox
        {
            Width = 250, DisplayMemberPath = "DisplayName",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(Box(LineOfFields(("المدرسة", schoolPicker), ("الصف والشعبة", gradePicker))));
        var selectedCount = Label("عدد الطلاب في الكشف: 0", 14, true);
        root.Children.Add(selectedCount);
        var search = Field(width: 285);
        search.ToolTip = "بحث فوري باسم الطالب أو رقمه في الكشف الحالي";
        root.Children.Add(Box(LineOfFields(("بحث في كشف الصف", search)), new Thickness(12)));

        var roster = GridFor(Array.Empty<StudentRosterItem>(),
            ("رمز الطالب", "StudentCode", false),
            ("اسم الطالب — اضغط لفتح ملفه", "FullName", false),
            ("الصف", "Grade", false), ("الشعبة", "Section", false),
            ("ولي الأمر", "GuardianName", false), ("الحالة", "StatusArabic", false));
        roster.MaxHeight = 480;
        root.Children.Add(Box(roster));
        var exportRoster = Btn("تصدير كشف الصف CSV", (_, _) => Safe(() =>
        {
            if (schoolPicker.SelectedItem is not School school ||
                gradePicker.SelectedItem is not SchoolGrade grade)
                throw new InvalidOperationException("حدد المدرسة والصف والشعبة قبل التصدير.");
            if (!AccessPolicy.CanManageStudents(_actor, school.Id))
                throw new UnauthorizedAccessException("لا تملك صلاحية تصدير السجلات الفردية.");
            var dialog = new SaveFileDialog
            {
                Filter = "CSV UTF-8 (*.csv)|*.csv", FileName = "school-class-students.csv",
                Title = "تصدير كشف الطلاب"
            };
            if (dialog.ShowDialog() != true) return;
            var students = _store.GetStudents(_actor, school.Id, grade.Id);
            var columns = new List<string> { "رمز الطالب", "اسم الطالب", "الصف", "الشعبة",
                "ولي الأمر", "هاتف ولي الأمر", "حالة التسجيل" };
            if (school.Gender == "مختلطة") columns.Add("الجنس");
            var output = new List<string> { string.Join(",", columns.Select(CsvCell)) };
            foreach (var student in students)
            {
                var cells = new List<string> { student.StudentCode, student.FullName,
                    student.Grade.ToString(CultureInfo.InvariantCulture), student.Section,
                    student.GuardianName, student.GuardianPhone,
                    student.IsActive ? "مسجل" : "غير نشط" };
                if (school.Gender == "مختلطة") cells.Add(student.Gender);
                output.Add(string.Join(",", cells.Select(CsvCell)));
            }
            File.WriteAllLines(dialog.FileName, output, new System.Text.UTF8Encoding(true));
            Info("تم تصدير كشف الصف المحدد بصيغة CSV UTF-8.");
        }), false, "secondary");
        root.Children.Add(exportRoster);
        var editor = Stack();
        editor.Children.Add(Label("إضافة طالب إلى الصف والشعبة المختارين", 17, true));
        var studentCode = Field(width: 170);
        var fullName = Field(width: 275);
        var guardianName = Field(width: 235);
        var guardianPhone = Field(width: 170);
        var gender = Choice(new[] { "ذكور", "إناث" }, 125);
        var genderField = FormField("جنس الطالب (للمدرسة المختلطة فقط)", gender);
        var inputs = LineOfFields(("رمز الطالب", studentCode), ("الاسم الكامل", fullName),
            ("اسم ولي الأمر", guardianName), ("هاتف ولي الأمر", guardianPhone));
        inputs.Children.Add(genderField);
        editor.Children.Add(inputs);
        editor.Children.Add(Label("ملاحظات ملف الطالب", 13));
        var notes = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 68, MaxHeight = 120, MaxLength = 2000,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        editor.Children.Add(notes);
        var active = new CheckBox { Content = "الطالب مسجل", IsChecked = true,
            Margin = new Thickness(12, 8, 12, 10) };
        editor.Children.Add(active);
        void ResetEditor()
        {
            roster.SelectedItem = null;
            studentCode.Text = ""; fullName.Text = "";
            guardianName.Text = ""; guardianPhone.Text = ""; notes.Text = "";
            active.IsChecked = true;
            gender.SelectedIndex = schoolPicker.SelectedItem is School selectedSchool &&
                selectedSchool.Gender == "مختلطة" ? -1 : 0;
        }

        void RefreshRoster()
        {
            var selectedSchool = schoolPicker.SelectedItem as School;
            var selectedGrade = gradePicker.SelectedItem as SchoolGrade;
            if (selectedSchool is null)
            {
                roster.ItemsSource = Array.Empty<StudentRosterItem>();
                selectedCount.Text = "عدد الطلاب في الكشف: 0";
                return;
            }
            var students = _store.GetStudents(_actor, selectedSchool.Id, selectedGrade?.Id);
            var filter = search.Text.Trim();
            var visible = students.Where(s => filter.Length == 0 ||
                s.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.StudentCode.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            roster.ItemsSource = visible.Select(s => new StudentRosterItem(s)).ToArray();
            selectedCount.Text = filter.Length == 0
                ? $"عدد الطلاب في الكشف: {students.Count}"
                : $"نتائج البحث: {visible.Length} من أصل {students.Count} طالب";
            editor.IsEnabled = AccessPolicy.CanManageStudents(_actor, selectedSchool.Id) &&
                selectedGrade is not null;
            exportRoster.IsEnabled = editor.IsEnabled;
        }

        var newGrade = Choice(Enumerable.Range(1, 12).Select(x => x.ToString(CultureInfo.InvariantCulture)), 100);
        var newSection = Field("أ", 90);
        var newGradePanel = Stack();
        newGradePanel.Children.Add(Label("إضافة صف وشعبة لهذه المدرسة", 16, true));
        newGradePanel.Children.Add(LineOfFields(("الصف", newGrade), ("الشعبة", newSection)));
        newGradePanel.Children.Add(Btn("إضافة الصف والشعبة", (_, _) => Safe(() =>
        {
            if (schoolPicker.SelectedItem is not School school) return;
            var created = _store.AddSchoolGrade(_actor, school.Id,
                int.Parse(newGrade.SelectedItem?.ToString() ?? "0", CultureInfo.InvariantCulture),
                newSection.Text);
            RefillGrades(created.Id);
            Info("تم إنشاء الكشف الدراسي بنجاح.");
        }), tone: "success"));
        root.Children.Add(Box(newGradePanel));

        void RefillGrades(string? selectId = null)
        {
            if (schoolPicker.SelectedItem is not School school) return;
            var available = _store.GetSchoolGrades(_actor, school.Id);
            gradePicker.ItemsSource = available;
            gradePicker.SelectedItem = (selectId is not null
                ? available.FirstOrDefault(x => x.Id == selectId) : null)
                ?? (preferredGrade is not null
                    ? available.FirstOrDefault(x => x.Grade == preferredGrade && x.Section == preferredSection)
                    : null)
                ?? available.FirstOrDefault();
            newGradePanel.IsEnabled = AccessPolicy.CanManageStudents(_actor, school.Id);
            var options = school.Stage switch
            {
                "أساسي" => Enumerable.Range(1, 9),
                "ثانوي" => Enumerable.Range(10, 3),
                _ => Enumerable.Range(1, 12)
            };
            newGrade.ItemsSource = options.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();
            newGrade.SelectedIndex = 0;
            genderField.Visibility = school.Gender == "مختلطة" ? Visibility.Visible : Visibility.Collapsed;
            ResetEditor();
            RefreshRoster();
        }
        search.TextChanged += (_, _) => Safe(RefreshRoster);
        schoolPicker.SelectionChanged += (_, _) => Safe(() => { search.Clear(); RefillGrades(); });
        gradePicker.SelectionChanged += (_, _) => Safe(() =>
        {
            search.Clear(); ResetEditor(); RefreshRoster();
        });
        roster.SelectionChanged += (_, _) =>
        {
            if (roster.SelectedItem is StudentRosterItem selected &&
                schoolPicker.SelectedItem is School school)
                Safe(() => ShowStudentProfile(school, selected.Student.Id));
        };

        var actions = new WrapPanel();
        actions.Children.Add(Btn("مسح الحقول / طالب جديد", (_, _) => ResetEditor(), tone: "secondary"));
        actions.Children.Add(Btn("حفظ بيانات الطالب", (_, _) => Safe(() =>
        {
            if (schoolPicker.SelectedItem is not School school ||
                gradePicker.SelectedItem is not SchoolGrade grade)
                throw new InvalidOperationException("اختر المدرسة والصف والشعبة أولا.");
            _store.SaveStudent(_actor, new SchoolStudent("", school.Id,
                studentCode.Text, fullName.Text, grade.Grade, grade.Section,
                school.Gender == "مختلطة" ? gender.SelectedItem?.ToString() ?? "" : "",
                guardianName.Text, guardianPhone.Text, notes.Text, active.IsChecked == true,
                _store.AcademicYear));
            Info("تم حفظ الطالب في كشف صفه.");
            ResetEditor(); RefreshRoster();
        }), tone: "success"));
        editor.Children.Add(actions);
        root.Children.Add(Box(editor));

        var import = Stack();
        import.Children.Add(Label("استيراد كشف الطلاب من Excel أو Word", 16, true));
        import.Children.Add(Label("يدعم .xlsx و.docx المنظمة. يلزم رقم الطالب والاسم والصف، وتُقبل الشعبة والجنس ورمز المدرسة وولي الأمر كأعمدة إضافية. أضف الصف والشعبة قبل الاستيراد.", 13));
        import.Children.Add(Btn("اختيار ملف واستيراد الطلاب", async (_, _) =>
        {
            if (schoolPicker.SelectedItem is not School school) return;
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Excel (*.xlsx)|*.xlsx|Word (*.docx)|*.docx",
                    Title = "اختر كشف طلاب المدرسة"
                };
                if (dialog.ShowDialog() != true) return;
                var rows = await Task.Run(() => StudentImportParser.Parse(dialog.FileName));
                var byGrade = rows.GroupBy(x => (x.Grade, x.Section))
                    .Select(g => $"الصف {g.Key.Grade} / {g.Key.Section}: {g.Count()} طالب")
                    .Take(12);
                var message = $"المدرسة المحددة: {school.Name}\nعدد السجلات: {rows.Count}\n" +
                    string.Join("\n", byGrade) +
                    "\n\nسيتم تحديث سجل الطالب الموجود حسب رمزه داخل المدرسة نفسها." +
                    "\nلن تُحذف السجلات الغائبة عن الملف. هل توافق على الاستيراد؟";
                if (MessageBox.Show(message, "مراجعة كشف الطلاب",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var saved = await Task.Run(() => _store.ImportStudents(_actor, school.Id,
                    rows, Path.GetFileName(dialog.FileName)));
                Info($"تم استيراد {saved} طالبا بأمان إلى المدرسة المحددة.");
                RefreshRoster();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تعذر استيراد كشف الطلاب",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, tone: "secondary"));
        root.Children.Add(Box(import));
        schoolPicker.SelectionChanged += (_, _) =>
        {
            if (schoolPicker.SelectedItem is School school)
                import.IsEnabled = AccessPolicy.CanManageStudents(_actor, school.Id);
        };

        Display("الطلاب والصفوف", root);
        schoolPicker.SelectedItem = schools.FirstOrDefault(x => x.Id == preferredSchool) ?? schools[0];
    }

    private void ShowStudentProfile(School school, string studentId)
    {
        var student = _store.GetStudent(_actor, school.Id, studentId);
        var root = Stack();
        root.Children.Add(Btn("العودة إلى كشوف الصفوف", (_, _) =>
            ShowStudentsCore(school.Id, student.Grade, student.Section), tone: "secondary"));
        root.Children.Add(Label($"ملف الطالب: {student.FullName}", 21, true));
        root.Children.Add(Label($"الرقم المدرسي: {student.StudentCode}  |  الصف {student.Grade} / {student.Section}" +
            $"  |  العام الدراسي: {student.AcademicYear}", 14));
        root.Children.Add(Box(Label($"ولي الأمر: {student.GuardianName}  |  الهاتف: {student.GuardianPhone}" +
            (school.Gender == "مختلطة" ? $"  |  الجنس: {student.Gender}" : ""), 14)));

        var tabs = new TabControl
        {
            Margin = new Thickness(2, 10, 2, 14), MinHeight = 370,
            BorderBrush = new SolidColorBrush(Color.FromRgb(161, 212, 228)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White
        };

        var personal = Stack();
        personal.Children.Add(Label("المعلومات الأساسية للطالب", 17, true));
        var name = Field(student.FullName, 280);
        var code = Field(student.StudentCode, 155);
        var guardian = Field(student.GuardianName, 260);
        var phone = Field(student.GuardianPhone, 190);
        var notes = new TextBox { Text = student.Notes, MinHeight = 65, MaxHeight = 140,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 2000 };
        var active = new CheckBox { IsChecked = student.IsActive, Content = "الطالب مسجل",
            Margin = new Thickness(10) };
        var gradeOptions = _store.GetSchoolGrades(_actor, school.Id);
        var gradeChoice = new ComboBox { ItemsSource = gradeOptions, DisplayMemberPath = "DisplayName",
            Width = 220, HorizontalAlignment = HorizontalAlignment.Left,
            SelectedItem = gradeOptions.FirstOrDefault(x => x.Grade == student.Grade && x.Section == student.Section) };
        var genderChoice = Choice(new[] { "ذكور", "إناث" }, 130);
        genderChoice.SelectedItem = student.Gender;
        var contact = LineOfFields(("رمز الطالب", code), ("اسم الطالب", name),
            ("الصف والشعبة", gradeChoice), ("اسم ولي الأمر", guardian), ("الهاتف", phone));
        if (school.Gender == "مختلطة")
            contact.Children.Add(FormField("جنس الطالب", genderChoice));
        personal.Children.Add(contact);
        personal.Children.Add(FormField("ملاحظات", notes));
        personal.Children.Add(active);
        var canEdit = AccessPolicy.CanManageStudents(_actor, school.Id);
        personal.IsEnabled = canEdit;
        personal.Children.Add(Btn("حفظ التعديلات على الملف", (_, _) => Safe(() =>
        {
            if (gradeChoice.SelectedItem is not SchoolGrade grade)
                throw new InvalidOperationException("اختر الصف والشعبة.");
            _store.SaveStudent(_actor, student with
            {
                StudentCode = code.Text, FullName = name.Text,
                Grade = grade.Grade, Section = grade.Section,
                Gender = school.Gender == "مختلطة" ? genderChoice.SelectedItem?.ToString() ?? "" : "",
                GuardianName = guardian.Text, GuardianPhone = phone.Text, Notes = notes.Text,
                IsActive = active.IsChecked == true
            });
            Info("تم حفظ التعديلات على بيانات الطالب."); ShowStudentProfile(school, studentId);
        }), canEdit, "success"));
        tabs.Items.Add(new TabItem { Header = "الملف الأساسي", Content = Box(personal) });

        var results = Stack();
        results.Children.Add(Label("النتائج الدراسية حسب المادة والفصل والسنة", 17, true));
        results.Children.Add(Box(GridFor(_store.GetStudentResults(_actor, school.Id, student.Id),
            ("السنة", "AcademicYear", false), ("الفصل", "Term", false),
            ("المادة", "Subject", false), ("الدرجة", "Score", false),
            ("من", "Maximum", false), ("ملاحظات", "Notes", false))));
        var term = Choice(new[] { "الفصل الأول", "الفصل الثاني", "النهائي" }, 150);
        var subject = Field(width: 200);
        var score = Field(width: 90);
        var maximum = Field("100", 90);
        var resultNote = Field(width: 230);
        results.Children.Add(LineOfFields(("الفصل الدراسي", term), ("المادة", subject),
            ("الدرجة", score), ("الدرجة الكاملة", maximum), ("ملاحظات", resultNote)));
        results.Children.Add(Btn("حفظ نتيجة المادة", (_, _) => Safe(() =>
        {
            if (!int.TryParse(score.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var achieved) ||
                !int.TryParse(maximum.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var full))
                throw new ArgumentException("أدخل درجة المادة والدرجة الكاملة بأرقام إنجليزية.");
            _store.SaveStudentResult(_actor, school.Id, student.Id,
                term.SelectedItem?.ToString() ?? "", subject.Text, achieved, full, resultNote.Text);
            Info("تم حفظ نتيجة المادة."); ShowStudentProfile(school, studentId);
        }), AccessPolicy.CanManageStudentEvaluation(_actor, school.Id), "success"));
        tabs.Items.Add(new TabItem { Header = "النتائج", Content = Box(results) });

        var assessment = Stack();
        assessment.Children.Add(Label("التقييم التربوي والسلوك والمواظبة", 17, true));
        assessment.Children.Add(Box(GridFor(_store.GetStudentAssessments(_actor, school.Id, student.Id),
            ("الفئة", "Category", false), ("التقدير", "Rating", false),
            ("الملاحظات", "Notes", false), ("التاريخ", "RecordedAt", false))));
        var category = Choice(new[] { "المشاركة", "السلوك", "المواظبة", "المهارات", "التقييم العام" }, 170);
        var rating = Choice(new[] { "ممتاز", "جيد جدا", "جيد", "مقبول", "يحتاج متابعة" }, 155);
        var assessmentNote = Field(width: 300);
        assessment.Children.Add(LineOfFields(("المجال", category), ("التقدير", rating),
            ("ملاحظات المعلم / الأخصائي", assessmentNote)));
        assessment.Children.Add(Btn("إضافة تقييم للطالب", (_, _) => Safe(() =>
        {
            _store.AddStudentAssessment(_actor, school.Id, student.Id,
                category.SelectedItem?.ToString() ?? "", rating.SelectedItem?.ToString() ?? "",
                assessmentNote.Text);
            Info("تم حفظ تقييم الطالب."); ShowStudentProfile(school, studentId);
        }), AccessPolicy.CanManageStudentEvaluation(_actor, school.Id), "success"));
        tabs.Items.Add(new TabItem { Header = "التقييمات", Content = Box(assessment) });

        var attendance = Stack();
        attendance.Children.Add(Label("الحضور والغياب والتأخر والأعذار", 17, true));
        attendance.Children.Add(Box(GridFor(_store.GetStudentAttendance(_actor, school.Id, student.Id),
            ("اليوم", "Date", false), ("الحالة", "Status", false), ("ملاحظات", "Notes", false))));
        var date = new DatePicker { SelectedDate = DateTime.Today, Width = 160 };
        var state = Choice(new[] { "حاضر", "غائب", "متأخر", "بعذر" }, 145);
        var attendanceNote = Field(width: 270);
        attendance.Children.Add(LineOfFields(("التاريخ", date), ("حالة الحضور", state),
            ("ملاحظات", attendanceNote)));
        attendance.Children.Add(Btn("حفظ الحضور", (_, _) => Safe(() =>
        {
            if (date.SelectedDate is not DateTime day)
                throw new ArgumentException("حدد تاريخ الحضور.");
            _store.RecordStudentAttendance(_actor, school.Id, student.Id, DateOnly.FromDateTime(day),
                state.SelectedItem?.ToString() ?? "", attendanceNote.Text);
            Info("تم حفظ سجل الحضور."); ShowStudentProfile(school, studentId);
        }), AccessPolicy.CanManageStudentEvaluation(_actor, school.Id), "success"));
        tabs.Items.Add(new TabItem { Header = "الحضور", Content = Box(attendance) });

        root.Children.Add(tabs);
        Display("ملف الطالب", root);
    }
}
