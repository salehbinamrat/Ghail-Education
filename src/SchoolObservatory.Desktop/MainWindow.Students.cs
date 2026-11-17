using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private sealed record StudentRosterItem(SchoolStudent Student, string Stage)
    {
        public string StudentCode => Student.StudentCode;
        public string FullName => Student.FullName;
        public string GradeName => GradeNames.ForStage(Stage, Student.Grade);
        public string Section => Student.Section;
        public string GuardianName => Student.GuardianName;
        public string StatusArabic => Student.IsActive ? "مسجل" : "غير نشط";
    }

    private sealed record GradeChoice(int Number, string Label);

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
        root.Children.Add(Label("اختر المدرسة والصف والشعبة ثم ابحث باسم الطالب أو رمزه أو اسم ولي الأمر أو هاتفه. لفتح ملف الطالب، حدده واضغط زر فتح الملف أو انقر مرتين على صفه.", 14));
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
        selectedCount.Foreground = new SolidColorBrush(Color.FromRgb(5, 91, 131));
        root.Children.Add(Box(selectedCount, new Thickness(12)));
        var search = Field(width: 285);
        search.ToolTip = "بحث محلي برمز الطالب أو اسمه أو ولي الأمر أو هاتفه، مع دعم الأرقام العربية";
        root.Children.Add(Box(LineOfFields(("بحث في كشف الصف", search)), new Thickness(12)));

        var roster = GridFor(Array.Empty<StudentRosterItem>(),
            ("رمز الطالب", "StudentCode", false),
            ("اسم الطالب — انقر مرتين لفتح ملفه", "FullName", false),
            ("المرحلة / الصف", "GradeName", false), ("الشعبة", "Section", false),
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
                    GradeNames.ForStage(school.Stage, student.Grade), student.Section,
                    student.GuardianName, student.GuardianPhone,
                    student.IsActive ? "مسجل" : "غير نشط" };
                if (school.Gender == "مختلطة") cells.Add(student.Gender);
                output.Add(string.Join(",", cells.Select(CsvCell)));
            }
            File.WriteAllLines(dialog.FileName, output, new System.Text.UTF8Encoding(true));
            Info("تم تصدير كشف الصف المحدد بصيغة CSV UTF-8.");
        }), false, "secondary");
        root.Children.Add(exportRoster);
        var printRoster = Btn("طباعة كشف الصف", (_, _) => Safe(() =>
        {
            if (schoolPicker.SelectedItem is not School school ||
                gradePicker.SelectedItem is not SchoolGrade grade)
                throw new InvalidOperationException("اختر المدرسة والصف والشعبة أولا.");
            if (!AccessPolicy.CanManageStudents(_actor, school.Id))
                throw new UnauthorizedAccessException("لا تملك صلاحية طباعة الكشف التفصيلي.");

            // Obtain a complete, authoritative roster; a search filter must not accidentally
            // truncate the official printable class list.
            var students = _store.GetStudents(_actor, school.Id, grade.Id);
            var printer = new PrintDialog();
            if (printer.ShowDialog() != true) return;
            var width = Math.Max(320, printer.PrintableAreaWidth);
            var height = Math.Max(450, printer.PrintableAreaHeight);
            var document = new FlowDocument
            {
                FlowDirection = FlowDirection.RightToLeft,
                FontFamily = new FontFamily("Arial"),
                FontSize = 11,
                PageWidth = width,
                PageHeight = height,
                PagePadding = new Thickness(28),
                ColumnWidth = width - 56
            };
            document.Blocks.Add(new Paragraph(new Run("المرصد المدرسي — كشف طلاب المدرسة"))
            {
                FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(7, 82, 126)),
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 8)
            });
            document.Blocks.Add(new Paragraph(new Run(
                $"المدرسة: {school.Name} ({school.Code})   |   {grade.DisplayName}   |   العام الدراسي: {grade.AcademicYear}"))
            {
                FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });
            document.Blocks.Add(new Paragraph(new Run(
                $"عدد الطلاب: {students.Count}   |   تاريخ الطباعة: {DateTime.Now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}"))
            {
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12)
            });

            var columns = school.Gender == "مختلطة"
                ? new[] { "م", "رقم الطالب", "اسم الطالب", "الصف", "الشعبة", "الجنس", "ولي الأمر" }
                : new[] { "م", "رقم الطالب", "اسم الطالب", "الصف", "الشعبة", "ولي الأمر" };
            var table = new Table { CellSpacing = 0 };
            foreach (var label in columns)
            {
                var weight = label == "اسم الطالب" ? 4 :
                    label == "ولي الأمر" ? 3 : label == "رقم الطالب" ? 2 : 1;
                table.Columns.Add(new TableColumn
                {
                    Width = new GridLength(weight, GridUnitType.Star)
                });
            }
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            void AppendRow(IReadOnlyList<string> cells, bool header)
            {
                var row = new TableRow();
                if (header)
                    row.Background = new SolidColorBrush(Color.FromRgb(224, 244, 249));
                foreach (var value in cells)
                {
                    row.Cells.Add(new TableCell(new Paragraph(new Run(value))
                    {
                        Margin = new Thickness(0), TextAlignment = TextAlignment.Right
                    })
                    {
                        Padding = new Thickness(4, 6, 4, 6),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(188, 218, 226)),
                        BorderThickness = new Thickness(0, 0, 0, 0.7),
                        FontWeight = header ? FontWeights.Bold : FontWeights.Normal
                    });
                }
                group.Rows.Add(row);
            }
            AppendRow(columns, true);
            for (var i = 0; i < students.Count; i++)
            {
                var student = students[i];
                var cells = new List<string>
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture), student.StudentCode,
                    student.FullName, GradeNames.ForStage(school.Stage, student.Grade),
                    student.Section
                };
                if (school.Gender == "مختلطة") cells.Add(student.Gender);
                cells.Add(student.GuardianName);
                AppendRow(cells, false);
            }
            document.Blocks.Add(table);
            document.Blocks.Add(new Paragraph(new Run(
                "مراجعة مسؤول المدرسة: ...........................     التوقيع والختم: ..........................."))
            {
                Margin = new Thickness(0, 22, 0, 0),
                TextAlignment = TextAlignment.Right
            });
            printer.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator,
                $"كشف {school.Name} — {grade.DisplayName}");
        }), false, "secondary");
        root.Children.Add(printRoster);
        var editor = Stack();
        editor.Children.Add(Label("إضافة طالب جديد إلى الكشف المحدد. لتصحيح اسم طالب موجود افتح ملفه من الجدول.", 17, true));
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

        IReadOnlyList<SchoolStudent> rosterCache = Array.Empty<SchoolStudent>();
        string? cachedSchoolId = null;
        string? cachedGradeId = null;
        void RefreshRoster(bool reload = true)
        {
            var selectedSchool = schoolPicker.SelectedItem as School;
            var selectedGrade = gradePicker.SelectedItem as SchoolGrade;
            if (selectedSchool is null || selectedGrade is null)
            {
                roster.ItemsSource = Array.Empty<StudentRosterItem>();
                rosterCache = Array.Empty<SchoolStudent>();
                cachedSchoolId = null;
                cachedGradeId = null;
                selectedCount.Text = selectedSchool is null
                    ? "اختر المدرسة لعرض كشوف الطلاب."
                    : "لا يوجد كشف محدد. أضف صفا وشعبة أو اختر كشفا موجودا.";
                editor.IsEnabled = false;
                exportRoster.IsEnabled = false;
                printRoster.IsEnabled = false;
                return;
            }
            if (reload || cachedSchoolId != selectedSchool.Id || cachedGradeId != selectedGrade.Id)
            {
                rosterCache = _store.GetStudents(_actor, selectedSchool.Id, selectedGrade.Id);
                cachedSchoolId = selectedSchool.Id;
                cachedGradeId = selectedGrade.Id;
            }
            var students = rosterCache;
            var filter = search.Text.Trim();
            var visible = SchoolStudentSearch.Find(students, filter);
            roster.ItemsSource = visible.Select(s => new StudentRosterItem(s, selectedSchool.Stage)).ToArray();
            selectedCount.Text = filter.Length == 0
                ? $"عدد الطلاب في الكشف: {students.Count}"
                : $"نتائج البحث: {visible.Count} من أصل {students.Count} طالب";
            editor.IsEnabled = AccessPolicy.CanManageStudents(_actor, selectedSchool.Id) &&
                selectedGrade is not null;
            exportRoster.IsEnabled = editor.IsEnabled;
            printRoster.IsEnabled = editor.IsEnabled;
        }

        var newGrade = new ComboBox
        {
            Width = 215, DisplayMemberPath = "Label",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var newSection = Field("أ", 90);
        var newGradePanel = Stack();
        newGradePanel.Children.Add(Label("إضافة كشف أو تصحيح الصف والشعبة المحددين دون حذف الطلاب", 16, true));
        newGradePanel.Children.Add(LineOfFields(("المرحلة / الصف", newGrade), ("الشعبة", newSection)));
        newGradePanel.Children.Add(Btn("إضافة الصف والشعبة", (_, _) => Safe(() =>
        {
            if (schoolPicker.SelectedItem is not School school) return;
            if (newGrade.SelectedItem is not GradeChoice selectedGrade)
                throw new InvalidOperationException("اختر المرحلة والصف أولا.");
            var created = _store.AddSchoolGrade(_actor, school.Id,
                selectedGrade.Number, newSection.Text);
            RefillGrades(created.Id);
            Info("تم إنشاء الكشف الدراسي بنجاح.");
        }), tone: "success"));
        newGradePanel.Children.Add(Btn("تعديل الصف والشعبة", (_, _) => Safe(() =>
        {
            if (schoolPicker.SelectedItem is not School school ||
                gradePicker.SelectedItem is not SchoolGrade current ||
                newGrade.SelectedItem is not GradeChoice selected)
                throw new InvalidOperationException("حدد كشف الصف والشعبة المطلوب تصحيحه أولا.");
            var enrolled = _store.GetStudents(_actor, school.Id, current.Id).Count;
            if (enrolled > 0 && MessageBox.Show(
                $"سيتم تغيير اسم الصف أو شعبته مع بقاء {enrolled} طالبا في الكشف نفسه، دون حذف ملفاتهم أو نتائجهم. هل تؤكد التصحيح؟",
                "تأكيد تصحيح الكشف", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _store.UpdateSchoolGrade(_actor, school.Id, current.Id,
                selected.Number, newSection.Text);
            RefillGrades(current.Id);
            Info("تم تصحيح الصف والشعبة مع الحفاظ على سجلات الطلاب.");
        }), tone: "warning"));
        root.Children.Add(Box(newGradePanel));

        bool suppressGradeSelection = false;
        bool suppressSearch = false;
        void ClearSearch()
        {
            suppressSearch = true;
            try { search.Clear(); }
            finally { suppressSearch = false; }
        }

        void RefillGrades(string? selectId = null)
        {
            if (schoolPicker.SelectedItem is not School school) return;
            var available = _store.GetSchoolGrades(_actor, school.Id);
            suppressGradeSelection = true;
            try
            {
                gradePicker.ItemsSource = available;
                gradePicker.SelectedItem = (selectId is not null
                    ? available.FirstOrDefault(x => x.Id == selectId) : null)
                    ?? (preferredGrade is not null
                        ? available.FirstOrDefault(x => x.Grade == preferredGrade && x.Section == preferredSection)
                        : null)
                    ?? available.FirstOrDefault();
            }
            finally { suppressGradeSelection = false; }
            newGradePanel.IsEnabled = AccessPolicy.CanManageStudents(_actor, school.Id);
            var options = GradeNames.Allowed(school.Stage)
                .Select(n => new GradeChoice(n, GradeNames.ForStage(school.Stage, n)))
                .ToArray();
            newGrade.ItemsSource = options;
            newGrade.SelectedItem = gradePicker.SelectedItem is SchoolGrade picked
                ? options.FirstOrDefault(x => x.Number == picked.Grade)
                : options.FirstOrDefault();
            newSection.Text = gradePicker.SelectedItem is SchoolGrade selected
                ? selected.Section : "أ";
            genderField.Visibility = school.Gender == "مختلطة" ? Visibility.Visible : Visibility.Collapsed;
            ResetEditor();
            RefreshRoster();
        }
        search.TextChanged += (_, _) =>
        {
            if (!suppressSearch) Safe(() => RefreshRoster(false));
        };
        schoolPicker.SelectionChanged += (_, _) => Safe(() => { ClearSearch(); RefillGrades(); });
        gradePicker.SelectionChanged += (_, _) => Safe(() =>
        {
            if (suppressGradeSelection) return;
            ClearSearch(); ResetEditor(); RefreshRoster();
            if (gradePicker.SelectedItem is SchoolGrade current)
            {
                newGrade.SelectedItem = (newGrade.ItemsSource as IEnumerable<GradeChoice>)
                    ?.FirstOrDefault(x => x.Number == current.Grade);
                newSection.Text = current.Section;
            }
        });
        void OpenSelectedStudent()
        {
            if (roster.SelectedItem is StudentRosterItem selected &&
                schoolPicker.SelectedItem is School school)
                Safe(() => ShowStudentProfile(school, selected.Student.Id));
            else
                Info("حدد طالبا من الكشف ثم اضغط «فتح ملف الطالب المحدد» أو انقر نقرا مزدوجا على اسمه.");
        }
        // Keyboard navigation and sorting must not unexpectedly open another student's file.
        roster.MouseDoubleClick += (_, e) =>
        {
            DependencyObject? origin = e.OriginalSource as DependencyObject;
            while (origin is not null && origin is not DataGridRow && origin != roster)
                origin = VisualTreeHelper.GetParent(origin);
            if (origin is DataGridRow) OpenSelectedStudent();
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
        bool isImporting = false;
        import.Children.Add(Btn("اختيار ملف واستيراد الطلاب", async (_, _) =>
        {
            if (isImporting) return;
            if (schoolPicker.SelectedItem is not School school) return;
            isImporting = true;
            import.IsEnabled = false;
            schoolPicker.IsEnabled = false;
            gradePicker.IsEnabled = false;
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
                    .Select(g => $"{GradeNames.ForStage(school.Stage, g.Key.Grade)} / الشعبة {g.Key.Section}: {g.Count()} طالب")
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
            finally
            {
                isImporting = false;
                schoolPicker.IsEnabled = true;
                gradePicker.IsEnabled = true;
                import.IsEnabled = schoolPicker.SelectedItem is School current &&
                    AccessPolicy.CanManageStudents(_actor, current.Id);
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

    private void ShowStudentProfile(School school, string studentId, int selectedTab = 0)
    {
        var student = _store.GetStudent(_actor, school.Id, studentId);
        var root = Stack();
        root.Children.Add(Btn("العودة إلى كشوف الصفوف", (_, _) =>
            ShowStudentsCore(school.Id, student.Grade, student.Section), tone: "secondary"));
        root.Children.Add(Label($"ملف الطالب: {student.FullName}", 21, true));
        root.Children.Add(Label($"الرقم المدرسي: {student.StudentCode}  |  {GradeNames.ForStage(school.Stage, student.Grade)} / الشعبة {student.Section}" +
            $"  |  العام الدراسي: {student.AcademicYear}", 14));
        if (!student.IsActive)
        {
            var warning = Label("ملف غير نشط — السجلات الدراسية السابقة متاحة للقراءة، والإدخال الجديد معطل.", 13, true);
            warning.Foreground = new SolidColorBrush(Color.FromRgb(163, 67, 26));
            root.Children.Add(Box(warning, new Thickness(12)));
        }
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
        name.IsReadOnly = !canEdit;
        code.IsReadOnly = !canEdit;
        guardian.IsReadOnly = !canEdit;
        phone.IsReadOnly = !canEdit;
        notes.IsReadOnly = !canEdit;
        gradeChoice.IsEnabled = canEdit;
        genderChoice.IsEnabled = canEdit;
        active.IsEnabled = canEdit;
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
        results.Children.Add(Label("النتائج الدراسية: اختر نتيجة من العام الحالي لتعديل درجتها أو ملاحظاتها ثم احفظ.", 17, true));
        var resultsGrid = GridFor(Array.Empty<StudentResult>(),
            ("السنة", "AcademicYear", false), ("الفصل", "Term", false),
            ("المادة", "Subject", false), ("الدرجة", "Score", false),
            ("من", "Maximum", false), ("ملاحظات", "Notes", false));
        results.Children.Add(Box(resultsGrid));
        var term = Choice(new[] { "الفصل الأول", "الفصل الثاني", "النهائي" }, 150);
        var subject = Field(width: 200);
        var score = Field(width: 90);
        var maximum = Field("100", 90);
        var resultNote = Field(width: 230);
        resultNote.MaxLength = 1000;
        // Click a current-year record to correct its value instead of adding a duplicate.
        resultsGrid.SelectionChanged += (_, _) =>
        {
            if (resultsGrid.SelectedItem is not StudentResult selected ||
                selected.AcademicYear != _store.AcademicYear) return;
            term.SelectedItem = selected.Term;
            subject.Text = selected.Subject;
            score.Text = selected.Score.ToString(CultureInfo.InvariantCulture);
            maximum.Text = selected.Maximum.ToString(CultureInfo.InvariantCulture);
            resultNote.Text = selected.Notes;
        };
        var canEvaluate = student.IsActive &&
            AccessPolicy.CanManageStudentEvaluation(_actor, school.Id);
        var resultEditor = LineOfFields(("الفصل الدراسي", term), ("المادة", subject),
            ("الدرجة", score), ("الدرجة الكاملة", maximum), ("ملاحظات", resultNote));
        resultEditor.IsEnabled = canEvaluate;
        results.Children.Add(resultEditor);
        results.Children.Add(Btn("حفظ نتيجة المادة", (_, _) => Safe(() =>
        {
            if (!int.TryParse(score.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var achieved) ||
                !int.TryParse(maximum.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var full))
                throw new ArgumentException("أدخل درجة المادة والدرجة الكاملة بأرقام إنجليزية.");
            _store.SaveStudentResult(_actor, school.Id, student.Id,
                term.SelectedItem?.ToString() ?? "", subject.Text, achieved, full, resultNote.Text);
            Info("تم حفظ نتيجة المادة."); ShowStudentProfile(school, studentId, 1);
        }), canEvaluate, "success"));
        tabs.Items.Add(new TabItem { Header = "النتائج", Content = Box(results) });

        var assessment = Stack();
        assessment.Children.Add(Label("التقييم التربوي: يمكن إضافة تقييم جديد أو تحديد تقييم من العام الحالي وتصحيحه.", 17, true));
        var assessmentsGrid = GridFor(Array.Empty<StudentAssessment>(),
            ("الفئة", "Category", false), ("التقدير", "Rating", false),
            ("الملاحظات", "Notes", false), ("التاريخ", "RecordedAt", false));
        assessment.Children.Add(Box(assessmentsGrid));
        var category = Choice(new[] { "المشاركة", "السلوك", "المواظبة", "المهارات", "التقييم العام" }, 170);
        var rating = Choice(new[] { "ممتاز", "جيد جدا", "جيد", "مقبول", "يحتاج متابعة" }, 155);
        var assessmentNote = Field(width: 300);
        assessmentNote.MaxLength = 1000;
        assessmentsGrid.SelectionChanged += (_, _) =>
        {
            if (assessmentsGrid.SelectedItem is not StudentAssessment selected ||
                selected.AcademicYear != _store.AcademicYear) return;
            category.SelectedItem = selected.Category;
            rating.SelectedItem = selected.Rating;
            assessmentNote.Text = selected.Notes;
        };
        var assessmentEditor = LineOfFields(("المجال", category), ("التقدير", rating),
            ("ملاحظات المعلم / الأخصائي", assessmentNote));
        assessmentEditor.IsEnabled = canEvaluate;
        assessment.Children.Add(assessmentEditor);
        assessment.Children.Add(Btn("إضافة تقييم للطالب", (_, _) => Safe(() =>
        {
            _store.AddStudentAssessment(_actor, school.Id, student.Id,
                category.SelectedItem?.ToString() ?? "", rating.SelectedItem?.ToString() ?? "",
                assessmentNote.Text);
            Info("تم حفظ تقييم الطالب."); ShowStudentProfile(school, studentId, 2);
        }), canEvaluate, "purple"));
        assessment.Children.Add(Btn("حفظ تصحيح التقييم المحدد", (_, _) => Safe(() =>
        {
            if (assessmentsGrid.SelectedItem is not StudentAssessment selected)
                throw new InvalidOperationException("حدد التقييم المطلوب تصحيحه من الجدول.");
            if (selected.AcademicYear != _store.AcademicYear)
                throw new InvalidOperationException("التقييم التاريخي متاح للقراءة فقط.");
            _store.UpdateStudentAssessment(_actor, school.Id, student.Id, selected.Id,
                category.SelectedItem?.ToString() ?? "",
                rating.SelectedItem?.ToString() ?? "", assessmentNote.Text);
            Info("تم تصحيح التقييم دون تغيير ملف الطالب."); ShowStudentProfile(school, studentId, 2);
        }), canEvaluate, "warning"));
        tabs.Items.Add(new TabItem { Header = "التقييمات", Content = Box(assessment) });

        var attendance = Stack();
        attendance.Children.Add(Label("الحضور والغياب: اختر تاريخا جديدا أو حدد سجلا سابقا لتصحيح الحالة وملاحظاتها.", 17, true));
        var attendanceGrid = GridFor(Array.Empty<StudentAttendance>(),
            ("اليوم", "Date", false), ("الحالة", "Status", false), ("ملاحظات", "Notes", false));
        attendance.Children.Add(Box(attendanceGrid));
        var date = new DatePicker { SelectedDate = DateTime.Today, Width = 160 };
        var state = Choice(new[] { "حاضر", "غائب", "متأخر", "بعذر" }, 145);
        var attendanceNote = Field(width: 270);
        attendanceNote.MaxLength = 400;
        attendanceGrid.SelectionChanged += (_, _) =>
        {
            if (attendanceGrid.SelectedItem is not StudentAttendance selected) return;
            if (DateTime.TryParseExact(selected.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var selectedDay))
                date.SelectedDate = selectedDay;
            state.SelectedItem = selected.Status;
            attendanceNote.Text = selected.Notes;
        };
        var attendanceEditor = LineOfFields(("التاريخ", date), ("حالة الحضور", state),
            ("ملاحظات", attendanceNote));
        attendanceEditor.IsEnabled = canEvaluate;
        attendance.Children.Add(attendanceEditor);
        attendance.Children.Add(Btn("حفظ الحضور", (_, _) => Safe(() =>
        {
            if (date.SelectedDate is not DateTime day)
                throw new ArgumentException("حدد تاريخ الحضور.");
            _store.RecordStudentAttendance(_actor, school.Id, student.Id, DateOnly.FromDateTime(day),
                state.SelectedItem?.ToString() ?? "", attendanceNote.Text);
            Info("تم حفظ سجل الحضور."); ShowStudentProfile(school, studentId, 3);
        }), canEvaluate, "warning"));
        tabs.Items.Add(new TabItem { Header = "الحضور", Content = Box(attendance) });

        // Retrieve history on demand: opening a student no longer requires
        // three extra SQLite queries for tabs that may never be visited.
        bool resultsLoaded = false, assessmentsLoaded = false, attendanceLoaded = false;
        void LoadSelectedTab()
        {
            switch (tabs.SelectedIndex)
            {
                case 1 when !resultsLoaded:
                    resultsGrid.ItemsSource = _store.GetStudentResults(_actor, school.Id, student.Id);
                    resultsLoaded = true;
                    break;
                case 2 when !assessmentsLoaded:
                    assessmentsGrid.ItemsSource = _store.GetStudentAssessments(_actor, school.Id, student.Id);
                    assessmentsLoaded = true;
                    break;
                case 3 when !attendanceLoaded:
                    attendanceGrid.ItemsSource = _store.GetStudentAttendance(_actor, school.Id, student.Id);
                    attendanceLoaded = true;
                    break;
            }
        }
        tabs.SelectionChanged += (_, e) =>
        {
            // DataGrid selections also raise this bubbling event.
            if (ReferenceEquals(e.OriginalSource, tabs)) Safe(LoadSelectedTab);
        };
        root.Children.Add(tabs);
        Display("ملف الطالب", root);
        tabs.SelectedIndex = Math.Clamp(selectedTab, 0, 3);
        Safe(LoadSelectedTab);
    }
}
