using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private static Border ProfileTile(string caption, string value, double width = 245)
    {
        var content = Stack();
        var heading = Label(caption, 13, true);
        heading.Foreground = new SolidColorBrush(Color.FromRgb(27, 91, 133));
        var text = Label(string.IsNullOrWhiteSpace(value) ? "غير مدخل" : value, 15);
        text.Foreground = new SolidColorBrush(Color.FromRgb(16, 48, 74));
        content.Children.Add(heading);
        content.Children.Add(text);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(234, 248, 253)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(179, 221, 235)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Margin = new Thickness(3, 4, 8, 8), Padding = new Thickness(10),
            Width = width, Child = content
        };
    }

    private static TextBox ProfileNotes(string value, int maximum = 2500) => new()
    {
        Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 75, MaxHeight = 150, MaxLength = maximum,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private static int? OptionalProfileYear(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result is < 1900 or > 2100)
            throw new ArgumentException($"سنة {label} يجب أن تكون بين 1900 و2100 بالأرقام الإنجليزية.");
        return result;
    }

    private void ShowSchoolProfile(string schoolId)
    {
        // The school list is permission-scoped. Never display another institution by guessed ID.
        var school = _store.GetSchools(_actor).FirstOrDefault(s => s.Id == schoolId)
            ?? throw new UnauthorizedAccessException("لا يمكنك فتح ملف هذه المدرسة.");
        var details = _store.GetSchoolDirectoryDetails(_actor, schoolId);
        var root = Stack();
        root.Children.Add(Btn("العودة إلى دليل المدارس", (_, _) => ShowSchools(), tone: "secondary"));
        root.Children.Add(Label($"ملف المدرسة — {school.Name}", 22, true));
        root.Children.Add(Label("البيانات الأساسية، الإدارة، الكادر، الصفوف والشعب في صفحة واحدة. يمكن تعديل المعلومات بحسب الصلاحيات.", 14));

        var summary = new WrapPanel();
        foreach (var (caption, value) in new (string, string)[]
        {
            ("رمز المدرسة", school.Code), ("الموقع / المنطقة", school.Zone),
            ("المرحلة التعليمية", school.Stage), ("نوع المدرسة", school.Gender),
            ("التبعية", school.Ownership), ("الحالة", school.IsActive ? "نشطة" : "غير نشطة"),
            ("مدير المدرسة", details.PrincipalName), ("رقم التواصل", details.ContactPhone),
            ("العنوان التفصيلي", details.Address),
            ("سنة التأسيس", details.FoundedYear?.ToString(CultureInfo.InvariantCulture) ?? "")
        }) summary.Children.Add(ProfileTile(caption, value));
        root.Children.Add(Box(summary));

        var basic = Stack();
        basic.Children.Add(Label("تعديل بيانات المدرسة الأساسية", 18, true));
        var canEditAll = AccessPolicy.CanEditSchool(_actor);
        var canEditClassification = canEditAll || AccessPolicy.CanEditSchoolProfile(_actor, school.Id);
        var code = Field(school.Code, 155);
        var name = Field(school.Name, 295);
        var zone = Field(school.Zone, 210);
        var ownership = Choice(new[] { "حكومية", "أهلية" }, 155);
        ownership.SelectedItem = school.Ownership;
        var stage = Choice(new[] { "روضة", "أساسي", "ثانوي", "مجمع تربوي" }, 180);
        stage.SelectedItem = school.Stage;
        var gender = Choice(new[] { "ذكور", "إناث", "مختلطة" }, 150);
        gender.SelectedItem = school.Gender;
        var active = new CheckBox { Content = "المدرسة نشطة", IsChecked = school.IsActive,
            Margin = new Thickness(9), IsEnabled = canEditAll };
        code.IsReadOnly = name.IsReadOnly = zone.IsReadOnly = !canEditAll;
        ownership.IsEnabled = canEditAll;
        stage.IsEnabled = gender.IsEnabled = canEditClassification;
        basic.Children.Add(LineOfFields(("الرمز", code), ("اسم المدرسة", name),
            ("المنطقة", zone), ("الملكية", ownership),
            ("المرحلة", stage), ("النوع", gender)));
        basic.Children.Add(active);
        basic.Children.Add(Btn("حفظ بيانات المدرسة الأساسية", (_, _) => Safe(() =>
        {
            if (canEditAll)
            {
                _store.UpdateSchool(_actor, school with
                {
                    Code = code.Text, Name = name.Text, Zone = zone.Text,
                    Ownership = ownership.SelectedItem?.ToString() ?? "",
                    Stage = stage.SelectedItem?.ToString() ?? "",
                    Gender = gender.SelectedItem?.ToString() ?? "",
                    IsActive = active.IsChecked == true
                });
            }
            else
            {
                _store.UpdateOwnSchoolProfile(_actor, school.Id,
                    stage.SelectedItem?.ToString() ?? "",
                    gender.SelectedItem?.ToString() ?? "");
            }
            Info("تم حفظ بيانات المدرسة."); ShowSchoolProfile(schoolId);
        }), canEditClassification, "success"));
        root.Children.Add(Box(basic));

        var info = Stack();
        info.Children.Add(Label("المعلومات الإدارية والتواصل", 18, true));
        var principal = Field(details.PrincipalName, 265);
        var phone = Field(details.ContactPhone, 180);
        var address = Field(details.Address, 350);
        address.MaxLength = 300;
        var founded = Field(details.FoundedYear?.ToString(CultureInfo.InvariantCulture), 135);
        founded.MaxLength = 4;
        info.Children.Add(LineOfFields(("مدير المدرسة", principal),
            ("هاتف المدرسة", phone), ("العنوان التفصيلي", address),
            ("سنة التأسيس", founded)));
        info.Children.Add(Label("ملاحظات الإدارة والمباني والتجهيزات", 14, true));
        var notes = ProfileNotes(details.Notes);
        info.Children.Add(notes);
        var canEditDetails = canEditClassification;
        principal.IsReadOnly = phone.IsReadOnly = address.IsReadOnly = founded.IsReadOnly =
            notes.IsReadOnly = !canEditDetails;
        info.Children.Add(Btn("حفظ المعلومات الإدارية", (_, _) => Safe(() =>
        {
            _store.SaveSchoolDirectoryDetails(_actor, school.Id,
                new SchoolDirectoryDetails(principal.Text, phone.Text, address.Text,
                    OptionalProfileYear(founded.Text, "التأسيس"), notes.Text));
            Info("تم حفظ معلومات المدرسة دون التأثير في الطلاب أو الكادر.");
            ShowSchoolProfile(schoolId);
        }), canEditDetails, "success"));
        root.Children.Add(Box(info));

        // The council is a first-class school record; the same school key drives
        // the directory detail and the live district counters.
        AddCouncilEditor(root, school);

        if (AccessPolicy.CanSeeStudents(_actor, schoolId))
        {
            var students = _store.GetStudents(_actor, schoolId);
            var grades = _store.GetSchoolGrades(_actor, schoolId);
            var pupils = students.Where(s => s.IsActive).GroupBy(s => (s.Grade, s.Section))
                .ToDictionary(g => g.Key, g => g.Count());
            var summaryGrades = grades.Select(g => new
            {
                ClassName = g.DisplayName,
                Count = pupils.GetValueOrDefault((g.Grade, g.Section))
            }).ToArray();
            var section = Stack();
            section.Children.Add(Label("كشوف الصفوف والشعب", 18, true));
            section.Children.Add(Label($"الطلاب النشطون في العام الحالي: {students.Count(s => s.IsActive)}، عدد الكشوف والشعب: {grades.Count}.", 14));
            section.Children.Add(GridFor(summaryGrades, ("الصف والشعبة", "ClassName", false),
                ("عدد الطلاب", "Count", false)));
            section.Children.Add(Btn("فتح كشوف الطلاب وملفاتهم", (_, _) =>
                ShowStudentsCore(schoolId), tone: "secondary"));
            root.Children.Add(Box(section));
        }

        if (AccessPolicy.CanSeeStaff(_actor, schoolId))
        {
            var staff = _store.GetSchoolStaff(_actor, schoolId);
            var section = Stack();
            section.Children.Add(Label("الكادر المدرسي — انقر مرتين لفتح الملف الوظيفي", 18, true));
            section.Children.Add(Label($"المعلمون النشطون: {staff.Count(s => s.IsActive && s.Category == "معلم")} | " +
                $"الإداريون النشطون: {staff.Count(s => s.IsActive && s.Category == "إداري")} | " +
                $"الأخصائيون النشطون: {staff.Count(s => s.IsActive && s.Category == "أخصائي اجتماعي")}", 14));
            var grid = GridFor(staff,
                ("الاسم", "FullName", false), ("الرقم الوظيفي", "EmployeeCode", false),
                ("الفئة", "Category", false), ("المسمى", "JobTitle", false),
                ("المؤهل", "Qualification", false));
            grid.MaxHeight = 320;
            grid.MouseDoubleClick += (_, e) =>
            {
                if (IsDoubleClickOnGridRow(e.OriginalSource, grid) &&
                    grid.SelectedItem is SchoolStaff item)
                    Safe(() => ShowStaffProfile(schoolId, item.Id));
            };
            section.Children.Add(grid);
            var canEditStaff = AccessPolicy.CanEditStaff(_actor, schoolId);
            section.Children.Add(Btn("حذف الموظف من الكشوف (أرشفة)", (_, _) => Safe(() =>
            {
                if (grid.SelectedItem is not SchoolStaff target)
                    throw new InvalidOperationException("حدد الموظف المطلوب أرشفته.");
                if (MessageBox.Show("سيُستبعد من الأعداد المباشرة مع الحفاظ على ملفه وسجله الوظيفي. هل تؤكد؟",
                    "تأكيد أرشفة الموظف", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _store.SaveSchoolStaff(_actor, target with { IsActive = false });
                ShowSchoolProfile(school.Id);
            }), canEditStaff, "danger"));
            section.Children.Add(Btn("استعادة الموظف المؤرشف", (_, _) => Safe(() =>
            {
                if (grid.SelectedItem is not SchoolStaff target)
                    throw new InvalidOperationException("حدد الموظف المراد استعادته.");
                _store.SaveSchoolStaff(_actor, target with { IsActive = true });
                ShowSchoolProfile(school.Id);
            }), canEditStaff, "secondary"));
            section.Children.Add(Btn("فتح كادر المدرسة وإضافة موظفين", (_, _) =>
                ShowStaffCore(schoolId), tone: "secondary"));
            root.Children.Add(Box(section));
            AddQuickStaffEditor(root, school);
        }
        Display("ملف المدرسة", root);
    }

    private void ShowStaffProfile(string schoolId, string staffId)
    {
        var school = _store.GetSchools(_actor).FirstOrDefault(s => s.Id == schoolId)
            ?? throw new UnauthorizedAccessException("لا يمكنك فتح ملفات كادر هذه المدرسة.");
        var person = _store.GetSchoolStaffMember(_actor, schoolId, staffId);
        var details = _store.GetStaffPersonnel(_actor, schoolId, staffId);
        var root = Stack();
        root.Children.Add(Btn("العودة إلى سجل الكادر", (_, _) =>
            ShowStaffCore(schoolId), tone: "secondary"));
        root.Children.Add(Label($"ملف {person.Category} — {person.FullName}", 22, true));
        root.Children.Add(Label("بيانات المعلم أو الإداري الأساسية، تبعيته، مؤهله، وضعه الوظيفي والجهة التي يباشر فيها عمله.", 14));

        var currentWorkplace = details.AssignmentStatus is ("منتدب" or "مكلف" or "معار")
            ? details.ActualWorkplace : string.IsNullOrWhiteSpace(details.ActualWorkplace)
                ? school.Name : details.ActualWorkplace;
        var overview = new WrapPanel();
        foreach (var (caption, value) in new (string, string)[]
        {
            ("الاسم الكامل", person.FullName), ("الرقم الوظيفي", person.EmployeeCode),
            ("الفئة", person.Category), ("المسمى الوظيفي", person.JobTitle),
            ("المدرسة المسجل في كشوفاتها", school.Name),
            ("التخصص", person.Specialization), ("المؤهل العلمي", person.Qualification),
            ("سنة المؤهل", details.QualificationYear?.ToString(CultureInfo.InvariantCulture) ?? ""),
            ("نوع العمل", details.EmploymentType),
            ("سنة التوظيف", details.EmploymentYear?.ToString(CultureInfo.InvariantCulture) ?? ""),
            ("سنة التعاقد", details.ContractYear?.ToString(CultureInfo.InvariantCulture) ?? ""),
            ("وضع العمل", details.AssignmentStatus),
            ("جهة العمل الفعلية", currentWorkplace),
            ("بداية الانتداب أو التكليف", details.AssignmentYear?.ToString(CultureInfo.InvariantCulture) ?? ""),
            ("الهاتف", details.ContactPhone),
            ("القيد", person.IsActive ? "ضمن كشوف الكادر" : "سجل غير نشط")
        }) overview.Children.Add(ProfileTile(caption, value));
        root.Children.Add(Box(overview));

        var canEdit = AccessPolicy.CanEditStaff(_actor, schoolId);
        var core = Stack();
        core.Children.Add(Label("تصحيح السجل الأساسي للمعلم أو الإداري", 18, true));
        var code = Field(person.EmployeeCode, 160);
        var name = Field(person.FullName, 290);
        var category = Choice(new[] { "معلم", "إداري", "أخصائي اجتماعي" }, 175);
        category.SelectedItem = person.Category;
        var title = Field(person.JobTitle, 230);
        var specialist = Field(person.Specialization, 230);
        var qualification = Field(person.Qualification, 220);
        var gender = Choice(new[] { "ذكور", "إناث", "غير محدد" }, 150);
        gender.SelectedItem = person.Gender;
        var active = new CheckBox
        {
            Content = "السجل نشط في كشوفات المدرسة",
            IsChecked = person.IsActive, Margin = new Thickness(10)
        };
        core.Children.Add(LineOfFields(("الرقم الوظيفي", code), ("الاسم", name),
            ("الفئة", category), ("المسمى", title), ("التخصص", specialist),
            ("المؤهل", qualification), ("الجنس", gender)));
        core.Children.Add(active);
        code.IsReadOnly = name.IsReadOnly = title.IsReadOnly = specialist.IsReadOnly =
            qualification.IsReadOnly = !canEdit;
        category.IsEnabled = gender.IsEnabled = active.IsEnabled = canEdit;
        core.Children.Add(Btn("حفظ تعديل البيانات الأساسية", (_, _) => Safe(() =>
        {
            _store.SaveSchoolStaff(_actor, person with
            {
                EmployeeCode = code.Text, FullName = name.Text,
                Category = category.SelectedItem?.ToString() ?? "",
                JobTitle = title.Text, Specialization = specialist.Text,
                Qualification = qualification.Text, Gender = gender.SelectedItem?.ToString() ?? "",
                IsActive = active.IsChecked == true
            });
            Info("تم حفظ بيانات المعلم أو الإداري."); ShowStaffProfile(schoolId, staffId);
        }), canEdit, "success"));
        root.Children.Add(Box(core));

        var personnel = Stack();
        personnel.Children.Add(Label("المعلومات الوظيفية والتعاقد والانتداب", 18, true));
        personnel.Children.Add(Label(
            "المدرسة المسجل فيها الموظف لا تتغير عند انتدابه لدائرة أخرى. اختر وضع العمل، ثم اكتب الجهة التي يعمل لديها فعليا. لا تفترض سنة توظيف للسجلات القديمة.", 13));
        var employment = Choice(new[] { "غير محدد", "موظف", "متعاقد" }, 155);
        employment.SelectedItem = details.EmploymentType;
        var hireYear = Field(details.EmploymentYear?.ToString(CultureInfo.InvariantCulture), 140);
        var contractYear = Field(details.ContractYear?.ToString(CultureInfo.InvariantCulture), 140);
        var qualificationYear = Field(details.QualificationYear?.ToString(CultureInfo.InvariantCulture), 140);
        var status = Choice(new[] { "غير محدد", "مباشر", "منتدب", "مكلف", "معار", "إجازة" }, 160);
        status.SelectedItem = details.AssignmentStatus;
        var actualWorkplace = Field(details.ActualWorkplace, 330);
        var assignmentYear = Field(details.AssignmentYear?.ToString(CultureInfo.InvariantCulture), 160);
        var phone = Field(details.ContactPhone, 175);
        hireYear.MaxLength = contractYear.MaxLength = qualificationYear.MaxLength =
            assignmentYear.MaxLength = 4;
        actualWorkplace.MaxLength = 250;
        phone.MaxLength = 40;
        personnel.Children.Add(LineOfFields(("الصفة الوظيفية", employment),
            ("سنة التوظيف", hireYear), ("سنة التعاقد", contractYear),
            ("سنة الحصول على المؤهل", qualificationYear),
            ("الوضع الوظيفي الحالي", status),
            ("المدرسة أو الدائرة المنتدب / المكلف إليها", actualWorkplace),
            ("سنة بدء الانتداب / التكليف", assignmentYear),
            ("هاتف الموظف", phone)));
        personnel.Children.Add(Label("الملاحظات الوظيفية أو وثائق الانتداب (وصف نصي)", 14, true));
        var notes = ProfileNotes(details.Notes);
        personnel.Children.Add(notes);
        employment.IsEnabled = status.IsEnabled = canEdit;
        qualificationYear.IsReadOnly = assignmentYear.IsReadOnly = actualWorkplace.IsReadOnly =
            phone.IsReadOnly = notes.IsReadOnly = !canEdit;
        void RefreshEmploymentFields()
        {
            hireYear.IsEnabled = canEdit && employment.SelectedItem?.ToString() == "موظف";
            contractYear.IsEnabled = canEdit && employment.SelectedItem?.ToString() == "متعاقد";
        }
        employment.SelectionChanged += (_, _) => RefreshEmploymentFields();
        RefreshEmploymentFields();
        personnel.Children.Add(Btn("حفظ المعلومات الوظيفية والانتداب", (_, _) => Safe(() =>
        {
            var type = employment.SelectedItem?.ToString() ?? "غير محدد";
            var selectedStatus = status.SelectedItem?.ToString() ?? "غير محدد";
            _store.SaveStaffPersonnel(_actor, schoolId, staffId,
                new StaffPersonnelDetails(type,
                    type == "موظف" ? OptionalProfileYear(hireYear.Text, "التوظيف") : null,
                    type == "متعاقد" ? OptionalProfileYear(contractYear.Text, "التعاقد") : null,
                    OptionalProfileYear(qualificationYear.Text, "المؤهل"), selectedStatus,
                    actualWorkplace.Text, OptionalProfileYear(assignmentYear.Text, "الانتداب"),
                    phone.Text, notes.Text));
            Info("تم حفظ الوضع الوظيفي والتبعية الفعلية دون نقل السجل إلى مدرسة أخرى.");
            ShowStaffProfile(schoolId, staffId);
        }), canEdit, "success"));
        root.Children.Add(Box(personnel));
        Display("ملف الموظف", root);
    }
}
